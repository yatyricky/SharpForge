[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$OutputDir = (Join-Path $PSScriptRoot "publish"),

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot

if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $repoRoot $OutputDir
}

$runtimeOutputDir = Join-Path $OutputDir $Runtime
$projectOutputRoot = Join-Path $runtimeOutputDir "_projects"

$projects = @(
    @{
        Name = "Transpiler"
        Project = "src\Transpiler\Transpiler.csproj"
        Binary = "sf-transpile"
    },
    @{
        Name = "Builder"
        Project = "src\Builder\Builder.csproj"
        Binary = "sf-build"
    },
    @{
        Name = "JassGen"
        Project = "src\JassGen\JassGen.csproj"
        Binary = "sf-jassgen"
    },
    @{
        Name = "Gui"
        Project = "src\Gui\Gui.csproj"
        Binary = "sf-gui"
    }
)

New-Item -ItemType Directory -Force -Path $runtimeOutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $projectOutputRoot | Out-Null

foreach ($project in $projects) {
    $projectPath = Join-Path $repoRoot $project.Project
    $projectPublishDir = Join-Path $projectOutputRoot $project.Name

    Write-Host "Publishing $($project.Name) for $Runtime..." -ForegroundColor Cyan

    $publishArgs = @(
        "publish",
        $projectPath,
        "-c", $Configuration,
        "-r", $Runtime,
        "-p:IsPublishing=true",
        "-o", $projectPublishDir
    )

    if ($NoRestore) {
        $publishArgs += "--no-restore"
    }

    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($project.Name)."
    }

    $binaryName = if ($Runtime.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) {
        "$($project.Binary).exe"
    }
    else {
        $project.Binary
    }

    $publishedBinary = Join-Path $projectPublishDir $binaryName
    if (-not (Test-Path -LiteralPath $publishedBinary)) {
        throw "Expected published binary was not found: $publishedBinary"
    }

    Copy-Item -LiteralPath $publishedBinary -Destination (Join-Path $runtimeOutputDir $binaryName) -Force
}

Write-Host ""
Write-Host "Published single-file executables:" -ForegroundColor Green
Get-ChildItem -LiteralPath $runtimeOutputDir -File |
    Sort-Object Name |
    ForEach-Object { Write-Host "  $($_.FullName)" }