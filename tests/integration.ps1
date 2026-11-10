param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "src\Sulu\Sulu.csproj"
$exe = Join-Path $repoRoot "src\Sulu\bin\$Configuration\net10.0-windows\sulu.exe"

& dotnet build $project -c $Configuration --nologo | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "ASSERT FAILED: $Message"
    }
}

function Invoke-Cli {
    param([string[]]$Arguments)

    $output = & $exe @Arguments 2>&1
    $code = $LASTEXITCODE
    [pscustomobject]@{
        ExitCode = $code
        Output = ($output | Out-String).Trim()
    }
}

$runId = [guid]::NewGuid().ToString("N")
$shortId = $runId.Substring(0, 12)
$testDir = Join-Path $repoRoot ".tmp\integration-$runId"
$dataDir = Join-Path $testDir "data"
$installDir = Join-Path $testDir "app"
$controlDir = Join-Path $testDir "other"
$backupRoot = Join-Path $testDir "backups"
$migrationSource = Join-Path $testDir "migration-source"
$migrationTarget = Join-Path $testDir "migration-target"
$migrationRecordRoot = Join-Path $testDir "migration-records"
$purgeBackupRoot = Join-Path $testDir "purge-backups"
$oldDataDir = $env:SULU_DATA_DIR
$env:SULU_DATA_DIR = $dataDir

$name = "Sulu SelfTest $shortId - Windows Software Development Kit Compatible Application"
$uninstallKeyPath = "Software\Microsoft\Windows\CurrentVersion\Uninstall\SuluSelfTest-$runId"
$uninstallKey = "HKCU:\$uninstallKeyPath"
$appPathKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\sulu-selftest-$shortId.exe"
$controlAppPathKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\sulu-control-$shortId.exe"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValue = "SuluSelfTest-$shortId"
$controlRunValue = "SuluControl-$shortId"
$protectedName = "Sulu Protected $shortId"
$protectedKeyName = "SuluProtected-$runId"
$protectedUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$protectedKeyName"
$protectedAppPathKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\sulu-protected-$shortId.exe"

$migrationName = "Sulu Migration SelfTest $shortId"
$migrationKeyName = "SuluMigration-$runId"
$migrationUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$migrationKeyName"
$migrationAppPathKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\sulu-migrate-$shortId.exe"
$migrationRunValue = "SuluMigration-$shortId"
$migrationSettingsRoot = "HKCU:\Software\SuluSelfTest-$shortId"
$migrationSettingsKey = "$migrationSettingsRoot\$runId"

$canWriteRegistry = $false
$probeKey = "HKCU:\Software\SuluProbe-$runId"
try {
    New-Item -Path $probeKey -Force | Out-Null
    New-ItemProperty -Path $probeKey -Name Probe -Value "ok" -PropertyType String -Force | Out-Null
    $canWriteRegistry = (Get-ItemPropertyValue -Path $probeKey -Name Probe) -eq "ok"
}
catch {
    $canWriteRegistry = $false
}
finally {
    Remove-Item -Path $probeKey -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    New-Item -ItemType Directory -Force -Path $testDir, $dataDir, $installDir, $controlDir | Out-Null

    Write-Host "`n[1/9] version, help, list/index, and refresh integrity" -ForegroundColor Cyan
    $version = Invoke-Cli @("--version")
    Assert-True ($version.ExitCode -eq 0) "version should exit 0"
    Assert-True ($version.Output -match "Sulu 1\.") "version should identify Sulu 1.x`n$($version.Output)"
    $migrateHelp = Invoke-Cli @("help", "migrate")
    Assert-True ($migrateHelp.ExitCode -eq 0) "migrate help should exit 0"
    Assert-True ($migrateHelp.Output.Contains("--to")) "migrate help should document --to"
    $uninstallHelp = Invoke-Cli @("help", "uninstall")
    Assert-True ($uninstallHelp.ExitCode -eq 0) "uninstall help should exit 0"
    Assert-True ($uninstallHelp.Output.Contains("--pause")) "uninstall help should document --pause"
    $purgeHelp = Invoke-Cli @("help", "purge")
    Assert-True ($purgeHelp.ExitCode -eq 0) "purge help should exit 0"
    Assert-True ($purgeHelp.Output.Contains("never deletes files")) "purge help should explain that files are kept"

    $listJson = Invoke-Cli @("list", "--json")
    Assert-True ($listJson.ExitCode -eq 0) "list --json should exit 0"
    $catalog = $listJson.Output | ConvertFrom-Json
    Assert-True ($catalog.count -gt 0) "list --json should return installed software"
    $indexes = @($catalog.apps | ForEach-Object { [int]$_.index })
    Assert-True (($indexes | Where-Object { $_ -le 0 }).Count -eq 0) "every app should have a positive index"
    Assert-True (($indexes | Select-Object -Unique).Count -eq $indexes.Count) "indexes must be unique"
    Assert-True (Test-Path (Join-Path $dataDir "index.json")) "list should persist the application index"

    $indexPath = Join-Path $dataDir "index.json"
    $indexData = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    $staleRegistryId = "HKCU:SOFTWARE\SuluRefreshTest-$runId"
    $staleEntry = [pscustomobject]@{
        index = 99999
        registryId = $staleRegistryId
        name = "Sulu stale index test"
        publisher = $null
        installLocation = $null
        firstSeen = (Get-Date).ToString("o")
        lastSeen = (Get-Date).ToString("o")
    }
    $indexData.apps = @($indexData.apps) + $staleEntry
    $indexData.nextIndex = 100000
    $indexData | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $indexPath -Encoding utf8

    $refreshJson = Invoke-Cli @("list", "--refresh", "--json", "--all")
    Assert-True ($refreshJson.ExitCode -eq 0) "list --refresh --json should exit 0"
    $refreshed = $refreshJson.Output | ConvertFrom-Json
    Assert-True ($refreshed.indexRefreshed -eq $true) "refresh JSON should report indexRefreshed=true"
    Assert-True ($refreshed.staleIndexEntriesRemoved -ge 1) "refresh should report removed stale index entries"
    Assert-True ($refreshed.indexSaved -eq $true) "refresh should save the index file"
    Assert-True ($refreshed.indexesReassigned -eq $refreshed.count) "refresh should renumber every current application"
    $refreshedIndexes = @($refreshed.apps | ForEach-Object { [int]$_.index } | Sort-Object)
    Assert-True ($refreshedIndexes[0] -eq 1) "refreshed numbering should start at 1"
    Assert-True ($refreshedIndexes[-1] -eq $refreshedIndexes.Count) "refreshed numbering should be contiguous"
    Assert-True (($refreshedIndexes | Select-Object -Unique).Count -eq $refreshedIndexes.Count) "refreshed indexes must be unique"
    $afterRefreshIndex = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    Assert-True (@($afterRefreshIndex.apps | Where-Object { $_.registryId -eq $staleRegistryId }).Count -eq 0) "refresh should prune the stale index entry"

    $sample = $catalog.apps | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.installLocation) `
            -and (-not [string]::IsNullOrWhiteSpace($_.uninstallString) -or -not [string]::IsNullOrWhiteSpace($_.productCode))
    } | Select-Object -First 1
    if ($null -eq $sample) {
        $sample = $catalog.apps | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.uninstallString) -or -not [string]::IsNullOrWhiteSpace($_.productCode)
        } | Select-Object -First 1
    }
    if ($null -eq $sample) {
        $sample = $catalog.apps | Select-Object -First 1
    }

    Write-Host "[2/9] index-based uninstall/scan (read-only)" -ForegroundColor Cyan
    $dryRun = Invoke-Cli @("uninstall", "$($sample.index)", "--dry-run", "--quiet", "--timeout", "30")
    Assert-True ($dryRun.ExitCode -eq 0) "index-based uninstall dry run should exit 0`n$($dryRun.Output)"
    Assert-True ($dryRun.Output.Contains("Index")) "uninstall output should show the index"
    Assert-True ($dryRun.Output.Contains("Dry run")) "uninstall dry run should say Dry run"
    $scan = Invoke-Cli @("scan", "$($sample.index)", "--json")
    Assert-True ($scan.ExitCode -eq 0) "index-based scan should exit 0"
    $scanJson = $scan.Output | ConvertFrom-Json
    Assert-True ($scanJson.app.index -eq $sample.index) "scan should resolve the same index"
    $dryRunPause = Invoke-Cli @("uninstall", "$($sample.index)", "--dry-run", "--pause", "--quiet", "--timeout", "30")
    Assert-True ($dryRunPause.ExitCode -eq 0) "dry run with --pause should not require Enter"
    $pauseConflict = Invoke-Cli @("uninstall", "$($sample.index)", "--dry-run", "--pause", "--no-pause")
    Assert-True ($pauseConflict.ExitCode -eq 2) "pause and no-pause together should be a usage error"

    if (-not $canWriteRegistry) {
        Write-Host "Read-only checks passed. This session cannot write HKCU, so synthetic uninstall/migration tests were skipped." -ForegroundColor Yellow
        return
    }

    New-Item -ItemType Directory -Force -Path $migrationSource, (Join-Path $migrationSource "data") | Out-Null
    Set-Content -LiteralPath (Join-Path $migrationSource "app.exe") -Value "fake executable" -Encoding Ascii
    Set-Content -LiteralPath (Join-Path $migrationSource "data\config.ini") -Value "setting=old" -Encoding Ascii

    $uninstallScript = Join-Path $testDir "uninstall.cmd"
    @"
@echo off
reg delete "HKCU\$uninstallKeyPath" /f >nul 2>&1
exit /b 0
"@ | Set-Content -Path $uninstallScript -Encoding Ascii

    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayName -Value $name -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value "1.2.3" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name Publisher -Value "Sulu Tests" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDir -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value (Join-Path $installDir "app.exe") -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallScript -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name QuietUninstallString -Value $uninstallScript -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name EstimatedSize -Value 1234 -PropertyType DWord -Force | Out-Null

    New-Item -Path $appPathKey -Force | Out-Null
    Set-Item -Path $appPathKey -Value (Join-Path $installDir "app.exe")
    New-Item -Path $controlAppPathKey -Force | Out-Null
    Set-Item -Path $controlAppPathKey -Value (Join-Path $controlDir "control.exe")

    Set-ItemProperty -Path $runKey -Name $runValue -Value "`"$(Join-Path $installDir 'launcher.exe')`" --background"
    Set-ItemProperty -Path $runKey -Name $controlRunValue -Value "`"$(Join-Path $controlDir 'control.exe')`""

    Write-Host "[3/9] full long name and stable index" -ForegroundColor Cyan
    $longList = Invoke-Cli @("list", "--filter", $name, "--verbose")
    Assert-True ($longList.ExitCode -eq 0) "long-name list should exit 0"
    Assert-True ($longList.Output.Contains($name)) "--verbose must show the complete long display name"
    $syntheticJson = (Invoke-Cli @("list", "--filter", $name, "--json")).Output | ConvertFrom-Json
    Assert-True ($syntheticJson.count -eq 1) "exactly one synthetic app should match"
    $syntheticIndex = [int]$syntheticJson.apps[0].index

    Write-Host "[4/9] scan while installed protects live settings" -ForegroundColor Cyan
    $scan = Invoke-Cli @("scan", "$syntheticIndex", "--json")
    Assert-True ($scan.ExitCode -eq 0) "scan should exit 0"
    $scanJson = $scan.Output | ConvertFrom-Json
    Assert-True ($scanJson.summary.appStillInstalled -eq $true) "scan should report appStillInstalled=true"
    Assert-True ($scanJson.summary.autoClean -eq 0) "scan of an installed app must not mark live entries for cleanup"
    Assert-True ($scanJson.summary.total -ge 3) "scan should find uninstall, App Paths, and Run entries`n$($scan.Output)"

    Write-Host "[5/9] uninstall --dry-run changes nothing" -ForegroundColor Cyan
    $dryRun = Invoke-Cli @("uninstall", "$syntheticIndex", "--dry-run", "--quiet", "--timeout", "60")
    Assert-True ($dryRun.ExitCode -eq 0) "dry run should exit 0 (got $($dryRun.ExitCode))"
    Assert-True ($dryRun.Output.Contains("Dry run")) "dry-run output should say Dry run"
    Assert-True (Test-Path $uninstallKey) "dry run must not remove the uninstall entry"

    Write-Host "[6/9] uninstall, scan, back up, and clean" -ForegroundColor Cyan
    $uninstall = Invoke-Cli @(
        "uninstall", "$syntheticIndex", "--yes", "--quiet", "--timeout", "60",
        "--settle", "5", "--backup-dir", $backupRoot
    )
    Assert-True ($uninstall.ExitCode -eq 0) "uninstall should exit 0 (got $($uninstall.ExitCode))`n$($uninstall.Output)"
    Assert-True ($uninstall.Output.Contains("Backup created")) "uninstall should report a backup`n$($uninstall.Output)"
    Assert-True (-not (Test-Path $uninstallKey)) "uninstaller should have removed the uninstall entry"
    Assert-True (-not (Test-Path $appPathKey)) "App Paths leftover should be deleted"
    Assert-True ($null -eq (Get-ItemPropertyValue -Path $runKey -Name $runValue -ErrorAction SilentlyContinue)) "Run leftover should be deleted"
    Assert-True (Test-Path $controlAppPathKey) "unrelated App Paths entry must be preserved"
    Assert-True ($null -ne (Get-ItemPropertyValue -Path $runKey -Name $controlRunValue -ErrorAction SilentlyContinue)) "unrelated Run value must be preserved"

    $backupFiles = @(Get-ChildItem -Path $backupRoot -Recurse -Filter "*.reg" -ErrorAction Stop)
    Assert-True ($backupFiles.Count -eq 2) "cleanup should export two registry keys (got $($backupFiles.Count))"
    Assert-True (Test-Path (Join-Path (Split-Path $backupFiles[0].FullName -Parent) "manifest.json")) "backup manifest should exist"

    Write-Host "[7/9] failed self-removal keeps live settings protected" -ForegroundColor Cyan
    $protectedScript = Join-Path $testDir "uninstall-noop.cmd"
    @"
@echo off
exit /b 0
"@ | Set-Content -Path $protectedScript -Encoding Ascii

    New-Item -Path $protectedUninstallKey -Force | Out-Null
    New-ItemProperty -Path $protectedUninstallKey -Name DisplayName -Value $protectedName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $protectedUninstallKey -Name Publisher -Value "Sulu Tests" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $protectedUninstallKey -Name InstallLocation -Value $installDir -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $protectedUninstallKey -Name UninstallString -Value $protectedScript -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $protectedUninstallKey -Name QuietUninstallString -Value $protectedScript -PropertyType String -Force | Out-Null

    New-Item -Path $protectedAppPathKey -Force | Out-Null
    Set-Item -Path $protectedAppPathKey -Value (Join-Path $installDir "protected.exe")

    $protectedJson = (Invoke-Cli @("list", "--filter", $protectedName, "--json")).Output | ConvertFrom-Json
    Assert-True ($protectedJson.count -eq 1) "protected app should be listed once"
    $protectedIndex = [int]$protectedJson.apps[0].index
    $protected = Invoke-Cli @(
        "uninstall", "$protectedIndex", "--yes", "--quiet", "--timeout", "30", "--settle", "1"
    )
    Assert-True ($protected.ExitCode -eq 0) "no-op uninstall should exit 0 (got $($protected.ExitCode))"
    Assert-True (Test-Path $protectedUninstallKey) "uninstall entry should remain after the no-op uninstaller"
    Assert-True (Test-Path $protectedAppPathKey) "live App Paths entry must be preserved when the app is still registered"
    Assert-True ($protected.Output.Contains("still registered")) "output should explain that live settings are protected"

    Write-Host "[8/9] migration and rollback" -ForegroundColor Cyan
    New-Item -Path $migrationUninstallKey -Force | Out-Null
    New-ItemProperty -Path $migrationUninstallKey -Name DisplayName -Value $migrationName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $migrationUninstallKey -Name Publisher -Value "Sulu Tests" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $migrationUninstallKey -Name InstallLocation -Value $migrationSource -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $migrationUninstallKey -Name DisplayIcon -Value (Join-Path $migrationSource "app.exe") -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $migrationUninstallKey -Name UninstallString -Value "`"$(Join-Path $migrationSource 'uninstall.exe')`"" -PropertyType String -Force | Out-Null
    New-Item -Path $migrationAppPathKey -Force | Out-Null
    Set-Item -Path $migrationAppPathKey -Value (Join-Path $migrationSource "app.exe")
    New-ItemProperty -Path $migrationAppPathKey -Name Path -Value $migrationSource -PropertyType String -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name $migrationRunValue -Value "`"$(Join-Path $migrationSource 'app.exe')`" --background"
    New-Item -Path $migrationSettingsKey -Force | Out-Null
    New-ItemProperty -Path $migrationSettingsKey -Name InstallRoot -Value $migrationSource -PropertyType String -Force | Out-Null

    $migrationJson = (Invoke-Cli @("list", "--filter", $migrationName, "--json")).Output | ConvertFrom-Json
    Assert-True ($migrationJson.count -eq 1) "migration app should be listed once"
    $migrationIndex = [int]$migrationJson.apps[0].index

    $migrateDryRun = Invoke-Cli @(
        "migrate", "$migrationIndex", "--to", $migrationTarget, "--dry-run", "--include-review"
    )
    Assert-True ($migrateDryRun.ExitCode -eq 0) "migration dry run should exit 0`n$($migrateDryRun.Output)"
    Assert-True ($migrateDryRun.Output.Contains("No files or registry values were changed")) "migration dry run must state that nothing changed"
    Assert-True (Test-Path (Join-Path $migrationSource "app.exe")) "migration dry run must leave source files intact"
    Assert-True (-not (Test-Path $migrationTarget)) "migration dry run must not create the target"
    Assert-True ((Get-ItemPropertyValue -Path $migrationUninstallKey -Name InstallLocation) -eq $migrationSource) "migration dry run must not rewrite InstallLocation"

    $migrate = Invoke-Cli @(
        "migrate", "$migrationIndex", "--to", $migrationTarget, "--yes",
        "--include-review", "--backup-dir", $migrationRecordRoot
    )
    Assert-True ($migrate.ExitCode -eq 0) "migration should exit 0`n$($migrate.Output)"
    Assert-True ($migrate.Output.Contains("Migration completed")) "migration should report completion"
    Assert-True (Test-Path (Join-Path $migrationTarget "app.exe")) "migration should copy files to the target"
    Assert-True (Test-Path (Join-Path $migrationTarget "data\config.ini")) "migration should copy subdirectories"
    Assert-True (Test-Path (Join-Path $migrationSource "app.exe")) "migration must keep the old directory"
    Assert-True ($migrate.Output.Contains("Manual directory cleanup")) "migration should print manual cleanup guidance"
    Assert-True ($migrate.Output.Contains($migrationSource)) "manual cleanup command should name the old directory"
    Assert-True ((Get-ItemPropertyValue -Path $migrationUninstallKey -Name InstallLocation) -eq $migrationTarget) "InstallLocation should be rewritten"
    Assert-True ((Get-ItemPropertyValue -Path $migrationAppPathKey -Name Path) -eq $migrationTarget) "App Paths Path should be rewritten"
    Assert-True ((Get-ItemPropertyValue -Path $runKey -Name $migrationRunValue).Contains($migrationTarget)) "Run value should be rewritten"
    Assert-True ((Get-ItemPropertyValue -Path $migrationSettingsKey -Name InstallRoot) -eq $migrationTarget) "reviewed app setting should be rewritten with --include-review"

    $migrationPlan = Get-ChildItem -Path $migrationRecordRoot -Filter "migration.json" -Recurse | Select-Object -First 1
    Assert-True ($null -ne $migrationPlan) "migration should write a rollback record"
    Assert-True (Test-Path (Join-Path $migrationPlan.DirectoryName "references.csv")) "migration should write the review CSV"
    $migrationCsv = @(Import-Csv -LiteralPath (Join-Path $migrationPlan.DirectoryName "references.csv"))
    Assert-True ($migrationCsv.Count -ge 4) "review CSV should list the migrated references"
    Assert-True (@($migrationCsv | Where-Object Approved -eq "YES").Count -ge 4) "review CSV should mark selected references as approved"

    $restore = Invoke-Cli @("migrate", "--restore", $migrationPlan.FullName, "--yes")
    Assert-True ($restore.ExitCode -eq 0) "restore should exit 0`n$($restore.Output)"
    Assert-True ($restore.Output.Contains("Restore completed")) "restore should report completion"
    Assert-True (Test-Path (Join-Path $migrationSource "app.exe")) "restore should keep the original source files"
    Assert-True (Test-Path (Join-Path $migrationTarget "app.exe")) "restore should not delete the migrated copy when the old path exists"
    Assert-True ($restore.Output.Contains("Migrated application directory")) "restore should print manual cleanup guidance for the redundant copy"
    Assert-True ((Get-ItemPropertyValue -Path $migrationUninstallKey -Name InstallLocation) -eq $migrationSource) "restore should recover InstallLocation"
    Assert-True ((Get-ItemPropertyValue -Path $migrationAppPathKey -Name Path) -eq $migrationSource) "restore should recover App Paths Path"
    Assert-True ((Get-ItemPropertyValue -Path $runKey -Name $migrationRunValue).Contains($migrationSource)) "restore should recover the Run value"
    Assert-True ((Get-ItemPropertyValue -Path $migrationSettingsKey -Name InstallRoot) -eq $migrationSource) "restore should recover reviewed settings"

    $restoredPlan = Get-Content -LiteralPath $migrationPlan.FullName -Raw | ConvertFrom-Json
    Assert-True ($restoredPlan.restored -eq $true) "rollback record should be marked as restored"

    Write-Host "[9/9] force-delete registry entries only" -ForegroundColor Cyan
    $purge = Invoke-Cli @(
        "purge", "$migrationIndex", "--yes", "--backup-dir", $purgeBackupRoot
    )
    Assert-True ($purge.ExitCode -eq 0) "purge should exit 0`n$($purge.Output)"
    Assert-True ($purge.Output.Contains("Force-delete registry entries")) "purge should identify itself clearly"
    Assert-True ($purge.Output.Contains("Manual directory cleanup")) "purge should print manual directory cleanup guidance"
    Assert-True ($purge.Output.Contains($migrationSource)) "purge cleanup command should name the install directory"
    Assert-True (Test-Path (Join-Path $migrationSource "app.exe")) "purge must not delete application files"
    Assert-True (-not (Test-Path $migrationUninstallKey)) "purge should delete the exact uninstall entry"
    Assert-True (-not (Test-Path $migrationAppPathKey)) "purge should delete the related App Paths key"
    Assert-True ($null -eq (Get-ItemPropertyValue -Path $runKey -Name $migrationRunValue -ErrorAction SilentlyContinue)) "purge should delete the related Run value"
    Assert-True (Test-Path $migrationSettingsKey) "purge must not delete unrelated vendor settings"
    $purgeBackups = @(Get-ChildItem -Path $purgeBackupRoot -Recurse -Filter "*.reg" -ErrorAction Stop)
    Assert-True ($purgeBackups.Count -ge 3) "purge should back up each registry key (got $($purgeBackups.Count))"

    $afterPurge = (Invoke-Cli @("list", "--filter", $migrationName, "--json")).Output | ConvertFrom-Json
    Assert-True ($afterPurge.count -eq 0) "purged app should no longer be listed"

    $purgeRefresh = Invoke-Cli @("list", "--refresh", "--json")
    Assert-True ($purgeRefresh.ExitCode -eq 0) "list --refresh after purge should exit 0"
    $purgeRefreshJson = $purgeRefresh.Output | ConvertFrom-Json
    Assert-True ($purgeRefreshJson.staleIndexEntriesRemoved -ge 1) "refresh should remove the purged app's stale index entry"
    $indexAfterPurge = Get-Content -LiteralPath (Join-Path $dataDir "index.json") -Raw | ConvertFrom-Json
    Assert-True (@($indexAfterPurge.apps | Where-Object { $_.registryId -eq $migrationJson.apps[0].id }).Count -eq 0) "purged app's index entry should be pruned"

    Write-Host "`nAll integration checks passed." -ForegroundColor Green
}
finally {
    Remove-Item -Path $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $protectedUninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $appPathKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $controlAppPathKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $protectedAppPathKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $migrationUninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $migrationAppPathKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $migrationSettingsKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $migrationSettingsRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name $controlRunValue -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name $migrationRunValue -ErrorAction SilentlyContinue
    Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
    $env:SULU_DATA_DIR = $oldDataDir
}
