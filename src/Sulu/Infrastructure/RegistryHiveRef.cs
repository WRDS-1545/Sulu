using Microsoft.Win32;

namespace Sulu.Infrastructure;

/// <summary>
/// A registry hive and view that can be opened directly and named exactly as the
/// scanner reports it. This is deliberately broader than RegistryLocation because
/// path migration also scans HKU and HKCC.
/// </summary>
internal sealed record RegistryHiveRef(
    string Name,
    RegistryHive Hive,
    string ViewName,
    RegistryView View)
{
    public bool RequiresElevation => Hive is RegistryHive.LocalMachine
        or RegistryHive.Users
        or RegistryHive.CurrentConfig;

    public RegistryKey OpenBaseKey() => RegistryKey.OpenBaseKey(Hive, View);

    public override string ToString() => $"{Name} ({ViewName})";

    public static IReadOnlyList<RegistryHiveRef> All { get; } =
    [
        new("HKLM", RegistryHive.LocalMachine, "Registry64", RegistryView.Registry64),
        new("HKLM", RegistryHive.LocalMachine, "Registry32", RegistryView.Registry32),
        new("HKCU", RegistryHive.CurrentUser, "Registry64", RegistryView.Registry64),
        new("HKCU", RegistryHive.CurrentUser, "Registry32", RegistryView.Registry32),
        new("HKU", RegistryHive.Users, "Registry64", RegistryView.Registry64),
        new("HKU", RegistryHive.Users, "Registry32", RegistryView.Registry32),
        new("HKCC", RegistryHive.CurrentConfig, "Registry64", RegistryView.Registry64),
        new("HKCC", RegistryHive.CurrentConfig, "Registry32", RegistryView.Registry32),
    ];

    public static RegistryHiveRef FromName(string hive, string view)
    {
        var match = All.FirstOrDefault(candidate =>
            candidate.Name.Equals(hive, StringComparison.OrdinalIgnoreCase)
            && candidate.ViewName.Equals(view, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException($"Unsupported registry hive/view: {hive}/{view}.");
        }

        return match;
    }
}
