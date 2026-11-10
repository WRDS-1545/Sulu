using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed class BackupException : Exception
{
    public BackupException(string message)
        : base(message)
    {
    }

    public BackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record BackupEntry(string Label, string RegistryKey, string File);

internal sealed record BackupResult(
    string Directory,
    IReadOnlyList<BackupEntry> Entries);

internal sealed class RegistryBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static BackupResult CreateBackup(
        string appName,
        string appId,
        IReadOnlyList<RegistryFinding> findings,
        string? parentDirectory)
    {
        if (findings.Count == 0)
        {
            throw new ArgumentException("At least one finding is required.", nameof(findings));
        }

        var parent = ResolveParentDirectory(parentDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var slug = Slugify(appName, 36);
        var directory = Path.Combine(parent, $"{timestamp}-{slug}");
        var suffix = 2;
        while (Directory.Exists(directory))
        {
            directory = Path.Combine(parent, $"{timestamp}-{slug}-{suffix.ToString(CultureInfo.InvariantCulture)}");
            suffix++;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupException($"Cannot create backup directory '{directory}': {ex.Message}", ex);
        }

        var keys = findings
            .Select(finding => finding.Location)
            .GroupBy(location => location.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var entries = new List<BackupEntry>();
        var index = 1;
        foreach (var location in keys)
        {
            var label = findings.First(finding =>
                finding.Location.Id.Equals(location.Id, StringComparison.OrdinalIgnoreCase)).Category;
            var fileName = $"{index.ToString("00", CultureInfo.InvariantCulture)}-{Slugify(label, 24)}.reg";
            var filePath = Path.Combine(directory, fileName);
            ExportKey(location, filePath);
            entries.Add(new BackupEntry(label, location.DisplayPath, filePath));
            index++;
        }

        var manifest = new
        {
            schemaVersion = 1,
            createdAt = DateTimeOffset.Now,
            app = new { name = appName, id = appId },
            registryKeys = entries.Select(entry => new
            {
                entry.Label,
                entry.RegistryKey,
                entry.File,
            }),
        };

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupException($"Cannot write the backup manifest: {ex.Message}", ex);
        }

        return new BackupResult(directory, entries);
    }

    private static string ResolveParentDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                var full = Path.GetFullPath(requested.Trim());
                Directory.CreateDirectory(full);
                return full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new BackupException($"Invalid backup directory '{requested}': {ex.Message}", ex);
            }
        }

        var root = SuluPaths.BackupsRoot;

        try
        {
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupException($"Cannot create the default backup directory '{root}': {ex.Message}", ex);
        }
    }

    private static void ExportKey(RegistryLocation location, string filePath)
    {
        var regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = regExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory,
        };

        startInfo.ArgumentList.Add("export");
        startInfo.ArgumentList.Add(location.RegExePath);
        startInfo.ArgumentList.Add(filePath);
        startInfo.ArgumentList.Add("/y");
        if (location.RegExeViewArgument is not null)
        {
            startInfo.ArgumentList.Add(location.RegExeViewArgument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new BackupException($"Could not start {regExe}.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killEx) when (killEx is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process may have exited between the timeout and the kill.
                }

                throw new BackupException($"Timed out while backing up {location.DisplayPath}.");
            }

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new BackupException(
                    $"Could not back up {location.DisplayPath}: {detail.Trim()}");
            }

            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            {
                throw new BackupException($"The backup file for {location.DisplayPath} was not created.");
            }
        }
        catch (BackupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new BackupException($"Could not export {location.DisplayPath}: {ex.Message}", ex);
        }
    }

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
