using System.Text;
using Sulu.Cli;

namespace Sulu;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            // Redirected or non-console hosts may not support changing the encoding.
        }

        try
        {
            return CliApplication.Run(args);
        }
        catch (CliException ex)
        {
            ConsoleUi.Error(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            ConsoleUi.Error($"Unexpected error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("SULU_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }

            return ExitCodes.Failure;
        }
    }
}
