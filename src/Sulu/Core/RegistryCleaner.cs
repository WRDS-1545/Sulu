using System.Security;
using Microsoft.Win32;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed record CleanupItemResult(
    RegistryFinding Finding,
    bool Deleted,
    string? Error);

internal sealed record CleanupResult(
    IReadOnlyList<CleanupItemResult> Items)
{
    public int DeletedCount => Items.Count(item => item.Deleted);

    public int FailedCount => Items.Count(item => !item.Deleted && item.Error is not null);

    public int SkippedCount => Items.Count(item => !item.Deleted && item.Error is null);
}

internal sealed class RegistryCleaner
{
    private const string UninstallPrefix =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private const string AppPathPrefix =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

    private static readonly HashSet<string> StartupKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
    };

    public static CleanupResult Clean(IReadOnlyList<RegistryFinding> findings, bool isElevated)
    {
        var results = new List<CleanupItemResult>(findings.Count);
        foreach (var finding in findings)
        {
            if (finding.Location.IsMachine && !isElevated)
            {
                results.Add(new CleanupItemResult(
                    finding,
                    false,
                    "Requires an elevated terminal."));
                continue;
            }

            if (!IsAllowedTarget(finding, out var validationError))
            {
                results.Add(new CleanupItemResult(finding, false, validationError));
                continue;
            }

            try
            {
                var deleted = finding.Kind == FindingKind.StartupValue
                    ? DeleteValue(finding)
                    : DeleteKey(finding.Location);

                results.Add(deleted
                    ? new CleanupItemResult(finding, true, null)
                    : new CleanupItemResult(finding, false, null));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                results.Add(new CleanupItemResult(finding, false, "Access denied."));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                results.Add(new CleanupItemResult(finding, false, PathTools.OneLine(ex.Message)));
            }
        }

        return new CleanupResult(results);
    }

    private static bool DeleteKey(RegistryLocation location)
    {
        using var baseKey = location.OpenBaseKey();
        using (var existing = baseKey.OpenSubKey(location.Path, writable: false))
        {
            if (existing is null)
            {
                return false;
            }
        }

        baseKey.DeleteSubKeyTree(location.Path, throwOnMissingSubKey: false);
        using var check = baseKey.OpenSubKey(location.Path, writable: false);
        return check is null;
    }

    private static bool DeleteValue(RegistryFinding finding)
    {
        using var baseKey = finding.Location.OpenBaseKey();
        using var key = baseKey.OpenSubKey(finding.Location.Path, writable: true);
        if (key is null || finding.ValueName is null)
        {
            return false;
        }

        if (!key.GetValueNames().Contains(finding.ValueName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        key.DeleteValue(finding.ValueName, throwOnMissingValue: false);
        return !key.GetValueNames().Contains(finding.ValueName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAllowedTarget(RegistryFinding finding, out string? error)
    {
        error = null;
        var path = finding.Location.Path;

        switch (finding.Kind)
        {
            case FindingKind.UninstallEntry:
                if (!IsStrictChild(path, UninstallPrefix))
                {
                    error = "Refused: target is not a child of the Uninstall registry root.";
                    return false;
                }

                break;

            case FindingKind.AppPath:
                if (!IsStrictChild(path, AppPathPrefix))
                {
                    error = "Refused: target is not a child of the App Paths registry root.";
                    return false;
                }

                break;

            case FindingKind.StartupValue:
                if (!StartupKeys.Contains(path) || string.IsNullOrWhiteSpace(finding.ValueName))
                {
                    error = "Refused: target is not a supported Run/RunOnce value.";
                    return false;
                }

                break;

            default:
                error = "Refused: unknown finding type.";
                return false;
        }

        return true;
    }

    private static bool IsStrictChild(string path, string prefix) =>
        path.Length > prefix.Length
        && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
