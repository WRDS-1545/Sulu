using System.ComponentModel;
using System.Diagnostics;
using Sulu.Cli;
using Sulu.Infrastructure;

namespace Sulu.Core;

internal sealed record UninstallPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? ShellCommand,
    string DisplayCommand,
    bool IsMsi,
    bool QuietFallback);

internal enum UninstallRunStatus
{
    Exited,
    TimedOut,
    StartFailed,
}

internal sealed record UninstallRunResult(
    UninstallRunStatus Status,
    int? ExitCode,
    string? Error);

internal sealed class UninstallService
{
    public static UninstallPlan BuildPlan(InstalledApp app, bool quiet)
    {
        if (app.NoRemove)
        {
            throw new CliException(
                $"'{app.Name}' is marked NoRemove in the registry and cannot be uninstalled by this tool.",
                ExitCodes.UninstallFailed);
        }

        var productCode = NormalizeProductCode(app.ProductCode);
        if (app.IsMsi && productCode is not null)
        {
            var arguments = new List<string> { "/x", productCode, quiet ? "/qn" : "/qb", "/norestart" };
            var display = $"{Path.Combine(Environment.SystemDirectory, "msiexec.exe")} /x {productCode} "
                + (quiet ? "/qn" : "/qb")
                + " /norestart";

            return new UninstallPlan(
                Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                arguments,
                null,
                display,
                true,
                false);
        }

        if (string.IsNullOrWhiteSpace(app.UninstallString)
            && string.IsNullOrWhiteSpace(app.QuietUninstallString))
        {
            throw new CliException(
                $"'{app.Name}' has no UninstallString in the registry.",
                ExitCodes.UninstallFailed);
        }

        var useQuiet = quiet && !string.IsNullOrWhiteSpace(app.QuietUninstallString);
        var command = useQuiet
            ? app.QuietUninstallString!
            : app.UninstallString ?? app.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new CliException(
                $"'{app.Name}' has no usable uninstall command in the registry.",
                ExitCodes.UninstallFailed);
        }

        var fallback = quiet && !useQuiet;

        return new UninstallPlan(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            [],
            command,
            command,
            false,
            fallback);
    }

    public static UninstallRunResult Run(UninstallPlan plan, bool quiet, TimeSpan timeout)
    {
        ProcessStartInfo startInfo;
        if (plan.IsMsi)
        {
            startInfo = new ProcessStartInfo
            {
                FileName = plan.FileName,
                UseShellExecute = false,
                CreateNoWindow = quiet,
            };

            foreach (var argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = plan.FileName,
                Arguments = $"/d /s /c \"{plan.ShellCommand}\"",
                UseShellExecute = false,
                CreateNoWindow = quiet,
            };
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new UninstallRunResult(
                    UninstallRunStatus.StartFailed,
                    null,
                    "The uninstaller process could not be started.");
            }

            var exited = timeout <= TimeSpan.Zero
                ? WaitIndefinitely(process)
                : process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));

            if (!exited)
            {
                return new UninstallRunResult(
                    UninstallRunStatus.TimedOut,
                    null,
                    $"The uninstaller did not exit within {timeout.TotalMinutes:0.#} minutes.");
            }

            return new UninstallRunResult(UninstallRunStatus.Exited, process.ExitCode, null);
        }
        catch (Win32Exception ex)
        {
            var hint = ex.NativeErrorCode switch
            {
                740 => " The uninstaller requires elevation. Re-run this tool from an elevated terminal.",
                1223 => " Elevation was cancelled.",
                _ => string.Empty,
            };

            return new UninstallRunResult(
                UninstallRunStatus.StartFailed,
                null,
                $"Could not start the uninstaller: {ex.Message}.{hint}".Trim());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new UninstallRunResult(
                UninstallRunStatus.StartFailed,
                null,
                $"Could not start the uninstaller: {ex.Message}");
        }
    }

    /// <summary>
    /// Gives the uninstaller a short window to remove its uninstall entry. Some
    /// uninstallers spawn a helper and exit early, so this avoids racing the scan.
    /// </summary>
    public static bool WaitForUninstallEntryRemoval(InstalledApp app, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (!RegistryCatalog.Exists(app.Location))
            {
                return true;
            }

            if (timeout <= TimeSpan.Zero || DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(250);
        }
        while (true);

        return !RegistryCatalog.Exists(app.Location);
    }

    private static bool WaitIndefinitely(Process process)
    {
        process.WaitForExit();
        return true;
    }

    private static string? NormalizeProductCode(string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        return Guid.TryParse(productCode.Trim().Trim('{', '}'), out var guid)
            ? $"{{{guid.ToString("D").ToUpperInvariant()}}}"
            : null;
    }
}
