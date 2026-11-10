using System.Text;

namespace Sulu.Cli;

internal static class ConsoleUi
{
    private const string Reset = "\u001b[0m";
    private const string BoldCode = "\u001b[1m";
    private const string DimCode = "\u001b[2m";
    private const string CyanCode = "\u001b[36m";
    private const string GreenCode = "\u001b[32m";
    private const string YellowCode = "\u001b[33m";
    private const string RedCode = "\u001b[31m";

    private static bool _color = !Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static void DisableColor() => _color = false;

    public static void Title(string text)
    {
        Console.WriteLine();
        WriteColor(text, BoldCode + CyanCode);
    }

    public static void Section(string text)
    {
        Console.WriteLine();
        WriteColor(text, BoldCode);
    }

    public static void Info(string text) => Console.WriteLine(text);

    public static void Muted(string text) => WriteColor(text, DimCode);

    public static void Success(string text) => WriteColor(text, GreenCode);

    public static void Warning(string text) => WriteColor(text, YellowCode);

    public static void WarningToStandardError(string text)
    {
        if (_color && !Console.IsErrorRedirected)
        {
            Console.Error.WriteLine($"{YellowCode}{text}{Reset}");
        }
        else
        {
            Console.Error.WriteLine(text);
        }
    }

    public static void Error(string text)
    {
        if (_color && !Console.IsErrorRedirected)
        {
            Console.Error.WriteLine($"{RedCode}{text}{Reset}");
        }
        else
        {
            Console.Error.WriteLine(text);
        }
    }

    public static void KeyValue(string key, string? value)
    {
        Console.Write($"  {key,-18}");
        Console.WriteLine(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    public static void WaitForEnter(string message)
    {
        if (Console.IsInputRedirected)
        {
            throw new CliException(
                "Waiting for Enter requires interactive stdin. Re-run without --pause in non-interactive mode.",
                ExitCodes.Cancelled);
        }

        Console.WriteLine();
        Warning(message);
        Console.Write("Press Enter to continue... ");
        if (Console.ReadLine() is null)
        {
            throw new CliException(
                "Input closed before Enter was pressed. Registry cleanup was skipped.",
                ExitCodes.Cancelled);
        }
    }

    public static bool Confirm(string question, bool defaultNo = true)
    {
        if (Console.IsInputRedirected)
        {
            throw new CliException(
                "Confirmation is required but stdin is not interactive. Re-run with --yes to proceed.",
                ExitCodes.Cancelled);
        }

        var suffix = defaultNo ? "[y/N]" : "[Y/n]";
        while (true)
        {
            Console.Write($"{question} {suffix} ");
            var answer = Console.ReadLine();
            if (answer is null)
            {
                return false;
            }

            answer = answer.Trim();
            if (answer.Length == 0)
            {
                return !defaultNo;
            }

            if (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (answer.Equals("n", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Warning("Please answer y or n.");
        }
    }

    public static string OneLine(string? value) => Infrastructure.PathTools.OneLine(value);

    private static void WriteColor(string text, string code)
    {
        if (_color)
        {
            Console.WriteLine($"{code}{text}{Reset}");
        }
        else
        {
            Console.WriteLine(text);
        }
    }
}
