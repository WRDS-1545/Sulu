using Microsoft.Win32;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed record RegistryValueReference
{
    public required int RowId { get; init; }

    public required RegistryHiveRef Hive { get; init; }

    public required string SubKey { get; init; }

    public required string ValueName { get; init; }

    public required RegistryValueKind Kind { get; init; }

    public required string CurrentValue { get; init; }

    public required string ProposedValue { get; init; }

    public required bool Safe { get; init; }

    public required string Reason { get; init; }

    public bool ServiceReference { get; init; }

    public bool RequiresElevation => Hive.RequiresElevation;

    public string Category => Safe ? "SAFE" : ServiceReference ? "SERVICE" : "REVIEW";

    public string DisplayLocation =>
        $"{Hive.Name} ({Hive.ViewName})\\{SubKey} :: "
        + (ValueName.Length == 0 ? "(Default)" : ValueName);
}

internal sealed record MigrationScanResult(
    IReadOnlyList<RegistryValueReference> References,
    IReadOnlyList<string> Warnings,
    int KeysScanned,
    int ValuesScanned,
    int ErrorCount);

internal sealed class MigrationChange
{
    public string Hive { get; set; } = string.Empty;

    public string View { get; set; } = string.Empty;

    public string SubKey { get; set; } = string.Empty;

    public string ValueName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string CurrentValue { get; set; } = string.Empty;

    public string ProposedValue { get; set; } = string.Empty;

    public string Tier { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public bool Selected { get; set; }

    public bool Applied { get; set; }

    public bool Restored { get; set; }

    public bool RequiresElevation => !Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase);

    public string DisplayLocation =>
        $"{Hive} ({View})\\{SubKey} :: " + (ValueName.Length == 0 ? "(Default)" : ValueName);

    public RegistryHiveRef HiveRef => RegistryHiveRef.FromName(Hive, View);

    public RegistryValueKind ValueKind =>
        Enum.TryParse<RegistryValueKind>(Kind, ignoreCase: true, out var kind)
            ? kind
            : throw new InvalidDataException($"Unsupported registry value kind '{Kind}'.");
}

internal sealed class MigrationPlan
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string AppName { get; set; } = string.Empty;

    public int AppIndex { get; set; }

    public string AppRegistryId { get; set; } = string.Empty;

    public string OldPath { get; set; } = string.Empty;

    public string NewPath { get; set; } = string.Empty;

    public bool RegistryOnly { get; set; }

    public bool FilesCopied { get; set; }

    public bool SourceDeleted { get; set; }

    public string? QuarantinePath { get; set; }

    public bool Completed { get; set; }

    public bool Restored { get; set; }

    public List<MigrationChange> Changes { get; set; } = [];
}

internal sealed record MigrationApplyResult(
    int AppliedCount,
    IReadOnlyList<string> Failures);
