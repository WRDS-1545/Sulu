using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Sulu.Infrastructure;

namespace Sulu.Core;

/// <summary>
/// Finds REG_SZ/REG_EXPAND_SZ values that contain the old application directory as
/// a complete path. The scan is intentionally broad, like the original PowerShell
/// script, and classifies results into safe app-owned references and values that
/// need manual review.
/// </summary>
internal static class RegistryReferenceScanner
{
    private const int MaxWarnings = 8;

    private const string UninstallPrefix =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private const string AppPathsPrefix =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

    private const string RunKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private const string RunOnceKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    public static MigrationScanResult Scan(InstalledApp app, string oldPath, string newPath)
    {
        var pattern = BuildPattern(oldPath);
        var references = new List<RegistryValueReference>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentUserSid = GetCurrentUserSid(warnings);
        var keysScanned = 0;
        var valuesScanned = 0;
        var errors = 0;
        var rowId = 1;

        foreach (var hive in RegistryHiveRef.All)
        {
            if (hive.Name == "HKU" && currentUserSid is null)
            {
                continue;
            }

            try
            {
                using var baseKey = hive.OpenBaseKey();
                if (hive.Name == "HKU")
                {
                    ScanUsersHive(
                        baseKey,
                        hive,
                        currentUserSid!,
                        pattern,
                        newPath,
                        app,
                        references,
                        seen,
                        warnings,
                        ref keysScanned,
                        ref valuesScanned,
                        ref errors,
                        ref rowId);
                }
                else
                {
                    WalkKey(
                        baseKey,
                        hive,
                        string.Empty,
                        pattern,
                        newPath,
                        app,
                        references,
                        seen,
                        warnings,
                        ref keysScanned,
                        ref valuesScanned,
                        ref errors,
                        ref rowId);
                }
            }
            catch (Exception ex) when (IsRegistryReadFailure(ex))
            {
                AddError(warnings, ref errors, $"Could not scan {hive.Name} ({hive.ViewName}): {ex.Message}");
            }
            catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
            {
                // Some hive/view combinations are unavailable on older systems.
            }
        }

        return new MigrationScanResult(
            references
                .OrderBy(reference => reference.Safe ? 0 : 1)
                .ThenBy(reference => reference.Hive.Name, StringComparer.Ordinal)
                .ThenBy(reference => reference.Hive.ViewName, StringComparer.Ordinal)
                .ThenBy(reference => reference.SubKey, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(reference => reference.ValueName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            warnings,
            keysScanned,
            valuesScanned,
            errors);
    }

    private static void ScanUsersHive(
        RegistryKey baseKey,
        RegistryHiveRef hive,
        string currentUserSid,
        Regex pattern,
        string newPath,
        InstalledApp app,
        List<RegistryValueReference> references,
        HashSet<string> seen,
        List<string> warnings,
        ref int keysScanned,
        ref int valuesScanned,
        ref int errors,
        ref int rowId)
    {
        string[] sidNames;
        try
        {
            sidNames = baseKey.GetSubKeyNames();
        }
        catch (Exception ex) when (IsRegistryReadFailure(ex))
        {
            AddError(warnings, ref errors, $"Could not enumerate HKU: {ex.Message}");
            return;
        }

        foreach (var sid in sidNames)
        {
            if (sid.Equals(currentUserSid, StringComparison.OrdinalIgnoreCase)
                || sid.Equals($"{currentUserSid}_Classes", StringComparison.OrdinalIgnoreCase))
            {
                // HKCU is scanned separately; scanning it again through HKU would
                // produce duplicate references to the same underlying values.
                continue;
            }

            RegistryKey? userKey = null;
            try
            {
                userKey = baseKey.OpenSubKey(sid, writable: false);
                if (userKey is null)
                {
                    continue;
                }

                WalkKey(
                    userKey,
                    hive,
                    sid,
                    pattern,
                    newPath,
                    app,
                    references,
                    seen,
                    warnings,
                    ref keysScanned,
                    ref valuesScanned,
                    ref errors,
                    ref rowId);
            }
            catch (Exception ex) when (IsRegistryReadFailure(ex))
            {
                AddError(warnings, ref errors, $"Could not scan HKU\\{sid}: {ex.Message}");
            }
            finally
            {
                userKey?.Dispose();
            }
        }
    }

    private static void WalkKey(
        RegistryKey key,
        RegistryHiveRef hive,
        string subKeyPath,
        Regex pattern,
        string newPath,
        InstalledApp app,
        List<RegistryValueReference> references,
        HashSet<string> seen,
        List<string> warnings,
        ref int keysScanned,
        ref int valuesScanned,
        ref int errors,
        ref int rowId)
    {
        keysScanned++;
        string[] valueNames;
        try
        {
            valueNames = key.GetValueNames();
        }
        catch (Exception ex) when (IsRegistryReadFailure(ex))
        {
            errors++;
            valueNames = [];
        }

        foreach (var valueName in valueNames)
        {
            RegistryValueKind kind;
            try
            {
                kind = key.GetValueKind(valueName);
            }
            catch (Exception ex) when (IsRegistryReadFailure(ex) || ex is IOException)
            {
                continue;
            }

            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            {
                continue;
            }

            valuesScanned++;
            string data;
            try
            {
                data = key.GetValue(
                    valueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
            }
            catch (Exception ex) when (IsRegistryReadFailure(ex))
            {
                continue;
            }

            if (data.Length == 0)
            {
                continue;
            }

            try
            {
                if (!pattern.IsMatch(data))
                {
                    continue;
                }

                var proposed = pattern.Replace(data, _ => newPath);
                if (proposed.Equals(data, StringComparison.Ordinal))
                {
                    continue;
                }

                var identity = $"{hive.Name}|{hive.ViewName}|{subKeyPath}|{valueName}";
                if (!seen.Add(identity))
                {
                    continue;
                }

                var classification = Classify(app, hive, subKeyPath);
                references.Add(new RegistryValueReference
                {
                    RowId = rowId++,
                    Hive = hive,
                    SubKey = subKeyPath,
                    ValueName = valueName,
                    Kind = kind,
                    CurrentValue = data,
                    ProposedValue = proposed,
                    Safe = classification.Safe,
                    ServiceReference = classification.Service,
                    Reason = classification.Reason,
                });
            }
            catch (RegexMatchTimeoutException)
            {
                AddError(
                    warnings,
                    ref errors,
                    $"A registry value under {hive.Name}\\{subKeyPath} was too complex to scan.");
            }
        }

        string[] childNames;
        try
        {
            childNames = key.GetSubKeyNames();
        }
        catch (Exception ex) when (IsRegistryReadFailure(ex))
        {
            errors++;
            return;
        }

        foreach (var childName in childNames)
        {
            if (hive.ViewName.Equals("Registry64", StringComparison.OrdinalIgnoreCase)
                && childName.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
            {
                // The Registry32 view exposes the same physical subtree with the
                // normal path. Skipping the 64-bit alias prevents duplicate edits.
                continue;
            }

            RegistryKey? child = null;
            try
            {
                child = key.OpenSubKey(childName, writable: false);
                if (child is not null)
                {
                    var childPath = string.IsNullOrEmpty(subKeyPath)
                        ? childName
                        : $"{subKeyPath}\\{childName}";
                    WalkKey(
                        child,
                        hive,
                        childPath,
                        pattern,
                        newPath,
                        app,
                        references,
                        seen,
                        warnings,
                        ref keysScanned,
                        ref valuesScanned,
                        ref errors,
                        ref rowId);
                }
            }
            catch (Exception ex) when (IsRegistryReadFailure(ex))
            {
                errors++;
            }
            finally
            {
                child?.Dispose();
            }
        }
    }

    private static (bool Safe, bool Service, string Reason) Classify(
        InstalledApp app,
        RegistryHiveRef hive,
        string subKey)
    {
        var normalized = subKey.Trim('\\');
        if (IsOwnUninstallEntry(app, hive, normalized))
        {
            return (true, false, "The application's own uninstall entry.");
        }

        var appPathsPath = normalized.StartsWith(@"SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase)
            ? normalized[@"SOFTWARE\WOW6432Node\".Length..]
            : normalized;
        if (appPathsPath.StartsWith(AppPathsPrefix, StringComparison.OrdinalIgnoreCase)
            && appPathsPath.Length > AppPathsPrefix.Length)
        {
            return (true, false, "App Paths entry that points inside the old install directory.");
        }

        if (normalized.Equals(RunKey, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(RunOnceKey, StringComparison.OrdinalIgnoreCase))
        {
            return (true, false, "Run/RunOnce startup value that points inside the old install directory.");
        }

        if (hive.Name == "HKLM"
            && normalized.StartsWith(@"SYSTEM\", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains(@"\Services\", StringComparison.OrdinalIgnoreCase))
        {
            return (
                false,
                true,
                "Service/driver reference. Stop or reconfigure the service before moving its files.");
        }

        if (normalized.StartsWith(UninstallPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, "Another uninstall entry references the old directory. Review manually.");
        }

        return (false, false, "General application setting. Review manually before approving it.");
    }

    private static bool IsOwnUninstallEntry(
        InstalledApp app,
        RegistryHiveRef hive,
        string subKey)
    {
        var expectedHive = app.Location.IsMachine ? "HKLM" : "HKCU";
        var expectedView = app.Location.Scope switch
        {
            RegistryScope.LocalMachine32 => "Registry32",
            _ => "Registry64",
        };

        return hive.Name.Equals(expectedHive, StringComparison.OrdinalIgnoreCase)
            && hive.ViewName.Equals(expectedView, StringComparison.OrdinalIgnoreCase)
            && subKey.Equals(app.Location.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static Regex BuildPattern(string oldPath)
    {
        var trimmed = oldPath.TrimEnd('\\');
        var text = $@"(?<![A-Za-z0-9_.-]){Regex.Escape(trimmed)}(?=\\|$|[""'\s;,\])}}])";
        return new Regex(
            text,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(3));
    }

    private static string? GetCurrentUserSid(List<string> warnings)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                warnings.Add("The current user SID could not be determined; HKU was skipped.");
            }

            return sid;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or SecurityException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            warnings.Add($"The current user SID could not be determined ({ex.Message}); HKU was skipped.");
            return null;
        }
    }

    private static void AddError(List<string> warnings, ref int errors, string message)
    {
        errors++;
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(message);
        }
    }

    private static bool IsRegistryReadFailure(Exception ex) =>
        ex is UnauthorizedAccessException
            or SecurityException
            or IOException
            or ObjectDisposedException
            or InvalidOperationException
            or PlatformNotSupportedException;
}
