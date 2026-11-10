using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed class RegistryCatalog
{
    private static readonly RegistryLocation[] UninstallRoots =
    [
        RegistryLocation.ForUninstallRoot(RegistryScope.CurrentUser),
        RegistryLocation.ForUninstallRoot(RegistryScope.LocalMachine64),
        RegistryLocation.ForUninstallRoot(RegistryScope.LocalMachine32),
    ];

    private static readonly Regex GuidPattern = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public static IReadOnlyList<InstalledApp> LoadAll()
    {
        var apps = new List<InstalledApp>();
        foreach (var root in UninstallRoots)
        {
            LoadRoot(root, apps);
        }

        return apps
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Location.Code, StringComparer.Ordinal)
            .ToList();
    }

    public static bool Exists(RegistryLocation location)
    {
        try
        {
            using var baseKey = location.OpenBaseKey();
            using var key = baseKey.OpenSubKey(location.Path, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (IsRegistryReadFailure(ex))
        {
            // Conservatively treat an unreadable key as present.
            return true;
        }
    }

    public static InstalledApp? FindById(IReadOnlyList<InstalledApp> apps, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var normalized = id.Trim();
        return apps.FirstOrDefault(
            app => app.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<InstalledApp> Search(IReadOnlyList<InstalledApp> apps, string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var exact = apps.Where(app =>
                app.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                || app.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                || KeyNameMatches(app, trimmed)
                || ProductCodeMatches(app, trimmed))
            .ToList();

        if (exact.Count > 0)
        {
            return exact;
        }

        return apps.Where(app =>
                app.Name.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase)
                || (app.Publisher?.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || app.Location.Path.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool KeyNameMatches(InstalledApp app, string query) =>
        app.KeyName.Equals(query.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)
        || app.KeyName.Equals(query, StringComparison.OrdinalIgnoreCase);

    private static bool ProductCodeMatches(InstalledApp app, string query) =>
        app.ProductCode is not null
        && NormalizeProductCode(app.ProductCode).Equals(
            NormalizeProductCode(query),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeProductCode(string value) => value.Trim().Trim('{', '}').ToUpperInvariant();

    private static void LoadRoot(RegistryLocation location, List<InstalledApp> apps)
    {
        try
        {
            using var baseKey = location.OpenBaseKey();
            using var root = baseKey.OpenSubKey(location.Path, writable: false);
            if (root is null)
            {
                return;
            }

            foreach (var keyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(keyName, writable: false);
                    if (key is null)
                    {
                        continue;
                    }

                    var app = ReadApp(location, keyName, key);
                    if (app is not null)
                    {
                        apps.Add(app);
                    }
                }
                catch (Exception ex) when (IsRegistryReadFailure(ex))
                {
                    // A single unreadable key must not hide the rest of the catalog.
                }
            }
        }
        catch (Exception ex) when (IsRegistryReadFailure(ex))
        {
            // Missing or inaccessible roots (for example a 32-bit view on a very
            // old system) are not fatal.
        }
    }

    private static InstalledApp? ReadApp(RegistryLocation root, string keyName, RegistryKey key)
    {
        var displayName = ReadString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var uninstallString = ReadString(key, "UninstallString");
        var quietUninstallString = ReadString(key, "QuietUninstallString");
        var releaseType = ReadString(key, "ReleaseType");
        var parentKeyName = ReadString(key, "ParentKeyName");
        var systemComponent = PathTools.ParseInt(key.GetValue("SystemComponent")) == 1;
        var noRemove = PathTools.ParseInt(key.GetValue("NoRemove")) == 1;
        var windowsInstaller = PathTools.ParseInt(key.GetValue("WindowsInstaller")) == 1;
        var isMsi = windowsInstaller
            || (uninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ?? false);
        var hidden = systemComponent
            || !string.IsNullOrWhiteSpace(parentKeyName)
            || IsUpdateReleaseType(releaseType);

        var productCode = ReadProductCode(keyName, uninstallString);
        var location = new RegistryLocation(root.Scope, $"{root.Path}\\{keyName}");

        return new InstalledApp
        {
            Name = PathTools.OneLine(displayName),
            Location = location,
            KeyName = keyName,
            Version = ReadString(key, "DisplayVersion"),
            Publisher = ReadString(key, "Publisher"),
            InstallLocation = ReadString(key, "InstallLocation"),
            UninstallString = uninstallString,
            QuietUninstallString = quietUninstallString,
            ProductCode = productCode,
            InstallDate = ReadString(key, "InstallDate"),
            EstimatedSizeBytes = PathTools.ParseEstimatedSize(key.GetValue("EstimatedSize")),
            IsHidden = hidden,
            NoRemove = noRemove,
            IsMsi = isMsi,
        };
    }

    private static string? ReadProductCode(string keyName, string? uninstallString)
    {
        if (Guid.TryParse(keyName.Trim('{', '}'), out var keyGuid))
        {
            return $"{{{keyGuid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant()}}}";
        }

        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        var match = GuidPattern.Match(uninstallString);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        if (value is null)
        {
            return null;
        }

        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : PathTools.OneLine(text);
    }

    private static bool IsUpdateReleaseType(string? releaseType)
    {
        if (string.IsNullOrWhiteSpace(releaseType))
        {
            return false;
        }

        return releaseType.Contains("update", StringComparison.OrdinalIgnoreCase)
            || releaseType.Contains("hotfix", StringComparison.OrdinalIgnoreCase)
            || releaseType.Contains("service pack", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistryReadFailure(Exception ex) =>
        ex is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ObjectDisposedException
            or PlatformNotSupportedException;
}
