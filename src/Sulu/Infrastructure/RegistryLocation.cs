using Microsoft.Win32;

namespace Sulu.Infrastructure;

internal enum RegistryScope
{
    CurrentUser,
    LocalMachine64,
    LocalMachine32,
}

/// <summary>
/// A registry key paired with the hive/view it must be opened in. The ID is stable
/// and can be passed back to the CLI with --id.
/// </summary>
internal sealed record RegistryLocation
{
    public RegistryLocation(RegistryScope scope, string path)
    {
        Scope = scope;
        Path = NormalizePath(path);
    }

    public RegistryScope Scope { get; }

    public string Path { get; }

    public string Code => Scope switch
    {
        RegistryScope.CurrentUser => "HKCU",
        RegistryScope.LocalMachine64 => "HKLM64",
        RegistryScope.LocalMachine32 => "HKLM32",
        _ => throw new ArgumentOutOfRangeException(nameof(Scope)),
    };

    public bool IsMachine => Scope != RegistryScope.CurrentUser;

    public string Id => $"{Code}:{Path}";

    public string DisplayPath => $"{Code}\\{Path}";

    public string RegExePath => $"{HiveName}\\{Path}";

    public RegistryHive Hive => Scope == RegistryScope.CurrentUser
        ? RegistryHive.CurrentUser
        : RegistryHive.LocalMachine;

    public RegistryView View => Scope switch
    {
        RegistryScope.LocalMachine64 => RegistryView.Registry64,
        RegistryScope.LocalMachine32 => RegistryView.Registry32,
        _ => RegistryView.Default,
    };

    /// <summary>Argument for reg.exe export, or null when no view switch is needed.</summary>
    public string? RegExeViewArgument => Scope switch
    {
        RegistryScope.LocalMachine64 => "/reg:64",
        RegistryScope.LocalMachine32 => "/reg:32",
        _ => null,
    };

    private string HiveName => Scope == RegistryScope.CurrentUser ? "HKCU" : "HKLM";

    public RegistryKey OpenBaseKey() => RegistryKey.OpenBaseKey(Hive, View);

    public static RegistryLocation ForUninstallRoot(RegistryScope scope) => new(
        scope,
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Registry path cannot be empty.", nameof(path));
        }

        return path.Trim().Trim('\\');
    }
}
