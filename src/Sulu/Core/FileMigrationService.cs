using System.ComponentModel;
using System.Diagnostics;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed record FileMigrationResult(
    bool Success,
    string? Error,
    IReadOnlyList<string> Warnings);

internal sealed record DirectoryRemovalResult(
    bool Removed,
    string? QuarantinePath,
    string? Error);

internal static class FileMigrationService
{
    public static IReadOnlyList<string> FindRunningProcesses(string directory)
    {
        var matches = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return matches;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var executable = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable)
                        && PathTools.IsSameOrUnder(executable, directory))
                    {
                        matches.Add($"{process.ProcessName} (PID {process.Id})");
                    }
                }
                catch (Exception ex) when (ex is Win32Exception
                    or InvalidOperationException
                    or NotSupportedException
                    or UnauthorizedAccessException)
                {
                    // Protected processes cannot expose their image path.
                }
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static FileMigrationResult CopyDirectory(string source, string target)
    {
        var warnings = new List<string>();
        try
        {
            var sourceFull = Path.GetFullPath(source).TrimEnd('\\');
            var targetFull = Path.GetFullPath(target).TrimEnd('\\');
            if (!Directory.Exists(sourceFull))
            {
                return new FileMigrationResult(false, $"Source directory does not exist: {sourceFull}", warnings);
            }

            if (PathTools.IsSamePath(sourceFull, targetFull)
                || PathTools.IsSameOrUnder(targetFull, sourceFull)
                || PathTools.IsSameOrUnder(sourceFull, targetFull))
            {
                return new FileMigrationResult(
                    false,
                    "The source and target directories must not contain each other.",
                    warnings);
            }

            if (File.Exists(targetFull))
            {
                return new FileMigrationResult(false, $"A file already exists at the target: {targetFull}", warnings);
            }

            if (Directory.Exists(targetFull))
            {
                if (Directory.EnumerateFileSystemEntries(targetFull).Any())
                {
                    return new FileMigrationResult(
                        false,
                        $"The target directory is not empty: {targetFull}",
                        warnings);
                }

                if ((new DirectoryInfo(targetFull).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return new FileMigrationResult(
                        false,
                        "The target directory is a reparse point. Refusing to copy through it.",
                        warnings);
                }
            }

            var sourceInfo = new DirectoryInfo(sourceFull);
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new FileMigrationResult(
                    false,
                    "The source directory is a reparse point. Refusing to migrate it automatically.",
                    warnings);
            }

            var parent = Path.GetDirectoryName(targetFull);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return new FileMigrationResult(false, "The target directory has no parent.", warnings);
            }

            Directory.CreateDirectory(parent);

            var robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            if (!File.Exists(robocopy))
            {
                return new FileMigrationResult(
                    false,
                    "robocopy.exe was not found; the file migration was not started.",
                    warnings);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = robocopy,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            startInfo.ArgumentList.Add(sourceFull);
            startInfo.ArgumentList.Add(targetFull);
            startInfo.ArgumentList.Add("/E");
            startInfo.ArgumentList.Add("/COPY:DAT");
            startInfo.ArgumentList.Add("/DCOPY:DAT");
            startInfo.ArgumentList.Add("/R:1");
            startInfo.ArgumentList.Add("/W:1");
            startInfo.ArgumentList.Add("/XJ");
            startInfo.ArgumentList.Add("/NFL");
            startInfo.ArgumentList.Add("/NDL");
            startInfo.ArgumentList.Add("/NP");
            startInfo.ArgumentList.Add("/NJH");
            startInfo.ArgumentList.Add("/NJS");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new FileMigrationResult(false, "The directory copy process could not be started.", warnings);
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode is >= 4)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return new FileMigrationResult(
                    false,
                    $"The directory copy reported mismatched or failed files "
                    + $"(robocopy exit code {process.ExitCode})."
                    + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {PathTools.OneLine(detail)}"),
                    warnings);
            }

            if (!Directory.Exists(targetFull))
            {
                return new FileMigrationResult(
                    false,
                    $"The directory copy reported success but the target does not exist: {targetFull}",
                    warnings);
            }

            return new FileMigrationResult(true, null, warnings);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or Win32Exception)
        {
            return new FileMigrationResult(false, PathTools.OneLine(ex.Message), warnings);
        }
    }

    public static FileMigrationResult DeleteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return new FileMigrationResult(true, null, []);
            }

            Directory.Delete(directory, recursive: true);
            return new FileMigrationResult(true, null, []);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return new FileMigrationResult(false, PathTools.OneLine(ex.Message), []);
        }
    }

    /// <summary>
    /// Removes the old application path through an atomic rename. If recursive
    /// deletion then fails, the old path is still gone and the complete tree is
    /// available under the returned quarantine path instead of being half-deleted.
    /// </summary>
    public static DirectoryRemovalResult RemoveApplicationDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return new DirectoryRemovalResult(true, null, null);
            }

            var parent = Path.GetDirectoryName(directory);
            var leaf = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(leaf))
            {
                return new DirectoryRemovalResult(false, null, "The old directory has no usable name.");
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var quarantine = Path.Combine(parent, $".{leaf}.sulu-old-{stamp}");
            var suffix = 2;
            while (Directory.Exists(quarantine) || File.Exists(quarantine))
            {
                quarantine = Path.Combine(
                    parent,
                    $".{leaf}.sulu-old-{stamp}-{suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                suffix++;
            }

            Directory.Move(directory, quarantine);
            try
            {
                Directory.Delete(quarantine, recursive: true);
                return new DirectoryRemovalResult(true, null, null);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return new DirectoryRemovalResult(true, quarantine, PathTools.OneLine(ex.Message));
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return new DirectoryRemovalResult(false, null, PathTools.OneLine(ex.Message));
        }
    }
}
