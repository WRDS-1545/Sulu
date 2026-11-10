using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed record IndexedCatalog(
    IReadOnlyList<InstalledApp> Apps,
    IReadOnlyList<string> Warnings);

internal sealed class AppIndexEntry
{
    public int Index { get; set; }

    public string RegistryId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Publisher { get; set; }

    public string? InstallLocation { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }
}

internal sealed class AppIndexDocument
{
    public int SchemaVersion { get; set; } = 1;

    public int NextIndex { get; set; } = 1;

    public List<AppIndexEntry> Apps { get; set; } = [];
}

/// <summary>
/// Assigns a persistent, human-sized number to every uninstall registry entry.
/// The registry key path is the identity; changing sort order or installing another
/// application never renumbers existing entries. The file is intentionally kept
/// outside the registry so listing remains read-only from the registry's point of view.
/// </summary>
internal static class AppIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object SyncRoot = new();

    public static IndexedCatalog LoadIndexedCatalog()
    {
        var apps = RegistryCatalog.LoadAll();
        return AttachIndexes(apps);
    }

    public static IndexedCatalog AttachIndexes(IReadOnlyList<InstalledApp> apps)
    {
        lock (SyncRoot)
        {
            var warnings = new List<string>();
            var document = LoadDocument(warnings);
            var originalJson = JsonSerializer.Serialize(document, JsonOptions);
            var now = DateTimeOffset.Now;

            var entriesById = new Dictionary<string, AppIndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Apps)
            {
                if (entry.Index > 0
                    && !string.IsNullOrWhiteSpace(entry.RegistryId)
                    && !entriesById.ContainsKey(entry.RegistryId))
                {
                    entriesById[entry.RegistryId] = entry;
                }
            }

            var nextIndex = Math.Max(
                document.NextIndex,
                document.Apps.Count == 0 ? 1 : document.Apps.Max(entry => entry.Index) + 1);
            var indexedApps = new List<InstalledApp>(apps.Count);

            foreach (var app in apps)
            {
                if (!entriesById.TryGetValue(app.Id, out var entry))
                {
                    entry = new AppIndexEntry
                    {
                        Index = nextIndex++,
                        RegistryId = app.Id,
                        FirstSeen = now,
                    };
                    entriesById[app.Id] = entry;
                }

                entry.Name = app.Name;
                entry.Publisher = app.Publisher;
                entry.InstallLocation = app.InstallLocation;
                entry.LastSeen = now;
                indexedApps.Add(app with { Index = entry.Index });
            }

            document.NextIndex = Math.Max(nextIndex, 1);
            document.Apps = entriesById.Values
                .OrderBy(entry => entry.Index)
                .ThenBy(entry => entry.RegistryId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var updatedJson = JsonSerializer.Serialize(document, JsonOptions);
            if (!string.Equals(originalJson, updatedJson, StringComparison.Ordinal))
            {
                TrySaveDocument(document, warnings);
            }

            return new IndexedCatalog(indexedApps, warnings);
        }
    }

    public static bool TryUpdateInstallLocation(
        string registryId,
        string installLocation,
        out string? warning)
    {
        lock (SyncRoot)
        {
            var warnings = new List<string>();
            var document = LoadDocument(warnings);
            var entry = document.Apps.FirstOrDefault(
                candidate => candidate.RegistryId.Equals(registryId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
                return false;
            }

            entry.InstallLocation = installLocation;
            entry.LastSeen = DateTimeOffset.Now;
            var saved = TrySaveDocument(document, warnings);
            warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
            return saved;
        }
    }

    public static InstalledApp? FindByIndex(IReadOnlyList<InstalledApp> apps, int index) =>
        apps.FirstOrDefault(app => app.Index == index);

    private static AppIndexDocument LoadDocument(List<string> warnings)
    {
        var path = SuluPaths.IndexFile;
        if (!File.Exists(path))
        {
            return new AppIndexDocument();
        }

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<AppIndexDocument>(json, JsonOptions);
            if (document is null)
            {
                warnings.Add($"The application index at '{path}' is empty and will be recreated.");
                return new AppIndexDocument();
            }

            NormalizeDocument(document, path, warnings);
            return document;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            warnings.Add(
                $"The application index at '{path}' could not be read ({ex.Message}). "
                + "New indexes will be assigned for this run.");
            TryPreserveCorruptIndex(path, warnings);
            return new AppIndexDocument();
        }
    }

    private static void NormalizeDocument(
        AppIndexDocument document,
        string path,
        List<string> warnings)
    {
        var usedIndexes = new HashSet<int>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleanEntries = new List<AppIndexEntry>();
        var changed = false;
        var next = Math.Max(document.NextIndex, 1);

        foreach (var entry in document.Apps.OrderBy(entry => entry.Index))
        {
            if (entry.Index <= 0
                || string.IsNullOrWhiteSpace(entry.RegistryId)
                || !usedIds.Add(entry.RegistryId))
            {
                changed = true;
                continue;
            }

            if (!usedIndexes.Add(entry.Index))
            {
                while (usedIndexes.Contains(next))
                {
                    next++;
                }

                entry.Index = next;
                usedIndexes.Add(entry.Index);
                changed = true;
            }

            next = Math.Max(next, entry.Index + 1);
            cleanEntries.Add(entry);
        }

        if (changed)
        {
            warnings.Add($"The application index at '{path}' contained duplicate entries and was repaired.");
        }

        document.Apps = cleanEntries;
        document.NextIndex = Math.Max(next, 1);
    }

    private static bool TrySaveDocument(AppIndexDocument document, List<string> warnings)
    {
        var path = SuluPaths.IndexFile;
        var directory = Path.GetDirectoryName(path);
        string? temporary = null;
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new IOException("The application index has no parent directory.");
            }

            Directory.CreateDirectory(directory);
            temporary = Path.Combine(
                directory,
                $".index-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            warnings.Add(
                $"Could not save the application index to '{path}' ({ex.Message}). "
                + "Indexes may change on the next run.");
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup of the temporary file.
                }
            }
        }
    }

    private static void TryPreserveCorruptIndex(string path, List<string> warnings)
    {
        try
        {
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup, overwrite: true);
            warnings.Add($"The unreadable index was preserved at '{backup}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original read error is already reported.
        }
    }
}
