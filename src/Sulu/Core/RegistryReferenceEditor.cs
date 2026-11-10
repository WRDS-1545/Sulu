using System.Security;
using Microsoft.Win32;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal static class RegistryReferenceEditor
{
    public static MigrationApplyResult Apply(MigrationPlan plan, bool isElevated)
    {
        var failures = new List<string>();
        var applied = 0;
        foreach (var change in plan.Changes.Where(change => change.Selected))
        {
            if (change.RequiresElevation && !isElevated)
            {
                failures.Add($"{change.DisplayLocation}: requires an elevated terminal.");
                break;
            }

            try
            {
                using var baseKey = change.HiveRef.OpenBaseKey();
                using var key = baseKey.OpenSubKey(change.SubKey, writable: true);
                if (key is null)
                {
                    failures.Add($"{change.DisplayLocation}: the registry key no longer exists.");
                    break;
                }

                var currentKind = key.GetValueKind(change.ValueName);
                var current = key.GetValue(
                    change.ValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
                if (currentKind != change.ValueKind)
                {
                    failures.Add($"{change.DisplayLocation}: the registry value type changed.");
                    break;
                }

                if (current.Equals(change.ProposedValue, StringComparison.Ordinal))
                {
                    change.Applied = true;
                    continue;
                }

                if (!current.Equals(change.CurrentValue, StringComparison.Ordinal))
                {
                    failures.Add($"{change.DisplayLocation}: the value changed after the migration scan.");
                    break;
                }

                key.SetValue(change.ValueName, change.ProposedValue, change.ValueKind);
                var verified = key.GetValue(
                    change.ValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
                if (!verified.Equals(change.ProposedValue, StringComparison.Ordinal))
                {
                    failures.Add($"{change.DisplayLocation}: the new registry value could not be verified.");
                    break;
                }

                change.Applied = true;
                applied++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or SecurityException
                or IOException
                or InvalidOperationException
                or ArgumentException)
            {
                failures.Add($"{change.DisplayLocation}: {PathTools.OneLine(ex.Message)}");
                break;
            }
        }

        return new MigrationApplyResult(applied, failures);
    }

    public static MigrationApplyResult Restore(
        MigrationPlan plan,
        bool isElevated,
        bool force)
    {
        var failures = new List<string>();
        var restored = 0;
        foreach (var change in plan.Changes.Where(change => change.Selected).Reverse())
        {
            if (change.RequiresElevation && !isElevated)
            {
                failures.Add($"{change.DisplayLocation}: requires an elevated terminal.");
                break;
            }

            try
            {
                using var baseKey = change.HiveRef.OpenBaseKey();
                using var key = baseKey.OpenSubKey(change.SubKey, writable: true);
                if (key is null)
                {
                    failures.Add($"{change.DisplayLocation}: the registry key no longer exists.");
                    continue;
                }

                var currentKind = key.GetValueKind(change.ValueName);
                var current = key.GetValue(
                    change.ValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
                if (currentKind == change.ValueKind
                    && current.Equals(change.CurrentValue, StringComparison.Ordinal))
                {
                    change.Restored = true;
                    continue;
                }

                if (!force
                    && (currentKind != change.ValueKind
                        || !current.Equals(change.ProposedValue, StringComparison.Ordinal)))
                {
                    failures.Add(
                        $"{change.DisplayLocation}: the current value differs from the migrated value; "
                        + "use --force to overwrite it.");
                    continue;
                }

                key.SetValue(change.ValueName, change.CurrentValue, change.ValueKind);
                var verified = key.GetValue(
                    change.ValueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
                if (!verified.Equals(change.CurrentValue, StringComparison.Ordinal))
                {
                    failures.Add($"{change.DisplayLocation}: the restored value could not be verified.");
                    continue;
                }

                change.Restored = true;
                restored++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or SecurityException
                or IOException
                or InvalidOperationException
                or ArgumentException)
            {
                failures.Add($"{change.DisplayLocation}: {PathTools.OneLine(ex.Message)}");
            }
        }

        return new MigrationApplyResult(restored, failures);
    }
}
