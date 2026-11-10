[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path $PSScriptRoot).Path
$project = Join-Path $repoRoot "src\Sulu\Sulu.csproj"
$output = Join-Path $repoRoot "dist\$Runtime"

if (Test-Path $output) {
    $resolvedOutput = (Resolve-Path $output).Path
    if (-not $resolvedOutput.StartsWith((Join-Path $repoRoot "dist"), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected output path: $resolvedOutput"
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

# Restore separately so a NuGet source outage can fall back to the local package
# cache. The audit feed is not needed to build this package and is disabled here.
& dotnet restore $project `
    -r $Runtime `
    -p:PublishSingleFile=true `
    -p:SelfContained=$selfContainedValue `
    -p:NuGetAudit=false `
    --ignore-failed-sources

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

& dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained $selfContainedValue `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:GenerateDocumentationFile=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $output "sulu.exe"
if (-not (Test-Path $exe)) {
    throw "Publish completed without producing $exe."
}

Write-Host "Published: $exe" -ForegroundColor Green
Write-Host "Self-contained: $($SelfContained.IsPresent)"
