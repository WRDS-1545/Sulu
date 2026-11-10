using Sulu.Infrastructure;

namespace Sulu.Core;

internal enum FindingKind
{
    UninstallEntry,
    AppPath,
    StartupValue,
}

internal enum FindingRisk
{
    Low,
    Medium,
    High,
}

internal sealed record RegistryFinding
{
    public required string Id { get; init; }

    public required FindingKind Kind { get; init; }

    public required RegistryLocation Location { get; init; }

    public string? ValueName { get; init; }

    public required string Summary { get; init; }

    public string? Detail { get; init; }

    public required FindingRisk Risk { get; init; }

    public required bool AutoClean { get; init; }

    public string? Reason { get; init; }

    public bool RequiresElevation => Location.IsMachine;

    public string Category => Kind switch
    {
        FindingKind.UninstallEntry => "Uninstall entry",
        FindingKind.AppPath => "App Path",
        FindingKind.StartupValue => "Startup value",
        _ => "Registry",
    };

    public string Action => Kind switch
    {
        FindingKind.UninstallEntry => "Delete key",
        FindingKind.AppPath => "Delete key",
        FindingKind.StartupValue => "Delete value",
        _ => "Delete",
    };

    public string DisplayLocation => ValueName is null
        ? Location.DisplayPath
        : $"{Location.DisplayPath} :: {ValueName}";
}

internal sealed record RegistryScanResult(
    IReadOnlyList<RegistryFinding> Findings,
    IReadOnlyList<string> Warnings,
    bool AppStillInstalled);

internal sealed class RegistryScanner
{
    private static readonly RegistryLocation[] AppPathRoots =
    [
        new(RegistryScope.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        new(RegistryScope.LocalMachine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        new(RegistryScope.LocalMachine32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
    ];

    private static readonly RegistryLocation[] StartupRoots =
    [
        new(RegistryScope.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryScope.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryScope.LocalMachine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryScope.LocalMachine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryScope.LocalMachine32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryScope.LocalMachine32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    public static RegistryScanResult Scan(AppIdentity identity, bool assumeUninstalled = false)
    {
        var findings = new List<RegistryFinding>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appStillInstalled = !assumeUninstalled
            && RegistryCatalog.LoadAll().Any(app => identity.OriginalKeyPaths.Contains(app.Id));
        var safeInstallRoot = PathTools.IsSafeAppRoot(identity.InstallLocation);
        if (!string.IsNullOrWhiteSpace(identity.InstallLocation) && !safeInstallRoot)
        {
            warnings.Add(
                $"Install location '{PathTools.OneLine(identity.InstallLocation)}' is a broad shared directory; "
                + "App Paths and startup matching was skipped.");
        }

        ScanUninstallEntries(identity, safeInstallRoot, findings, seen);
        ScanAppPaths(identity, appStillInstalled, safeInstallRoot, findings, warnings, seen);
        ScanStartupValues(identity, appStillInstalled, safeInstallRoot, findings, warnings, seen);
        return new RegistryScanResult(
            findings
                .OrderBy(finding => finding.Kind)
                .ThenBy(finding => finding.Location.Code, StringComparer.Ordinal)
                .ThenBy(finding => finding.Summary, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            warnings,
            appStillInstalled);
    }

    private static void ScanUninstallEntries(
        AppIdentity identity,
        bool safeInstallRoot,
        List<RegistryFinding> findings,
        HashSet<string> seen)
    {
        foreach (var app in RegistryCatalog.LoadAll())
        {
            var isOriginal = identity.OriginalKeyPaths.Contains(app.Id);
            var productCodeMatch = identity.ProductCode is not null
                && app.ProductCode is not null
                && NormalizeProductCode(identity.ProductCode).Equals(
                    NormalizeProductCode(app.ProductCode),
                    StringComparison.OrdinalIgnoreCase);

            var installLocationMatch = !string.IsNullOrWhiteSpace(identity.InstallLocation)
                && PathTools.IsSamePath(app.InstallLocation, identity.InstallLocation);

            var nameMatch = app.Name.Equals(identity.Name, StringComparison.OrdinalIgnoreCase);
            var publisherMatch = NormalizeText(app.Publisher).Equals(
                NormalizeText(identity.Publisher),
                StringComparison.OrdinalIgnoreCase);

            if (!isOriginal && !productCodeMatch && !(nameMatch && installLocationMatch) && !nameMatch)
            {
                continue;
            }

            string reason;
            bool autoClean;
            FindingRisk risk;

            if (isOriginal)
            {
                reason = "The original uninstall entry still exists. The uninstaller may not have finished; it was left untouched.";
                autoClean = false;
                risk = FindingRisk.High;
            }
            else if (productCodeMatch)
            {
                reason = "Same MSI product code as the uninstalled app.";
                autoClean = true;
                risk = FindingRisk.Medium;
            }
            else if (nameMatch && installLocationMatch && safeInstallRoot)
            {
                reason = "Same display name and install location as the uninstalled app.";
                autoClean = true;
                risk = FindingRisk.Medium;
            }
            else if (nameMatch && publisherMatch)
            {
                reason = "Same display name and publisher, but different product details. Review manually.";
                autoClean = false;
                risk = FindingRisk.High;
            }
            else
            {
                reason = "Same display name but different publisher or install location. Review manually.";
                autoClean = false;
                risk = FindingRisk.High;
            }

            var detail = string.Join(
                "; ",
                new[]
                {
                    $"Name={app.Name}",
                    $"Version={app.DisplayVersion}",
                    $"Publisher={app.DisplayPublisher}",
                    $"InstallLocation={app.DisplayInstallLocation}",
                });

            AddFinding(new RegistryFinding
            {
                Id = app.Id,
                Kind = FindingKind.UninstallEntry,
                Location = app.Location,
                Summary = app.Name,
                Detail = detail,
                Risk = risk,
                AutoClean = autoClean,
                Reason = reason,
            });
        }

        void AddFinding(RegistryFinding finding)
        {
            if (seen.Add(finding.Id))
            {
                findings.Add(finding);
            }
        }
    }

    private static void ScanAppPaths(
        AppIdentity identity,
        bool appStillInstalled,
        bool safeInstallRoot,
        List<RegistryFinding> findings,
        List<string> warnings,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(identity.InstallLocation) || !safeInstallRoot)
        {
            return;
        }

        foreach (var root in AppPathRoots)
        {
            try
            {
                using var baseKey = root.OpenBaseKey();
                using var appPaths = baseKey.OpenSubKey(root.Path, writable: false);
                if (appPaths is null)
                {
                    continue;
                }

                foreach (var subKeyName in appPaths.GetSubKeyNames())
                {
                    using var appKey = appPaths.OpenSubKey(subKeyName, writable: false);
                    if (appKey is null)
                    {
                        continue;
                    }

                    var raw = appKey.GetValue(null) as string;
                    var executable = PathTools.ExtractAppPathExecutable(raw);
                    if (string.IsNullOrWhiteSpace(executable)
                        || !PathTools.IsSameOrUnder(executable, identity.InstallLocation))
                    {
                        continue;
                    }

                    var location = new RegistryLocation(root.Scope, $"{root.Path}\\{subKeyName}");
                    var finding = new RegistryFinding
                    {
                        Id = location.Id,
                        Kind = FindingKind.AppPath,
                        Location = location,
                        Summary = subKeyName,
                        Detail = executable,
                        Risk = appStillInstalled ? FindingRisk.High : FindingRisk.Low,
                        AutoClean = !appStillInstalled,
                        Reason = appStillInstalled
                            ? "The app's uninstall entry is still present; this may be a live setting, so it is not auto-cleaned."
                            : "App Paths entry points inside the uninstalled app's install location.",
                    };

                    if (seen.Add(finding.Id))
                    {
                        findings.Add(finding);
                    }
                }
            }
            catch (Exception ex) when (IsRegistryReadFailure(ex))
            {
                warnings.Add($"Could not read App Paths at {root.DisplayPath}: {ex.Message}");
            }
        }
    }

    private static void ScanStartupValues(
        AppIdentity identity,
        bool appStillInstalled,
        bool safeInstallRoot,
        List<RegistryFinding> findings,
        List<string> warnings,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(identity.InstallLocation) || !safeInstallRoot)
        {
            return;
        }

        foreach (var root in StartupRoots)
        {
            try
            {
                using var baseKey = root.OpenBaseKey();
                using var runKey = baseKey.OpenSubKey(root.Path, writable: false);
                if (runKey is null)
                {
                    continue;
                }

                foreach (var valueName in runKey.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(valueName))
                    {
                        continue;
                    }

                    var command = runKey.GetValue(valueName) as string;
                    if (!PathTools.CommandReferencesDirectory(command, identity.InstallLocation))
                    {
                        continue;
                    }

                    var id = $"{root.Id}|value:{valueName}";
                    var finding = new RegistryFinding
                    {
                        Id = id,
                        Kind = FindingKind.StartupValue,
                        Location = root,
                        ValueName = valueName,
                        Summary = valueName,
                        Detail = PathTools.OneLine(command),
                        Risk = appStillInstalled ? FindingRisk.High : FindingRisk.Low,
                        AutoClean = !appStillInstalled,
                        Reason = appStillInstalled
                            ? "The app's uninstall entry is still present; this may be a live setting, so it is not auto-cleaned."
                            : "Startup command points inside the uninstalled app's install location.",
                    };

                    if (seen.Add(finding.Id))
                    {
                        findings.Add(finding);
                    }
                }
            }
            catch (Exception ex) when (IsRegistryReadFailure(ex))
            {
                warnings.Add($"Could not read startup key at {root.DisplayPath}: {ex.Message}");
            }
        }
    }

    private static string NormalizeProductCode(string value) => value.Trim().Trim('{', '}').ToUpperInvariant();

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;

    internal static bool IsRegistryReadFailure(Exception ex) =>
        ex is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ObjectDisposedException
            or PlatformNotSupportedException;
}
