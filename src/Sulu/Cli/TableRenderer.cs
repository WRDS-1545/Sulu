using System.Globalization;
using System.Text;

namespace Sulu.Cli;

internal static class TableRenderer
{
    public static void Render(
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows,
        IReadOnlyList<int> maxWidths,
        IReadOnlySet<int>? rightAligned = null,
        IReadOnlySet<int>? wrapColumns = null)
    {
        if (headers.Count == 0 || rows.Count == 0)
        {
            return;
        }

        var widths = new int[headers.Count];
        for (var column = 0; column < headers.Count; column++)
        {
            var cap = column < maxWidths.Count ? maxWidths[column] : 60;
            var width = DisplayWidth(headers[column]);
            foreach (var row in rows)
            {
                if (column < row.Length)
                {
                    width = Math.Max(width, DisplayWidth(row[column]));
                }
            }

            widths[column] = Math.Clamp(width, 1, Math.Max(1, cap));
        }

        var border = "+" + string.Join("+", widths.Select(width => new string('-', width + 2))) + "+";
        Console.WriteLine(border);
        Console.WriteLine(BuildRow(headers, widths, null));
        Console.WriteLine(border);
        foreach (var row in rows)
        {
            foreach (var line in BuildRowLines(row, widths, rightAligned, wrapColumns))
            {
                Console.WriteLine(line);
            }
        }

        Console.WriteLine(border);
    }

    private static List<string> BuildRowLines(
        string[] cells,
        int[] widths,
        IReadOnlySet<int>? rightAligned,
        IReadOnlySet<int>? wrapColumns)
    {
        var linesByColumn = new List<string>[widths.Length];
        var lineCount = 1;
        for (var column = 0; column < widths.Length; column++)
        {
            var raw = OneLine(column < cells.Length ? cells[column] ?? string.Empty : string.Empty);
            var values = wrapColumns?.Contains(column) == true
                ? Wrap(raw, widths[column])
                : new List<string> { Truncate(raw, widths[column]) };
            linesByColumn[column] = values;
            lineCount = Math.Max(lineCount, values.Count);
        }

        var lines = new List<string>(lineCount);
        for (var line = 0; line < lineCount; line++)
        {
            var values = new string[widths.Length];
            for (var column = 0; column < widths.Length; column++)
            {
                var columnLines = linesByColumn[column];
                values[column] = line < columnLines.Count ? columnLines[line] : string.Empty;
            }

            lines.Add(BuildRow(values, widths, rightAligned));
        }

        return lines;
    }

    private static string BuildRow(
        IReadOnlyList<string> cells,
        int[] widths,
        IReadOnlySet<int>? rightAligned)
    {
        var builder = new StringBuilder("|");
        for (var column = 0; column < widths.Length; column++)
        {
            var raw = column < cells.Count ? cells[column] ?? string.Empty : string.Empty;
            var value = Truncate(OneLine(raw), widths[column]);
            var padding = widths[column] - DisplayWidth(value);
            builder.Append(' ');
            if (rightAligned?.Contains(column) == true)
            {
                builder.Append(' ', padding).Append(value);
            }
            else
            {
                builder.Append(value).Append(' ', padding);
            }

            builder.Append(" |");
        }

        return builder.ToString();
    }

    private static string OneLine(string value) => value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("\t", " ", StringComparison.Ordinal);

    public static int DisplayWidth(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }

        return width;
    }

    private static List<string> Wrap(string value, int maxWidth)
    {
        if (DisplayWidth(value) <= maxWidth)
        {
            return [value];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            lines.Add(current.ToString());
            current.Clear();
            currentWidth = 0;
        }

        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var wordWidth = DisplayWidth(word);
            if (wordWidth > maxWidth)
            {
                Flush();
                var chunk = new StringBuilder();
                var chunkWidth = 0;
                foreach (var rune in word.EnumerateRunes())
                {
                    var runeWidth = RuneWidth(rune);
                    if (chunkWidth + runeWidth > maxWidth && chunk.Length > 0)
                    {
                        lines.Add(chunk.ToString());
                        chunk.Clear();
                        chunkWidth = 0;
                    }

                    chunk.Append(rune.ToString());
                    chunkWidth += runeWidth;
                }

                if (chunk.Length > 0)
                {
                    current.Append(chunk);
                    currentWidth = chunkWidth;
                }

                continue;
            }

            if (current.Length == 0)
            {
                current.Append(word);
                currentWidth = wordWidth;
            }
            else if (currentWidth + 1 + wordWidth <= maxWidth)
            {
                current.Append(' ').Append(word);
                currentWidth += 1 + wordWidth;
            }
            else
            {
                Flush();
                current.Append(word);
                currentWidth = wordWidth;
            }
        }

        Flush();
        return lines.Count == 0 ? [string.Empty] : lines;
    }


    private static string Truncate(string value, int maxWidth)
    {
        if (DisplayWidth(value) <= maxWidth)
        {
            return value;
        }

        if (maxWidth <= 3)
        {
            return new string('.', maxWidth);
        }

        var builder = new StringBuilder();
        var width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeWidth = RuneWidth(rune);
            if (width + runeWidth > maxWidth - 3)
            {
                break;
            }

            builder.Append(rune.ToString());
            width += runeWidth;
        }

        builder.Append("...");
        return builder.ToString();
    }

    private static int RuneWidth(Rune rune)
    {
        var value = rune.Value;
        if (value is 0 or < 32 or (>= 0x7f and < 0xa0))
        {
            return 0;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(value) ? 2 : 1;
    }

    private static bool IsWide(int value) =>
        value is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1B000 and <= 0x1B2FF
            or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;
}
