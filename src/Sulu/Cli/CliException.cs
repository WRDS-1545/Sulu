namespace Sulu.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int NotFoundOrAmbiguous = 3;
    public const int Cancelled = 4;
    public const int UninstallFailed = 5;
    public const int ElevationRequired = 6;
    public const int CleanupFailed = 7;
    public const int MigrationFailed = 8;
}

internal sealed class CliException : Exception
{
    public CliException(string message, int exitCode = ExitCodes.Failure)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
