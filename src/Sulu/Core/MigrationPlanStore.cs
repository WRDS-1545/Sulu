using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed record MigrationPlanFile(
    MigrationPlan Plan,
    string Directory,
    string PlanPath,
    string CsvPath);

internal static class MigrationPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static MigrationPlanFile Create(
        InstalledApp app,
        string oldPath,
        string newPath,
        bool registryOnly,
        IReadOnlyList<MigrationChange> changes,
        string? parentDirectory)
    {
        var parent = ResolveParentDirectory(parentDirectory);
        if (PathTools.IsSameOrUnder(parent, oldPath) || PathTools.IsSameOrUnder(parent, newPath))
        {
            throw new InvalidOperationException(
                "The migration record directory must not be inside the old or new application directory.");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var slug = Slugify(app.Name, 36);
        var directory = Path.Combine(parent, $"{timestamp}-{slug}");
        var suffix = 2;
        while (Directory.Exists(directory))
        {
            directory = Path.Combine(
                parent,
                $"{timestamp}-{slug}-{suffix.ToString(CultureInfo.InvariantCulture)}");
            suffix++;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot create the migration record directory '{directory}': {ex.Message}",
                ex);
        }

        var plan = new MigrationPlan
        {
            AppName = app.Name,
            AppIndex = app.Index,
            AppRegistryId = app.Id,
            OldPath = oldPath,
            NewPath = newPath,
            RegistryOnly = registryOnly,
            Changes = changes.ToList(),
        };
        var planPath = Path.Combine(directory, "migration.json");
        var csvPath = Path.Combine(directory, "references.csv");
        WriteJson(plan, planPath);
        WriteCsv(plan, csvPath);
        return new MigrationPlanFile(plan, directory, planPath, csvPath);
    }

    public static void Save(MigrationPlan plan, string planPath)
    {
        WriteJson(plan, planPath);
    }

    public static MigrationPlan Load(string path)
    {
        var resolved = path.Trim();
        if (Directory.Exists(resolved))
        {
            resolved = Path.Combine(resolved, "migration.json");
        }

        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"Migration plan not found: {resolved}", resolved);
        }

        try
        {
            var json = File.ReadAllText(resolved);
            var plan = JsonSerializer.Deserialize<MigrationPlan>(json, JsonOptions)
                ?? throw new InvalidDataException("The migration plan is empty.");
            if (plan.SchemaVersion != 1
                || string.IsNullOrWhiteSpace(plan.OldPath)
                || string.IsNullOrWhiteSpace(plan.NewPath))
            {
                throw new InvalidDataException("The migration plan is incomplete or has an unsupported schema.");
            }

            foreach (var change in plan.Changes)
            {
                _ = change.HiveRef;
                _ = change.ValueKind;
            }

            return plan;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The migration plan is not valid JSON: {ex.Message}", ex);
        }
    }

    private static string ResolveParentDirectory(string? requested)
    {
        var raw = string.IsNullOrWhiteSpace(requested) ? SuluPaths.MigrationsRoot : requested.Trim();
        try
        {
            var full = Path.GetFullPath(raw);
            Directory.CreateDirectory(full);
            return full;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            throw new InvalidOperationException($"Invalid migration record directory '{raw}': {ex.Message}", ex);
        }
    }

    private static void WriteJson(MigrationPlan plan, string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The migration plan has no parent directory.");
        var temporary = Path.Combine(directory, $".migration-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(plan, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not write the migration plan '{path}': {ex.Message}",
                ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static void WriteCsv(MigrationPlan plan, string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "RowId,Hive,View,SubKey,ValueName,Kind,Category,Reason,CurrentValue,ProposedValue,Approved");
        for (var index = 0; index < plan.Changes.Count; index++)
        {
            var change = plan.Changes[index];
            builder.Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Csv(change.Hive)).Append(',');
            builder.Append(Csv(change.View)).Append(',');
            builder.Append(Csv(change.SubKey)).Append(',');
            builder.Append(Csv(change.ValueName)).Append(',');
            builder.Append(Csv(change.Kind)).Append(',');
            builder.Append(Csv(change.Tier)).Append(',');
            builder.Append(Csv(change.Reason)).Append(',');
            builder.Append(Csv(change.CurrentValue)).Append(',');
            builder.Append(Csv(change.ProposedValue)).Append(',');
            builder.AppendLine(change.Selected ? "YES" : "NO");
        }

        try
        {
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not write the migration review CSV '{path}': {ex.Message}",
                ex);
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Slugify(string value, int maxLength)
    {
        var chars = value
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length == 0)
        {
            slug = "app";
        }

        return slug.Length <= maxLength ? slug : slug[..maxLength].TrimEnd('-');
    }
}
