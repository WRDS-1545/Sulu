using System.Globalization;
using System.Text;

namespace Sulu.Infrastructure;

internal static class PathTools
{
    /// <summary>
    /// Turns a registry path/command fragment into a comparable absolute path.
    /// Returns null for empty, relative, or dangerous roots.
    /// </summary>
    public static string? NormalizePath(string? raw, bool rejectDriveRoot = true)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().Trim('"').Trim();
        if (value.Length == 0)
        {
            return null;
        }

        value = Environment.ExpandEnvironmentVariables(value);
        value = value.Replace('/', '\\');

        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        full = full.TrimEnd('\\');
        if (full.Length == 0)
        {
            return null;
        }

        if (rejectDriveRoot)
        {
            var pathRoot = Path.GetPathRoot(full)?.TrimEnd('\\');
            if (!string.IsNullOrEmpty(pathRoot)
                && full.Equals(pathRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return full;
    }

    public static bool IsSameOrUnder(string? candidate, string? root)
    {
        var normalizedCandidate = NormalizePath(candidate);
        var normalizedRoot = NormalizePath(root);
        if (normalizedCandidate is null || normalizedRoot is null)
        {
            return false;
        }

        if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSamePath(string? left, string? right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        return normalizedLeft is not null
            && normalizedRight is not null
            && normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects install locations that are actually shared system roots. Matching
    /// App Paths or startup values against "C:\Program Files" would be catastrophic,
    /// so such values must never be treated as an app-specific root.
    /// </summary>
    public static bool IsSafeAppRoot(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null)
        {
            return false;
        }

        string?[] sharedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ];

        foreach (var sharedRoot in sharedRoots)
        {
            var normalizedSharedRoot = NormalizePath(sharedRoot);
            if (normalizedSharedRoot is null)
            {
                continue;
            }

            // Reject the shared root itself and any of its ancestors.
            if (normalized.Equals(normalizedSharedRoot, StringComparison.OrdinalIgnoreCase)
                || IsSameOrUnder(normalizedSharedRoot, normalized))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true for locations where creating or moving files normally requires
    /// administrator rights.
    /// </summary>
    public static bool IsSystemProtectedPath(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null)
        {
            return false;
        }

        string?[] protectedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ];

        return protectedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root => IsSameOrUnder(normalized, root));
    }

    /// <summary>
    /// Returns true when a command line contains the directory as a complete path
    /// component. Requires a complete match so "C:\Apps\Foo" does not match
    /// "C:\Apps\Foobar".
    /// </summary>
    public static bool CommandReferencesDirectory(string? command, string? directory)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var root = NormalizePath(directory);
        if (root is null)
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(command).Replace('/', '\\');
        var index = 0;
        while ((index = expanded.IndexOf(root, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 ? '\0' : expanded[index - 1];
            var afterIndex = index + root.Length;
            var after = afterIndex >= expanded.Length ? '\0' : expanded[afterIndex];

            var validBefore = before is '\0' or '"' or '\'' or ' ' or '\t' or '=' or ',' or ';';
            var validAfter = after is '\0' or '"' or '\'' or ' ' or '\t' or ',' or ';' or '\\';

            if (validBefore && validAfter)
            {
                return true;
            }

            index += Math.Max(root.Length, 1);
        }

        return false;
    }

    /// <summary>
    /// App Paths default values are normally unquoted full paths, even when the
    /// path contains spaces. Only strip quotes when present; otherwise keep the
    /// whole value instead of splitting on the first space.
    /// </summary>
    public static string? ExtractAppPathExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(value).Trim();
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            return closing > 1 ? text[1..closing].Trim() : null;
        }

        return text.Trim();
    }

    public static long? ParseEstimatedSize(object? raw)
    {
        if (raw is null)
        {
            return null;
        }

        try
        {
            var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) && kb >= 0)
            {
                return kb > long.MaxValue / 1024 ? null : kb * 1024;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }

        return null;
    }

    public static int? ParseInt(object? raw)
    {
        if (raw is null)
        {
            return null;
        }

        try
        {
            var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString().Trim();
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null or < 0)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes.Value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }
}
