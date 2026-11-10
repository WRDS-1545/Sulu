using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sulu.Core;
using Sulu.Infrastructure;

namespace Sulu.Cli;

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> ListOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "filter",
        "sort",
        "limit",
    };

    private static readonly HashSet<string> ListFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "all",
        "json",
        "verbose",
        "refresh",
    };

    private static readonly HashSet<string> ScanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "index",
    };

    private static readonly HashSet<string> ScanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json",
    };

    private static readonly HashSet<string> UninstallOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "index",
        "timeout",
        "settle",
        "backup-dir",
    };

    private static readonly HashSet<string> UninstallFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes",
        "quiet",
        "dry-run",
        "no-clean",
        "pause",
        "no-pause",
    };

    private static readonly HashSet<string> MigrateOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "index",
        "to",
        "from",
        "backup-dir",
        "restore",
    };

    private static readonly HashSet<string> MigrateFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes",
        "dry-run",
        "include-review",
        "keep-source",
        "registry-only",
        "force",
    };

    private static readonly HashSet<string> PurgeOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "index",
        "backup-dir",
    };

    private static readonly HashSet<string> PurgeFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes",
        "dry-run",
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintGeneralHelp();
            return ExitCodes.Success;
        }

        var parsed = CommandLine.Parse(args);
        if (parsed.HasFlag("no-color"))
        {
            ConsoleUi.DisableColor();
        }

        if (parsed.HasFlag("version"))
        {
            Console.WriteLine($"Sulu {WindowsInfo.Version}");
            return ExitCodes.Success;
        }

        if (parsed.Name is null)
        {
            if (parsed.HasFlag("help"))
            {
                PrintGeneralHelp();
                return ExitCodes.Success;
            }

            throw new CliException("No command specified. Run 'sulu --help'.", ExitCodes.Usage);
        }

        var command = parsed.Name.ToLowerInvariant();
        if (parsed.HasFlag("help"))
        {
            PrintCommandHelp(command);
            return ExitCodes.Success;
        }

        return command switch
        {
            "list" or "ls" => RunList(parsed),
            "uninstall" or "remove" => RunUninstall(parsed),
            "scan" => RunScan(parsed),
            "purge" or "force-delete" => RunPurge(parsed),
            "migrate" or "move" => RunMigrate(parsed),
            "help" => RunHelp(parsed),
            _ => throw new CliException(
                $"Unknown command '{parsed.Name}'. Run 'sulu --help'.",
                ExitCodes.Usage),
        };
    }

    private static int RunList(ParsedCommand parsed)
    {
        CommandLine.Validate(parsed, ListOptions, ListFlags);
        if (parsed.Positionals.Count > 1)
        {
            throw new CliException("Usage: sulu list [filter] [options].", ExitCodes.Usage);
        }

        if (parsed.Positionals.Count == 1 && parsed.HasOption("filter"))
        {
            throw new CliException(
                "Use either a positional filter or --filter, not both.",
                ExitCodes.Usage);
        }

        var filter = parsed.GetOption("filter")
            ?? (parsed.Positionals.Count == 1 ? parsed.Positionals[0] : null);
        var includeHidden = parsed.HasFlag("all");
        var json = parsed.HasFlag("json");
        var verbose = parsed.HasFlag("verbose");
        var refresh = parsed.HasFlag("refresh");
        var sort = (parsed.GetOption("sort") ?? "name").ToLowerInvariant();
        if (sort is not ("name" or "publisher" or "date" or "size"))
        {
            throw new CliException("--sort must be one of: name, publisher, date, size.", ExitCodes.Usage);
        }

        var limit = ParseIntOption(parsed, "limit", defaultValue: 0, minimum: 0, maximum: 10_000);
        var indexed = AppIndexService.LoadIndexedCatalog(refresh);
        PrintIndexWarnings(indexed.Warnings, toStandardError: json);
        var allApps = indexed.Apps;
        var hiddenCount = allApps.Count(app => app.IsHidden);
        var indexFilter = 0;
        var isIndexFilter = !string.IsNullOrWhiteSpace(filter)
            && TryParseIndexReference(filter.Trim(), out indexFilter);
        List<InstalledApp> apps;
        if (isIndexFilter)
        {
            // An explicit index should work even for hidden system/update entries.
            apps = allApps.Where(app => app.Index == indexFilter).ToList();
        }
        else
        {
            apps = includeHidden ? allApps.ToList() : allApps.Where(app => !app.IsHidden).ToList();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var text = filter.Trim();
                apps = apps.Where(app =>
                        app.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                        || (app.Publisher?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)
                        || app.Location.Path.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        apps = SortApps(apps, sort);
        var totalMatches = apps.Count;
        if (limit > 0)
        {
            apps = apps.Take(limit).ToList();
        }

        if (json)
        {
            WriteJson(new
            {
                schemaVersion = 2,
                generatedAt = DateTimeOffset.Now,
                count = apps.Count,
                totalMatches,
                hiddenCount,
                indexRefreshed = refresh,
                staleIndexEntriesRemoved = indexed.RemovedEntries,
                indexesReassigned = indexed.RenumberedEntries,
                indexSaved = indexed.IndexSaved,
                apps = apps.Select(app => new
                {
                    index = app.Index,
                    refName = app.Ref,
                    id = app.Id,
                    name = app.Name,
                    version = app.Version,
                    publisher = app.Publisher,
                    installLocation = app.InstallLocation,
                    uninstallString = app.UninstallString,
                    quietUninstallString = app.QuietUninstallString,
                    productCode = app.ProductCode,
                    source = app.Location.Code,
                    keyPath = app.Location.Path,
                    estimatedSizeBytes = app.EstimatedSizeBytes,
                    installDate = app.InstallDate,
                    hidden = app.IsHidden,
                }),
            });
            return ExitCodes.Success;
        }

        ConsoleUi.Title($"Installed software ({apps.Count})");
        if (refresh)
        {
            if (!indexed.IndexSaved)
            {
                ConsoleUi.Warning("The index refresh could not be written to disk; see the warning above.");
            }
            else if (indexed.RemovedEntries > 0)
            {
                ConsoleUi.Success(
                    $"Index refreshed: removed {indexed.RemovedEntries} stale "
                    + $"entr{(indexed.RemovedEntries == 1 ? "y" : "ies")}; "
                    + $"renumbered {indexed.RenumberedEntries} application(s).");
            }
            else if (indexed.RenumberedEntries > 0)
            {
                ConsoleUi.Success(
                    $"Index refreshed: renumbered {indexed.RenumberedEntries} application(s).");
            }
            else
            {
                ConsoleUi.Muted("Index refreshed; no installed applications were found.");
            }

            ConsoleUi.Muted($"Index file: {SuluPaths.IndexFile}");
        }

        if (apps.Count == 0)
        {
            ConsoleUi.Muted("No installed software matched.");
        }
        else if (verbose)
        {
            PrintVerboseList(apps);
        }
        else
        {
            var headers = new[] { "Index", "Name", "Version", "Publisher", "Source", "Size" };
            var rows = apps.Select(app => new[]
            {
                app.Ref,
                app.Name,
                app.DisplayVersion,
                app.DisplayPublisher,
                app.SourceLabel,
                PathTools.FormatBytes(app.EstimatedSizeBytes),
            }).ToList();
            var caps = new[] { 6, ListNameWidth(), 18, 24, 7, 10 };
            TableRenderer.Render(
                headers,
                rows,
                caps,
                rightAligned: new HashSet<int> { 0, 5 },
                wrapColumns: new HashSet<int> { 1 });
        }

        var hiddenNote = includeHidden || isIndexFilter || hiddenCount == 0
            ? null
            : $"{hiddenCount} hidden system/update entr{(hiddenCount == 1 ? "y" : "ies")} omitted. Use --all to include.";
        if (hiddenNote is not null)
        {
            ConsoleUi.Muted(hiddenNote);
        }

        if (limit > 0 && totalMatches > apps.Count)
        {
            ConsoleUi.Muted($"Showing {apps.Count} of {totalMatches} matches.");
        }

        if (apps.Count > 0)
        {
            ConsoleUi.Muted(
                "Use the Index for short commands (for example 'sulu uninstall 12'), "
                + "or --verbose/--json for full names and registry IDs.");
        }

        return ExitCodes.Success;
    }

    private static int RunUninstall(ParsedCommand parsed)
    {
        CommandLine.Validate(parsed, UninstallOptions, UninstallFlags);
        var indexed = AppIndexService.LoadIndexedCatalog();
        PrintIndexWarnings(indexed.Warnings);
        var apps = indexed.Apps;
        var app = ResolveApp(parsed, apps);
        var yes = parsed.HasFlag("yes");
        var quiet = parsed.HasFlag("quiet");
        var dryRun = parsed.HasFlag("dry-run");
        var noClean = parsed.HasFlag("no-clean");
        var pause = parsed.HasFlag("pause");
        var noPause = parsed.HasFlag("no-pause");
        if (pause && noPause)
        {
            throw new CliException("Use either --pause or --no-pause, not both.", ExitCodes.Usage);
        }

        if (pause && !dryRun && !noClean && Console.IsInputRedirected)
        {
            throw new CliException(
                "--pause requires interactive stdin. Re-run with --no-pause or in an interactive terminal.",
                ExitCodes.Cancelled);
        }

        var timeout = TimeSpan.FromSeconds(
            ParseIntOption(parsed, "timeout", defaultValue: 1800, minimum: 0, maximum: 86_400));
        var settle = TimeSpan.FromSeconds(
            ParseIntOption(parsed, "settle", defaultValue: 5, minimum: 0, maximum: 300));
        var backupDir = parsed.GetOption("backup-dir");

        ConsoleUi.Title("Uninstall software");
        PrintApp(app);
        var plan = UninstallService.BuildPlan(app, quiet);
        ConsoleUi.KeyValue("Command", plan.DisplayCommand);

        if (plan.QuietFallback)
        {
            ConsoleUi.Warning(
                "No QuietUninstallString is registered. The normal uninstaller command will be used; it may show a UI.");
        }

        if (dryRun)
        {
            ConsoleUi.Section("Dry run");
            ConsoleUi.Warning("No uninstall command was run and no registry data was changed.");
            var preview = RegistryScanner.Scan(AppIdentity.From(app), assumeUninstalled: true);
            PrintScanResult(preview);
            ConsoleUi.Muted("Run the same command without --dry-run to perform the uninstall.");
            return ExitCodes.Success;
        }

        if (app.Location.IsMachine && !WindowsInfo.IsElevated)
        {
            throw new CliException(
                $"'{app.Name}' is registered for all users (HKLM). Run this command from an elevated terminal.",
                ExitCodes.ElevationRequired);
        }

        if (!yes)
        {
            ConsoleUi.Warning(
                "The vendor uninstaller will run. After it exits successfully, safe registry leftovers will be shown and cleaned.");
            if (!ConsoleUi.Confirm("Continue?", defaultNo: true))
            {
                ConsoleUi.Warning("Cancelled.");
                return ExitCodes.Cancelled;
            }
        }

        ConsoleUi.Info(string.Empty);
        ConsoleUi.Info("Running uninstaller...");
        var result = UninstallService.Run(plan, quiet, timeout);
        switch (result.Status)
        {
            case UninstallRunStatus.TimedOut:
                ConsoleUi.Error(result.Error ?? "The uninstaller timed out.");
                ConsoleUi.Warning("Registry cleanup was skipped because the uninstaller is still running.");
                return ExitCodes.UninstallFailed;

            case UninstallRunStatus.StartFailed:
                ConsoleUi.Error(result.Error ?? "The uninstaller could not be started.");
                ConsoleUi.Warning("Registry cleanup was skipped.");
                return ExitCodes.UninstallFailed;
        }

        var exitCode = result.ExitCode ?? -1;
        if (exitCode == 1602)
        {
            ConsoleUi.Warning("The uninstaller reported that the operation was cancelled (MSI exit code 1602).");
            ConsoleUi.Warning("Registry cleanup was skipped.");
            return ExitCodes.Cancelled;
        }

        if (exitCode is not (0 or 3010 or 1641))
        {
            ConsoleUi.Error($"The uninstaller failed with exit code {exitCode.ToString(CultureInfo.InvariantCulture)}.");
            ConsoleUi.Warning("Registry cleanup was skipped. The uninstall entry was left untouched.");
            return ExitCodes.UninstallFailed;
        }

        ConsoleUi.Success($"Uninstaller exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.");
        if (exitCode is 3010 or 1641)
        {
            ConsoleUi.Warning("A restart is required to finish the uninstall.");
        }

        if (noClean)
        {
            ConsoleUi.Muted("Registry cleanup skipped because --no-clean was specified.");
            return ExitCodes.Success;
        }

        var shouldPause = pause || (!yes && !noPause);
        if (shouldPause)
        {
            ConsoleUi.WaitForEnter(
                "The uninstaller process has exited, but some installers start a helper and return early. "
                + "Wait until the vendor uninstaller has fully finished, then press Enter to scan registry leftovers.");
        }
        else if (yes && !noPause)
        {
            ConsoleUi.Muted(
                "Skipping the Enter wait because --yes is set. Use --pause to force the wait in automated mode.");
        }

        if (settle > TimeSpan.Zero)
        {
            ConsoleUi.Muted("Waiting briefly for registry changes to settle...");
            var removed = UninstallService.WaitForUninstallEntryRemoval(app, settle);
            if (!removed)
            {
                ConsoleUi.Warning(
                    "The uninstall entry is still present. It will be protected during cleanup; the uninstaller may not have finished.");
            }
        }

        ConsoleUi.Section("Registry leftover scan");
        var scan = RegistryScanner.Scan(AppIdentity.From(app));
        PrintScanResult(scan);
        if (scan.AppStillInstalled)
        {
            ConsoleUi.Warning(
                "The app is still registered as installed. Live settings are protected and will not be cleaned automatically.");
        }

        var candidates = scan.Findings.Where(finding => finding.AutoClean).ToList();
        if (candidates.Count == 0)
        {
            ConsoleUi.Success("No safe automatic registry cleanup is available.");
            return ExitCodes.Success;
        }

        return ExecuteCleanup(app, scan, yes, dryRun: false, backupDir);
    }

    private static int RunScan(ParsedCommand parsed)
    {
        CommandLine.Validate(parsed, ScanOptions, ScanFlags);
        var json = parsed.HasFlag("json");
        var indexed = AppIndexService.LoadIndexedCatalog();
        PrintIndexWarnings(indexed.Warnings, toStandardError: json);
        var apps = indexed.Apps;
        var app = ResolveApp(parsed, apps);
        var scan = RegistryScanner.Scan(AppIdentity.From(app));

        if (json)
        {
            WriteJson(BuildScanJson(app, scan));
            return ExitCodes.Success;
        }

        ConsoleUi.Title("Registry footprint scan");
        PrintApp(app);
        PrintScanResult(scan);
        if (scan.AppStillInstalled)
        {
            ConsoleUi.Muted(
                "This app is still registered as installed. Its current uninstall and startup settings are protected from automatic cleanup.");
        }

        return ExitCodes.Success;
    }

    private static int RunPurge(ParsedCommand parsed)
    {
        CommandLine.Validate(parsed, PurgeOptions, PurgeFlags);
        var indexed = AppIndexService.LoadIndexedCatalog();
        PrintIndexWarnings(indexed.Warnings);
        var app = ResolveApp(parsed, indexed.Apps);
        var yes = parsed.HasFlag("yes");
        var dryRun = parsed.HasFlag("dry-run");
        var backupDir = parsed.GetOption("backup-dir");

        ConsoleUi.Title("Force-delete registry entries");
        PrintApp(app);
        ConsoleUi.Warning(
            "This removes registry entries only. It does not run the vendor uninstaller and does not delete files.");

        var sharingApps = indexed.Apps
            .Where(candidate =>
                !candidate.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                && PathTools.IsSamePath(candidate.InstallLocation, app.InstallLocation))
            .Take(6)
            .ToList();
        if (sharingApps.Count > 0)
        {
            ConsoleUi.Warning(
                "Other registered applications share the recorded install directory: "
                + string.Join(", ", sharingApps.Select(candidate => $"{candidate.Ref} {candidate.Name}")));
            ConsoleUi.Warning(
                "Delete the directory manually only after checking that no other application still needs it.");
        }

        var scan = RegistryScanner.ScanForPurge(app);
        PrintScanResult(scan);
        if (scan.Findings.Count == 0)
        {
            ConsoleUi.Success("No related registry entries were found.");
            return ExitCodes.Success;
        }

        var elevated = WindowsInfo.IsElevated;
        if (!dryRun && scan.Findings.Any(finding => finding.RequiresElevation) && !elevated)
        {
            throw new CliException(
                "These registry entries include machine-wide values. Run this command from an elevated terminal.",
                ExitCodes.ElevationRequired);
        }

        var result = ExecuteCleanup(app, scan, yes, dryRun, backupDir, force: true);
        if (result == ExitCodes.Success)
        {
            PrintManualDirectoryRemoval(
                app.InstallLocation,
                "Application directory",
                dryRun
                    ? "Dry run only; no registry entries were deleted. This is the directory command "
                      + "a real purge would print:"
                    : "The application directory is intentionally kept. Do not delete it if you still need the files.");
        }

        return result;
    }

    private static int RunMigrate(ParsedCommand parsed)
    {
        CommandLine.Validate(parsed, MigrateOptions, MigrateFlags);
        if (parsed.HasOption("restore"))
        {
            return RunRestore(parsed);
        }

        var toOption = parsed.GetOption("to");
        if (string.IsNullOrWhiteSpace(toOption))
        {
            throw new CliException(
                "Usage: sulu migrate <index|name> --to <new-directory> [options].",
                ExitCodes.Usage);
        }

        var indexed = AppIndexService.LoadIndexedCatalog();
        PrintIndexWarnings(indexed.Warnings);
        var app = ResolveApp(parsed, indexed.Apps);
        var dryRun = parsed.HasFlag("dry-run");
        var yes = parsed.HasFlag("yes");
        var includeReview = parsed.HasFlag("include-review");
        var registryOnly = parsed.HasFlag("registry-only");
        var force = parsed.HasFlag("force");
        if (parsed.HasFlag("keep-source"))
        {
            ConsoleUi.Muted("--keep-source is now the default for migration; the old directory is always kept.");
        }
        if (app.IsMsi)
        {
            throw new CliException(
                $"'{app.Name}' is an MSI product. Moving an installed MSI outside its component database "
                + "would break repair and upgrade. Uninstall and reinstall it at the new location instead.",
                ExitCodes.MigrationFailed);
        }

        var oldInput = parsed.GetOption("from") ?? app.InstallLocation;
        if (string.IsNullOrWhiteSpace(oldInput))
        {
            throw new CliException(
                "The app has no InstallLocation. Provide the current directory with --from.",
                ExitCodes.Usage);
        }

        var oldPath = ResolveMigrationPath(oldInput, "--from");
        var newPath = ResolveMigrationPath(toOption, "--to");
        if (PathTools.IsSamePath(oldPath, newPath))
        {
            throw new CliException("The old and new directories must be different.", ExitCodes.Usage);
        }

        if (PathTools.IsSameOrUnder(newPath, oldPath) || PathTools.IsSameOrUnder(oldPath, newPath))
        {
            throw new CliException(
                "The old and new directories must not contain each other.",
                ExitCodes.Usage);
        }

        if (!registryOnly && !Directory.Exists(oldPath))
        {
            throw new CliException($"The old directory does not exist: {oldPath}", ExitCodes.MigrationFailed);
        }

        if (File.Exists(newPath))
        {
            throw new CliException($"A file already exists at the new path: {newPath}", ExitCodes.MigrationFailed);
        }

        if (!registryOnly
            && Directory.Exists(newPath)
            && Directory.EnumerateFileSystemEntries(newPath).Any())
        {
            throw new CliException(
                $"The new directory is not empty: {newPath}. Choose an empty or non-existent directory.",
                ExitCodes.MigrationFailed);
        }

        if (registryOnly && !dryRun && !Directory.Exists(newPath))
        {
            throw new CliException(
                $"With --registry-only, the new directory must already exist: {newPath}",
                ExitCodes.MigrationFailed);
        }

        if (!PathTools.IsSamePath(oldPath, app.InstallLocation))
        {
            ConsoleUi.Warning(
                $"The registry InstallLocation is '{app.DisplayInstallLocation}', "
                + $"but the migration will use --from '{oldPath}'.");
        }

        var sharingApps = indexed.Apps
            .Where(candidate =>
                !candidate.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                && PathTools.IsSamePath(candidate.InstallLocation, oldPath))
            .Take(6)
            .ToList();
        if (sharingApps.Count > 0)
        {
            ConsoleUi.Warning(
                "Other registered applications share this install directory: "
                + string.Join(", ", sharingApps.Select(candidate => $"{candidate.Ref} {candidate.Name}")));
            if (!dryRun && !force)
            {
                throw new CliException(
                    "Refusing to move a directory shared with other registered applications. "
                    + "Use --force only after checking that every sharing application can move with it.",
                    ExitCodes.MigrationFailed);
            }
        }

        var elevated = WindowsInfo.IsElevated;
        var protectedFiles = !registryOnly
            && (PathTools.IsSystemProtectedPath(oldPath) || PathTools.IsSystemProtectedPath(newPath));
        if (!dryRun
            && !elevated
            && (app.Location.IsMachine || protectedFiles))
        {
            throw new CliException(
                "Moving this application or changing its machine-wide registry data requires an elevated terminal.",
                ExitCodes.ElevationRequired);
        }

        ConsoleUi.Title("Migrate application");
        PrintApp(app);
        ConsoleUi.KeyValue("From", oldPath);
        ConsoleUi.KeyValue("To", newPath);
        ConsoleUi.KeyValue("Mode", registryOnly ? "registry only, keep files" : "copy files, keep source");

        if (!registryOnly)
        {
            var running = FileMigrationService.FindRunningProcesses(oldPath);
            if (running.Count > 0)
            {
                ConsoleUi.Warning($"These processes are running from the old directory: {string.Join(", ", running)}");
                if (!dryRun && !force)
                {
                    throw new CliException(
                        "Close the application first, or use --force if you have verified that continuing is safe.",
                        ExitCodes.MigrationFailed);
                }
            }
        }

        ConsoleUi.Section("Scan registry references");
        ConsoleUi.Muted("Scanning HKLM, HKCU, HKU, and HKCC in both registry views; this can take a moment.");
        MigrationScanResult scan;
        try
        {
            scan = RegistryReferenceScanner.Scan(app, oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            throw new CliException(
                $"The registry reference scan failed: {ex.Message}",
                ExitCodes.MigrationFailed);
        }

        PrintMigrationScan(scan);

        var serviceReferences = scan.References.Where(reference => reference.ServiceReference).ToList();
        if (!registryOnly && serviceReferences.Count > 0 && !(force && includeReview))
        {
            var serviceMessage =
                $"Found {serviceReferences.Count} service/driver reference(s) under the old directory. "
                + "Moving those files without updating the service path would break the service. "
                + "Stop or reconfigure the service, then re-run with both --include-review and --force "
                + "after verifying every service reference.";
            if (!dryRun)
            {
                throw new CliException(serviceMessage, ExitCodes.MigrationFailed);
            }

            ConsoleUi.Warning(serviceMessage);
        }

        if (registryOnly
            && serviceReferences.Count > 0
            && !(force && includeReview))
        {
            ConsoleUi.Warning(
                $"{serviceReferences.Count} service/driver reference(s) will be left unchanged. "
                + "Use --include-review and --force only after stopping or reconfiguring the service.");
        }

        bool IsSelected(RegistryValueReference reference) =>
            reference.Safe
            || (includeReview && (!reference.ServiceReference || force));
        var selected = scan.References.Where(IsSelected).ToList();
        var selectedRowIds = selected.Select(reference => reference.RowId).ToHashSet();
        var notSelected = scan.References.Count - selected.Count;

        if (selected.Count == 0)
        {
            ConsoleUi.Warning(
                "No registry values are selected. Moving files alone can leave the application pointing at the old path.");
        }

        if (notSelected > 0)
        {
            ConsoleUi.Warning(
                $"{notSelected} review reference(s) will be left unchanged. Use --include-review "
                + "only after checking that those values belong to this application.");
        }

        var selectedRequiresElevation = selected.Any(reference => reference.RequiresElevation);
        if (!dryRun && selectedRequiresElevation && !elevated)
        {
            throw new CliException(
                "The selected registry references include machine-wide values. Run this command from an elevated terminal.",
                ExitCodes.ElevationRequired);
        }

        if (dryRun)
        {
            ConsoleUi.Section("Dry run");
            ConsoleUi.Info(
                registryOnly
                    ? $"{selected.Count} registry value(s) would be changed."
                    : $"{selected.Count} registry value(s) would be changed, then files would be copied to the new path.");
            if (!registryOnly)
            {
                ConsoleUi.Info($"The old directory would be kept: {oldPath}");
            }

            ConsoleUi.Warning("No files or registry values were changed.");
            return ExitCodes.Success;
        }

        if (!yes)
        {
            ConsoleUi.Warning(
                registryOnly
                    ? $"This will rewrite {selected.Count} registry value(s). Files will not be moved."
                    : $"This will copy files to '{newPath}' and rewrite {selected.Count} registry value(s). "
                      + "The old directory will be kept for rollback.");
            if (!ConsoleUi.Confirm("Continue?", defaultNo: true))
            {
                ConsoleUi.Warning("Migration cancelled.");
                return ExitCodes.Cancelled;
            }
        }

        var changes = scan.References.Select(reference => new MigrationChange
        {
            Hive = reference.Hive.Name,
            View = reference.Hive.ViewName,
            SubKey = reference.SubKey,
            ValueName = reference.ValueName,
            Kind = reference.Kind.ToString(),
            CurrentValue = reference.CurrentValue,
            ProposedValue = reference.ProposedValue,
            Tier = reference.Category,
            Reason = reference.Reason,
            Selected = selectedRowIds.Contains(reference.RowId),
        }).ToList();

        MigrationPlanFile planFile;
        try
        {
            planFile = MigrationPlanStore.Create(
                app,
                oldPath,
                newPath,
                registryOnly,
                changes,
                parsed.GetOption("backup-dir"));
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            throw new CliException(
                $"Migration aborted because its backup record could not be created: {ex.Message}",
                ExitCodes.CleanupFailed);
        }

        ConsoleUi.Success($"Migration record: {planFile.PlanPath}");
        ConsoleUi.Muted($"Review CSV: {planFile.CsvPath}");

        if (!registryOnly)
        {
            var targetWasPresent = Directory.Exists(newPath);
            ConsoleUi.Section("Copy files");
            var copy = FileMigrationService.CopyDirectory(oldPath, newPath);
            foreach (var warning in copy.Warnings)
            {
                ConsoleUi.Warning(warning);
            }

            if (!copy.Success)
            {
                ConsoleUi.Error($"File copy failed: {copy.Error}");
                if (!targetWasPresent)
                {
                    var partialCleanup = FileMigrationService.DeleteDirectory(newPath);
                    if (!partialCleanup.Success)
                    {
                        ConsoleUi.Warning(
                            $"The partial target copy could not be removed: {partialCleanup.Error}");
                    }
                }

                ConsoleUi.Warning(
                    "No registry values were changed. The source directory is still intact.");
                ConsoleUi.Info($"Backup record: {planFile.PlanPath}");
                return ExitCodes.MigrationFailed;
            }

            planFile.Plan.FilesCopied = true;
            TrySavePlan(planFile.Plan, planFile.PlanPath);
            ConsoleUi.Success("File copy completed.");
            ConsoleUi.Muted(
                "Shortcuts, scheduled tasks, services, file associations, and config files outside "
                + "the copied tree are not updated automatically.");
        }

        ConsoleUi.Section("Update registry");
        var apply = RegistryReferenceEditor.Apply(planFile.Plan, elevated);
        planFile.Plan.Completed = apply.Failures.Count == 0;
        TrySavePlan(planFile.Plan, planFile.PlanPath);
        if (apply.Failures.Count > 0)
        {
            foreach (var failure in apply.Failures)
            {
                ConsoleUi.Error(failure);
            }

            ConsoleUi.Warning("The registry update failed; rolling back values already changed...");
            var rollback = RegistryReferenceEditor.Restore(planFile.Plan, elevated, force: false);
            TrySavePlan(planFile.Plan, planFile.PlanPath);
            if (rollback.Failures.Count > 0)
            {
                foreach (var failure in rollback.Failures)
                {
                    ConsoleUi.Error(failure);
                }

                ConsoleUi.Warning(
                    "The automatic registry rollback was incomplete. The source files remain at the old path. "
                    + $"Use the migration record for manual recovery: {planFile.PlanPath}");
            }
            else
            {
                ConsoleUi.Success("Registry values were rolled back.");
            }

            return ExitCodes.MigrationFailed;
        }

        ConsoleUi.Success($"Registry updated: {apply.AppliedCount} value(s) changed.");

        PrintManualDirectoryRemoval(
            oldPath,
            "Old application directory",
            "The old application directory is intentionally kept for rollback. After verifying that "
            + "the application starts and works from the new location, delete the old directory manually:");

        AppIndexService.TryUpdateInstallLocation(app.Id, newPath, out var indexWarning);
        if (!string.IsNullOrWhiteSpace(indexWarning))
        {
            ConsoleUi.Warning(indexWarning);
        }

        if (notSelected > 0)
        {
            ConsoleUi.Warning(
                $"{notSelected} review reference(s) still point at the old path. They are listed in the review CSV.");
        }

        ConsoleUi.Success("Migration completed.");
        ConsoleUi.Muted($"Rollback: sulu migrate --restore \"{planFile.PlanPath}\"");
        return ExitCodes.Success;
    }

    private static int RunRestore(ParsedCommand parsed)
    {
        if (parsed.Positionals.Count > 0
            || parsed.HasOption("to")
            || parsed.HasOption("from")
            || parsed.HasOption("id")
            || parsed.HasOption("index")
            || parsed.HasOption("backup-dir")
            || parsed.HasFlag("include-review"))
        {
            throw new CliException(
                "With --restore, only --yes, --dry-run, --force, --registry-only, and --keep-source are valid.",
                ExitCodes.Usage);
        }

        var restorePath = parsed.GetOption("restore");
        if (string.IsNullOrWhiteSpace(restorePath))
        {
            throw new CliException("--restore requires a migration.json path or directory.", ExitCodes.Usage);
        }

        var planPath = Directory.Exists(restorePath.Trim())
            ? Path.Combine(restorePath.Trim(), "migration.json")
            : Path.GetFullPath(restorePath.Trim());
        MigrationPlan plan;
        try
        {
            plan = MigrationPlanStore.Load(planPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            throw new CliException($"Could not read the migration record: {ex.Message}", ExitCodes.MigrationFailed);
        }

        var oldPath = TryNormalizeSafePath(plan.OldPath);
        var newPath = TryNormalizeSafePath(plan.NewPath);
        if (oldPath is null || newPath is null)
        {
            throw new CliException(
                "The migration record contains an unsafe or invalid directory path.",
                ExitCodes.MigrationFailed);
        }

        var dryRun = parsed.HasFlag("dry-run");
        var yes = parsed.HasFlag("yes");
        var force = parsed.HasFlag("force");
        var keepSource = parsed.HasFlag("keep-source");
        var registryOnly = plan.RegistryOnly || parsed.HasFlag("registry-only");
        var selected = plan.Changes.Where(change => change.Selected).ToList();
        var elevated = WindowsInfo.IsElevated;
        var filesToRestore = !registryOnly
            && Directory.Exists(newPath)
            && !Directory.Exists(oldPath);
        var protectedFiles = filesToRestore
            && (PathTools.IsSystemProtectedPath(oldPath) || PathTools.IsSystemProtectedPath(newPath));

        ConsoleUi.Title("Restore application migration");
        ConsoleUi.KeyValue("App", plan.AppName);
        ConsoleUi.KeyValue("From", newPath);
        ConsoleUi.KeyValue("To", oldPath);
        ConsoleUi.KeyValue("Values", selected.Count.ToString(CultureInfo.InvariantCulture));
        ConsoleUi.KeyValue("Files", filesToRestore ? "copy back to old path" : "no automatic file restore");

        if (plan.Changes.Count == 0 && !filesToRestore)
        {
            ConsoleUi.Warning("Nothing is recorded for restoration.");
            return ExitCodes.Success;
        }

        if (!registryOnly && !Directory.Exists(oldPath) && !Directory.Exists(newPath))
        {
            throw new CliException(
                "Neither the old nor the new application directory exists; the files cannot be restored.",
                ExitCodes.MigrationFailed);
        }

        if (!dryRun
            && !elevated
            && (selected.Any(change => change.RequiresElevation) || protectedFiles))
        {
            throw new CliException(
                "Restoring this migration requires an elevated terminal.",
                ExitCodes.ElevationRequired);
        }

        if (plan.Restored)
        {
            ConsoleUi.Warning("This migration record is already marked as restored.");
        }

        if (dryRun)
        {
            ConsoleUi.Section("Dry run");
            ConsoleUi.Info($"{selected.Count} registry value(s) would be restored.");
            if (filesToRestore)
            {
                ConsoleUi.Info($"Files would be copied from '{newPath}' back to '{oldPath}'.");
            }

            ConsoleUi.Warning("No files or registry values were changed.");
            return ExitCodes.Success;
        }

        if (!yes)
        {
            ConsoleUi.Warning(
                "This will restore the recorded registry values. Values changed since the migration "
                + "will be skipped unless --force is used.");
            if (filesToRestore)
            {
                ConsoleUi.Warning(
                    $"Files will first be copied back to '{oldPath}', then the migrated copy at '{newPath}' "
                    + (keepSource ? "will be kept." : "will be removed."));
            }

            if (!ConsoleUi.Confirm("Continue?", defaultNo: true))
            {
                ConsoleUi.Warning("Restore cancelled.");
                return ExitCodes.Cancelled;
            }
        }

        var copiedBack = false;
        if (filesToRestore)
        {
            ConsoleUi.Section("Copy files back");
            var copy = FileMigrationService.CopyDirectory(newPath, oldPath);
            foreach (var warning in copy.Warnings)
            {
                ConsoleUi.Warning(warning);
            }

            if (!copy.Success)
            {
                ConsoleUi.Error($"File restore failed: {copy.Error}");
                var partialCleanup = FileMigrationService.DeleteDirectory(oldPath);
                if (!partialCleanup.Success)
                {
                    ConsoleUi.Warning(
                        $"The partial old-path copy could not be removed: {partialCleanup.Error}");
                }

                ConsoleUi.Warning("The migrated copy is still intact; no registry values were changed.");
                return ExitCodes.MigrationFailed;
            }

            copiedBack = true;
            ConsoleUi.Success("Files copied back to the old path.");
        }

        ConsoleUi.Section("Restore registry");
        var restore = RegistryReferenceEditor.Restore(plan, elevated, force);
        plan.Restored = restore.Failures.Count == 0;
        plan.Completed = false;
        TrySavePlan(plan, planPath);
        if (restore.Failures.Count > 0)
        {
            foreach (var failure in restore.Failures)
            {
                ConsoleUi.Error(failure);
            }

            ConsoleUi.Warning(
                "The registry restore was incomplete. The migrated file copy remains available at the new path.");
            return ExitCodes.MigrationFailed;
        }

        ConsoleUi.Success($"Registry restored: {restore.AppliedCount} value(s) changed.");

        var migratedCopyRemoved = false;
        if (copiedBack && !keepSource)
        {
            var deletion = FileMigrationService.DeleteDirectory(newPath);
            if (!deletion.Success)
            {
                ConsoleUi.Warning($"The migrated copy could not be removed: {deletion.Error}");
                ConsoleUi.Muted($"Remove it manually when you are satisfied: {newPath}");
            }
            else
            {
                migratedCopyRemoved = true;
                ConsoleUi.Success("The migrated copy was removed.");
            }

            if (IsSuluQuarantinePath(plan.QuarantinePath, oldPath)
                && Directory.Exists(plan.QuarantinePath))
            {
                var quarantineDeletion = FileMigrationService.DeleteDirectory(plan.QuarantinePath);
                if (!quarantineDeletion.Success)
                {
                    ConsoleUi.Warning(
                        $"The old quarantine copy could not be removed: {quarantineDeletion.Error}");
                }
            }
        }

        if (!registryOnly && !keepSource && !migratedCopyRemoved)
        {
            PrintManualDirectoryRemoval(
                newPath,
                "Migrated application directory",
                "The registry now points to the restored old location. The migrated copy is no longer "
                + "referenced; after verifying the restore, delete it manually:");
        }

        AppIndexService.TryUpdateInstallLocation(plan.AppRegistryId, oldPath, out var indexWarning);
        if (!string.IsNullOrWhiteSpace(indexWarning))
        {
            ConsoleUi.Warning(indexWarning);
        }

        ConsoleUi.Success("Restore completed.");
        return ExitCodes.Success;
    }

    private static void PrintMigrationScan(MigrationScanResult scan)
    {
        foreach (var warning in scan.Warnings)
        {
            ConsoleUi.Warning(warning);
        }

        var elevationCount = scan.References.Count(reference => reference.RequiresElevation);
        ConsoleUi.Info(
            $"{scan.References.Count} reference(s) found; {scan.KeysScanned} registry key(s) and "
            + $"{scan.ValuesScanned} string value(s) scanned, {elevationCount} need elevation.");
        if (scan.ErrorCount > 0)
        {
            ConsoleUi.Muted($"{scan.ErrorCount} inaccessible registry location(s) were skipped.");
        }

        if (scan.References.Count == 0)
        {
            ConsoleUi.Warning(
                "No registry string values contain the old directory. The app may still have shortcuts, "
                + "scheduled tasks, services, or config files that need manual attention.");
            return;
        }

        var safe = scan.References.Count(reference => reference.Safe);
        var service = scan.References.Count(reference => reference.ServiceReference);
        ConsoleUi.Info(
            $"{safe} safe, {scan.References.Count - safe} review, {service} service/driver reference(s).");
        for (var index = 0; index < scan.References.Count; index++)
        {
            var reference = scan.References[index];
            ConsoleUi.Info($"  {index + 1,3}. [{reference.Category}] {reference.DisplayLocation}");
            ConsoleUi.Muted($"       {reference.Reason}");
            ConsoleUi.Info($"       old: {TruncateForDisplay(reference.CurrentValue, 220)}");
            ConsoleUi.Info($"       new: {TruncateForDisplay(reference.ProposedValue, 220)}");
        }
    }

    private static string ResolveMigrationPath(string raw, string optionName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new CliException(
                $"{optionName} must be an absolute directory path.",
                ExitCodes.Usage);
        }

        var normalized = PathTools.NormalizePath(expanded);
        if (normalized is null)
        {
            throw new CliException(
                $"{optionName} must be a directory below a drive root, not a drive root itself.",
                ExitCodes.Usage);
        }

        if (!PathTools.IsSafeAppRoot(normalized))
        {
            throw new CliException(
                $"{optionName} points at a shared system/user root, which is not a safe application directory: {normalized}",
                ExitCodes.Usage);
        }

        return normalized;
    }

    private static string? TryNormalizeSafePath(string raw)
    {
        var normalized = PathTools.NormalizePath(raw);
        return normalized is not null && PathTools.IsSafeAppRoot(normalized) ? normalized : null;
    }

    private static bool IsSuluQuarantinePath(string? candidate, string oldPath)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalizedCandidate = PathTools.NormalizePath(candidate);
        var normalizedOld = PathTools.NormalizePath(oldPath);
        if (normalizedCandidate is null || normalizedOld is null)
        {
            return false;
        }

        var candidateParent = Path.GetDirectoryName(normalizedCandidate);
        var oldParent = Path.GetDirectoryName(normalizedOld);
        var candidateLeaf = Path.GetFileName(normalizedCandidate);
        var oldLeaf = Path.GetFileName(normalizedOld);
        return !string.IsNullOrWhiteSpace(candidateParent)
            && !string.IsNullOrWhiteSpace(oldParent)
            && !string.IsNullOrWhiteSpace(candidateLeaf)
            && !string.IsNullOrWhiteSpace(oldLeaf)
            && candidateParent.Equals(oldParent, StringComparison.OrdinalIgnoreCase)
            && candidateLeaf.StartsWith($".{oldLeaf}.sulu-old-", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintManualDirectoryRemoval(string? path, string label, string warning)
    {
        var normalized = PathTools.NormalizePath(path);
        if (normalized is null)
        {
            return;
        }

        if (!PathTools.IsSafeAppRoot(normalized))
        {
            ConsoleUi.Warning(
                $"The recorded {label.ToLowerInvariant()} is a broad shared directory, "
                + "so no directory-removal command was generated.");
            return;
        }

        if (!Directory.Exists(normalized))
        {
            return;
        }

        var quoted = normalized.Replace("'", "''", StringComparison.Ordinal);
        ConsoleUi.Section("Manual directory cleanup");
        ConsoleUi.Warning(warning);
        ConsoleUi.Info($"  Remove-Item -LiteralPath '{quoted}' -Recurse -Force");
        ConsoleUi.Muted("This permanently deletes the directory and everything inside it. Run it only when appropriate.");
    }

    private static void TrySavePlan(MigrationPlan plan, string planPath)
    {
        try
        {
            MigrationPlanStore.Save(plan, planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            ConsoleUi.Warning($"Could not update the migration record: {ex.Message}");
        }
    }

    private static int ExecuteCleanup(
        InstalledApp app,
        RegistryScanResult scan,
        bool yes,
        bool dryRun,
        string? backupDir,
        bool force = false)
    {
        var autoCandidates = scan.Findings.Where(finding => finding.AutoClean).ToList();
        if (autoCandidates.Count == 0)
        {
            ConsoleUi.Warning(
                force
                    ? "No registry entries are available for forced deletion."
                    : "No registry entries are eligible for safe automatic cleanup.");
            return ExitCodes.Success;
        }

        var elevated = WindowsInfo.IsElevated;
        if (dryRun)
        {
            ConsoleUi.Section("Dry run");
            ConsoleUi.Info($"{autoCandidates.Count} registry item(s) would be deleted:");
            PrintCleanupTargets(autoCandidates);
            if (autoCandidates.Any(finding => finding.RequiresElevation) && !elevated)
            {
                ConsoleUi.Warning(
                    "Some targets are machine-wide and a real deletion will require an elevated terminal.");
            }

            ConsoleUi.Warning("No registry data was changed and no backup was created.");
            return ExitCodes.Success;
        }

        var permitted = autoCandidates
            .Where(finding => !finding.RequiresElevation || elevated)
            .ToList();
        var skippedForElevation = autoCandidates
            .Where(finding => finding.RequiresElevation && !elevated)
            .ToList();

        if (permitted.Count == 0)
        {
            ConsoleUi.Warning(
                "All eligible leftovers are under HKLM. Re-run from an elevated terminal to clean them.");
            return ExitCodes.ElevationRequired;
        }

        if (skippedForElevation.Count > 0)
        {
            ConsoleUi.Warning(
                $"{skippedForElevation.Count} HKLM leftover(s) require an elevated terminal and will be skipped.");
        }

        if (!yes)
        {
            ConsoleUi.Warning(
                force
                    ? $"This force-deletes {permitted.Count} registry item(s) for '{app.Name}'. "
                      + "Application files are not touched."
                    : $"This will delete {permitted.Count} registry item(s) that were identified as belonging to '{app.Name}'.");
            if (!ConsoleUi.Confirm("Continue?", defaultNo: true))
            {
                ConsoleUi.Warning("Registry cleanup cancelled.");
                return ExitCodes.Cancelled;
            }
        }

        BackupResult backup;
        try
        {
            backup = RegistryBackupService.CreateBackup(app.Name, app.Id, permitted, backupDir);
        }
        catch (BackupException ex)
        {
            throw new CliException(
                $"Registry cleanup aborted because the backup failed: {ex.Message}",
                ExitCodes.CleanupFailed);
        }

        ConsoleUi.Success($"Backup created: {backup.Directory}");
        ConsoleUi.Muted($"Backup contains {backup.Entries.Count} registry key export(s).");

        var cleanup = RegistryCleaner.Clean(permitted, elevated);
        foreach (var item in cleanup.Items)
        {
            if (item.Deleted)
            {
                ConsoleUi.Success($"Deleted: {item.Finding.DisplayLocation}");
            }
            else if (item.Error is not null)
            {
                ConsoleUi.Error($"Failed: {item.Finding.DisplayLocation} - {item.Error}");
            }
            else
            {
                ConsoleUi.Warning($"Skipped (already gone): {item.Finding.DisplayLocation}");
            }
        }

        var summary = $"Cleanup complete: {cleanup.DeletedCount} deleted, "
            + $"{cleanup.SkippedCount} skipped, {cleanup.FailedCount} failed.";
        if (cleanup.FailedCount > 0)
        {
            ConsoleUi.Warning(summary);
            ConsoleUi.Muted($"Restore registry exports with 'reg import' from: {backup.Directory}");
            return ExitCodes.CleanupFailed;
        }

        ConsoleUi.Success(summary);
        if (skippedForElevation.Count > 0)
        {
            ConsoleUi.Warning(
                $"{skippedForElevation.Count} HKLM item(s) were not cleaned because this process is not elevated.");
            return ExitCodes.ElevationRequired;
        }

        return ExitCodes.Success;
    }

    private static InstalledApp ResolveApp(ParsedCommand parsed, IReadOnlyList<InstalledApp> apps)
    {
        var id = parsed.GetOption("id");
        var indexOption = parsed.GetOption("index");
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(indexOption))
        {
            throw new CliException("Use either --id or --index, not both.", ExitCodes.Usage);
        }

        if (!string.IsNullOrWhiteSpace(indexOption))
        {
            if (parsed.Positionals.Count > 0)
            {
                throw new CliException("Use either a name query or --index, not both.", ExitCodes.Usage);
            }

            if (!TryParseIndexReference(indexOption, out var requestedIndex))
            {
                throw new CliException("--index must be a positive application index, for example --index 12.", ExitCodes.Usage);
            }

            var byIndex = AppIndexService.FindByIndex(apps, requestedIndex);
            if (byIndex is null)
            {
                throw new CliException(
                    $"No installed software has index #{requestedIndex}. Run 'sulu list' to see current indexes.",
                    ExitCodes.NotFoundOrAmbiguous);
            }

            return byIndex;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            if (parsed.Positionals.Count > 0)
            {
                throw new CliException(
                    "Use either a name query or --id, not both.",
                    ExitCodes.Usage);
            }

            var byId = RegistryCatalog.FindById(apps, id);
            if (byId is null)
            {
                throw new CliException(
                    $"No installed software has ID '{id.Trim()}'. Use 'sulu list --verbose' or 'sulu list --json' to find IDs.",
                    ExitCodes.NotFoundOrAmbiguous);
            }

            return byId;
        }

        if (parsed.Positionals.Count != 1)
        {
            throw new CliException(
                "Specify exactly one software index or name (or use --id/--index).",
                ExitCodes.Usage);
        }

        var query = parsed.Positionals[0].Trim();
        var explicitlyIndexed = query.StartsWith('#');
        var nameMatchesExactly = apps.Any(
            app => app.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if ((explicitlyIndexed || !nameMatchesExactly) && TryParseIndexReference(query, out var queryIndex))
        {
            var byIndex = AppIndexService.FindByIndex(apps, queryIndex);
            if (byIndex is not null)
            {
                return byIndex;
            }

            if (explicitlyIndexed)
            {
                throw new CliException(
                    $"No installed software has index #{queryIndex}. Run 'sulu list' to see current indexes.",
                    ExitCodes.NotFoundOrAmbiguous);
            }
        }

        if (query.Length < 2)
        {
            throw new CliException(
                "The search query must contain at least 2 characters. Use an index or --id for an exact match.",
                ExitCodes.NotFoundOrAmbiguous);
        }

        var matches = RegistryCatalog.Search(apps, query);
        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            throw new CliException(
                $"No installed software matched '{query}'. Run 'sulu list --filter {query}'.",
                ExitCodes.NotFoundOrAmbiguous);
        }

        var lines = new List<string>
        {
            $"'{query}' matched {matches.Count} software titles. Use an index or --id to choose one:",
        };
        foreach (var match in matches.Take(15))
        {
            lines.Add(
                $"  - {match.Ref}  {match.Name} {match.DisplayVersion} "
                + $"[{match.SourceLabel}]  id={match.Id}");
        }

        if (matches.Count > 15)
        {
            lines.Add($"  ... and {matches.Count - 15} more.");
        }

        throw new CliException(string.Join(Environment.NewLine, lines), ExitCodes.NotFoundOrAmbiguous);
    }

    private static void PrintIndexWarnings(IReadOnlyList<string> warnings, bool toStandardError = false)
    {
        foreach (var warning in warnings)
        {
            if (toStandardError)
            {
                ConsoleUi.WarningToStandardError(warning);
            }
            else
            {
                ConsoleUi.Warning(warning);
            }
        }
    }

    private static void PrintVerboseList(IReadOnlyList<InstalledApp> apps)
    {
        foreach (var app in apps)
        {
            ConsoleUi.Info(string.Empty);
            ConsoleUi.Info($"{app.Ref,6}  {app.Name}");
            ConsoleUi.KeyValue("Version", app.DisplayVersion);
            ConsoleUi.KeyValue("Publisher", app.DisplayPublisher);
            ConsoleUi.KeyValue("Source", app.SourceLabel);
            ConsoleUi.KeyValue("Size", PathTools.FormatBytes(app.EstimatedSizeBytes));
            ConsoleUi.KeyValue("Install location", app.DisplayInstallLocation);
            ConsoleUi.KeyValue("Registry", app.Id);
        }
    }

    private static int ListNameWidth()
    {
        var width = 120;
        try
        {
            if (Console.WindowWidth > 0)
            {
                width = Console.WindowWidth;
            }
        }
        catch (Exception ex) when (ex is IOException
            or PlatformNotSupportedException
            or ArgumentOutOfRangeException
            or System.Security.SecurityException)
        {
            // Redirected or non-interactive hosts may not expose a width.
        }

        return Math.Clamp(width - 83, 28, 72);
    }

    private static bool TryParseIndexReference(string value, out int index)
    {
        index = 0;
        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        return text.Length > 0
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index > 0;
    }

    private static List<InstalledApp> SortApps(IReadOnlyList<InstalledApp> apps, string sort)
    {
        IOrderedEnumerable<InstalledApp> ordered = sort switch
        {
            "publisher" => apps
                .OrderBy(app => app.Publisher ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => apps
                .OrderByDescending(app => ParseInstallDate(app.InstallDate) ?? DateTime.MinValue)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase),
            "size" => apps
                .OrderByDescending(app => app.EstimatedSizeBytes ?? -1)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => apps
                .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.SourceLabel, StringComparer.Ordinal),
        };

        return ordered.ToList();
    }

    private static DateTime? ParseInstallDate(string? value) =>
        DateTime.TryParseExact(
            value,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;

    private static int ParseIntOption(
        ParsedCommand parsed,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = parsed.GetOption(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new CliException(
                $"--{name} must be an integer between {minimum} and {maximum}.",
                ExitCodes.Usage);
        }

        return value;
    }

    private static void PrintApp(InstalledApp app)
    {
        ConsoleUi.KeyValue("Index", app.Ref);
        ConsoleUi.KeyValue("Name", app.Name);
        ConsoleUi.KeyValue("Version", app.DisplayVersion);
        ConsoleUi.KeyValue("Publisher", app.DisplayPublisher);
        ConsoleUi.KeyValue("Install location", app.DisplayInstallLocation);
        ConsoleUi.KeyValue("Registry", app.Id);
    }

    private static void PrintScanResult(RegistryScanResult scan)
    {
        if (scan.Warnings.Count > 0)
        {
            foreach (var warning in scan.Warnings)
            {
                ConsoleUi.Warning(warning);
            }
        }

        if (scan.Findings.Count == 0)
        {
            ConsoleUi.Success("No registry leftovers were found in the scanned locations.");
            return;
        }

        var autoCount = scan.Findings.Count(finding => finding.AutoClean);
        ConsoleUi.Info($"{scan.Findings.Count} finding(s), {autoCount} eligible for automatic cleanup:");
        for (var i = 0; i < scan.Findings.Count; i++)
        {
            var finding = scan.Findings[i];
            var marker = finding.AutoClean ? "AUTO" : "REVIEW";
            ConsoleUi.Info(
                $"  {i + 1,2}. [{marker}] {finding.Category} ({finding.Risk.ToString().ToLowerInvariant()})");
            ConsoleUi.Info($"      {finding.DisplayLocation}");
            if (!string.IsNullOrWhiteSpace(finding.Detail))
            {
                ConsoleUi.Info($"      {TruncateForDisplay(finding.Detail, 240)}");
            }

            if (!finding.AutoClean && !string.IsNullOrWhiteSpace(finding.Reason))
            {
                ConsoleUi.Info($"      Reason: {finding.Reason}");
            }
        }
    }

    private static void PrintCleanupTargets(IReadOnlyList<RegistryFinding> findings)
    {
        foreach (var finding in findings)
        {
            ConsoleUi.Info($"  - {finding.Category}: {finding.DisplayLocation}");
        }
    }

    private static string TruncateForDisplay(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

    private static object BuildScanJson(InstalledApp app, RegistryScanResult scan) => new
    {
        schemaVersion = 1,
        generatedAt = DateTimeOffset.Now,
        app = new
        {
            index = app.Index,
            refName = app.Ref,
            id = app.Id,
            name = app.Name,
            version = app.Version,
            publisher = app.Publisher,
            installLocation = app.InstallLocation,
            source = app.Location.Code,
        },
        summary = new
        {
            total = scan.Findings.Count,
            autoClean = scan.Findings.Count(finding => finding.AutoClean),
            review = scan.Findings.Count(finding => !finding.AutoClean),
            requiresElevation = scan.Findings.Count(finding => finding.RequiresElevation),
            appStillInstalled = scan.AppStillInstalled,
        },
        findings = scan.Findings.Select(finding => new
        {
            id = finding.Id,
            category = finding.Category,
            action = finding.Action,
            registryKey = finding.Location.DisplayPath,
            valueName = finding.ValueName,
            risk = finding.Risk.ToString().ToLowerInvariant(),
            autoClean = finding.AutoClean,
            requiresElevation = finding.RequiresElevation,
            summary = finding.Summary,
            detail = finding.Detail,
            reason = finding.Reason,
        }),
        warnings = scan.Warnings,
    };

    private static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static int RunHelp(ParsedCommand parsed)
    {
        if (parsed.Positionals.Count > 1)
        {
            throw new CliException("Usage: sulu help [command].", ExitCodes.Usage);
        }

        if (parsed.Positionals.Count == 1)
        {
            PrintCommandHelp(parsed.Positionals[0]);
        }
        else
        {
            PrintGeneralHelp();
        }

        return ExitCodes.Success;
    }

    private static void PrintCommandHelp(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "list":
            case "ls":
                Console.WriteLine(
                    """
                    Usage: sulu list [filter] [options]

                    List classic Win32/MSI software registered in the per-user and
                    per-machine Uninstall registry keys.

                    Every app gets a persistent Index. Pass that number to commands
                    such as 'sulu uninstall 12', which avoids long or truncated names.

                    Options:
                      --filter <text>       Filter by name, publisher, registry path, or index
                      --sort <field>        name (default), publisher, date, size
                      --limit <n>           Show at most n results
                      --all                 Include hidden system components and updates
                      --verbose             Print full names and registry IDs, one app per block
                      --refresh             Rebuild index.json: prune stale entries and renumber
                      --json                Machine-readable JSON output

                    --refresh re-enumerates installed software, removes indexes for
                    registry keys that no longer exist, and assigns contiguous numbers
                    from 1 to current apps. Visible apps are numbered first, followed by
                    hidden system/update entries. Stored indexes change after a refresh.

                    Examples:
                      sulu list
                      sulu list --filter chrome
                      sulu list --all --verbose
                      sulu list --filter 12
                      sulu list --refresh
                    """);
                return;

            case "uninstall":
            case "remove":
                Console.WriteLine(
                    """
                    Usage: sulu uninstall <index|name> [options]
                           sulu uninstall --index <n> [options]
                           sulu uninstall --id <id> [options]

                    Runs the vendor uninstaller, then scans and cleans registry
                    leftovers. A .reg backup is created before any cleanup.

                    Options:
                      --index <n>           Stable application index from 'sulu list'
                      --id <id>             Exact registry ID from 'sulu list --verbose'
                      --yes, -y             Do not ask for confirmation
                      --quiet, -q           Use QuietUninstallString / MSI silent mode
                      --dry-run             Show the command and findings; change nothing
                      --no-clean            Uninstall only; skip registry cleanup
                      --pause               Wait for Enter after the uninstaller exits and before the scan
                      --no-pause            Never wait for Enter (default when --yes is used)
                      --timeout <seconds>   Uninstaller timeout (default 1800, 0 = forever)
                      --settle <seconds>    Wait for the uninstall entry to disappear (default 5)
                      --backup-dir <path>   Parent directory for .reg backups

                    Examples:
                      sulu uninstall 12
                      sulu uninstall "Example App"
                      sulu uninstall 12 --quiet --yes
                      sulu uninstall --id "HKLM64:SOFTWARE\...\Uninstall\{GUID}" --dry-run
                    """);
                return;

            case "scan":
                Console.WriteLine(
                    """
                    Usage: sulu scan <index|name> [options]
                           sulu scan --index <n> [options]
                           sulu scan --id <id> [options]

                    Scans the app's uninstall entry, App Paths, and Run/RunOnce
                    references. This command never changes the registry. While an
                    app is still installed, its live settings are protected and
                    marked REVIEW; automatic cleanup happens after uninstall.

                    Options:
                      --index <n>           Stable application index from 'sulu list'
                      --id <id>             Exact registry ID from 'sulu list --verbose'
                      --json                Machine-readable JSON output

                    Examples:
                      sulu scan 12
                      sulu scan "Example App"
                      sulu scan 12 --json
                    """);
                return;

            case "purge":
            case "force-delete":
                Console.WriteLine(
                    """
                    Usage: sulu purge <index|name> [options]
                           sulu purge --index <n> [options]
                           sulu purge --id <id> [options]

                    Force-deletes the selected application's registry footprint without
                    running the vendor uninstaller. Only the application's own Uninstall
                    key, App Paths entries, and Run/RunOnce values are targeted.

                    This command never deletes files. After a successful purge it prints
                    a Remove-Item command that you may run manually to delete the
                    application directory.

                    Options:
                      --index <n>           Stable application index from 'sulu list'
                      --id <id>             Exact registry ID from 'sulu list --verbose'
                      --yes, -y             Do not ask for confirmation
                      --dry-run             Show the registry targets; change nothing
                      --backup-dir <path>   Parent directory for the .reg backup

                    This is destructive: the application may still be installed and its
                    uninstall entry will disappear. A .reg backup is created first.

                    Examples:
                      sulu purge 12 --dry-run
                      sulu purge 12 --yes
                    """);
                return;

            case "migrate":
            case "move":
                Console.WriteLine(
                    """
                    Usage: sulu migrate <index|name> --to <new-directory> [options]
                           sulu migrate --restore <migration.json|record-directory> [options]

                    Moves a non-MSI application to a new directory and rewrites registry
                    string values that contain the old path. Safe references in the
                    app's own uninstall entry, App Paths, and Run/RunOnce are selected
                    automatically. Everything else is listed for review.

                    Files are copied first and the registry is updated second. The old
                    directory is always kept for rollback; after success a Remove-Item
                    command is printed for you to delete it manually. A migration
                    record and review CSV are written before any change.

                    Migrate options:
                      --to <path>           New application directory (required)
                      --from <path>         Current directory (default: InstallLocation)
                      --index <n>           Stable application index from 'sulu list'
                      --id <id>             Exact registry ID from 'sulu list --verbose'
                      --registry-only       Rewrite registry values; do not move files
                      --keep-source         Accepted for compatibility; the old directory is always kept now
                      --include-review      Also change review values, not just safe ones
                      --force               Allow running processes or shared roots; required with service refs
                      --dry-run             Show the plan; change nothing
                      --yes, -y             Do not ask for confirmation
                      --backup-dir <path>   Parent directory for the migration record

                    Restore options:
                      --restore <path>      migration.json or its record directory
                      --force               Overwrite values changed after migration
                      --registry-only       Do not copy or delete files
                      --keep-source         Keep the migrated copy after copying files back
                      --dry-run             Show what would be restored
                      --yes, -y             Do not ask for confirmation

                    MSI products are refused because relocating them outside the Windows
                    Installer component database breaks repair and upgrade. Reinstall
                    those products in the desired location instead.

                    Examples:
                      sulu migrate 12 --to "D:\Apps\Example" --dry-run
                      sulu migrate 12 --to "D:\Apps\Example"
                      sulu migrate 12 --to "D:\Apps\Example" --registry-only --yes
                      sulu migrate --restore "$env:LOCALAPPDATA\sulu\migrations\20261110-120000-Example\migration.json"
                    """);
                return;

            case "help":
                Console.WriteLine("Usage: sulu help [command]");
                return;

            default:
                throw new CliException(
                    $"Unknown command '{command}'. Run 'sulu --help'.",
                    ExitCodes.Usage);
        }
    }

    private static void PrintGeneralHelp()
    {
        Console.WriteLine(
            $"""
            Sulu {WindowsInfo.Version}
            A conservative Windows software uninstaller with registry cleanup,
            stable app indexes, and application path migration.

            Usage:
              sulu <command> [options]

            Commands:
              list        List installed software and its stable index
              uninstall   Run an uninstaller, then scan and clean registry leftovers
              scan        Preview registry footprint without changing anything
              purge       Force-delete registry entries only; files are kept
              migrate     Move an application and rewrite its registry references
              help        Show general or command help

            Global options:
              -h, --help        Show help
              -V, --version     Show version
              --no-color        Disable ANSI colors

            Indexes:
              Every uninstall registry entry receives a persistent number stored in
              %LOCALAPPDATA%\sulu\index.json. Use it instead of a long display name:
              'sulu uninstall 12', 'sulu scan 12', or 'sulu migrate 12 --to ...'.

            Safety:
              Cleanup is limited to stale uninstall entries, App Paths entries, and
              Run/RunOnce values that can be tied to the app. A .reg backup is created
              before deletion. HKLM changes and machine-wide uninstalls require an
              elevated terminal. Path migration refuses MSI products and writes a
              rollback record before copying files.

            Exit codes:
              0 success, 2 usage, 3 not found/ambiguous, 4 cancelled,
              5 uninstall failed, 6 elevation required, 7 cleanup failed,
              8 migration failed

            Examples:
              sulu list --filter chrome
              sulu list --verbose
              sulu uninstall 12
              sulu scan 12 --json
              sulu migrate 12 --to "D:\Apps\Example" --dry-run

            Run 'sulu help <command>' for command-specific help.
            """);
    }
}
