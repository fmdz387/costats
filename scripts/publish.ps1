<#
.SYNOPSIS
    Publishes costats for Windows x64 and ARM64.

.DESCRIPTION
    Creates self-contained, single-file executables for distribution.

.PARAMETER Version
    Version number for the build (major.minor.patch). Defaults to VersionPrefix from src/Directory.Build.props.

.PARAMETER Platform
    Target platform: x64, arm64, or all. Defaults to all.

.PARAMETER Configuration
    Build configuration: Release or Debug. Defaults to Release.

.EXAMPLE
    .\publish.ps1
    Publishes for all platforms with default settings.

.EXAMPLE
    .\publish.ps1 -Version "1.2.0" -Platform x64
    Publishes version 1.2.0 for Windows x64 only.
#>

param(
    [string]$Version = "",
    [ValidateSet("x64", "arm64", "all")]
    [string]$Platform = "all",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Get-DefaultVersion {
    $propsPath = Join-Path $PSScriptRoot "..\src\Directory.Build.props"
    if (-not (Test-Path $propsPath)) {
        return "1.0.0"
    }

    [xml]$props = Get-Content -Path $propsPath -Raw
    $propertyGroups = @($props.Project.PropertyGroup)
    foreach ($group in $propertyGroups) {
        if ($group.VersionPrefix) {
            $candidate = [string]$group.VersionPrefix
            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                return $candidate.Trim()
            }
        }
    }

    return "1.0.0"
}

function Assert-SemVer {
    param([string]$Value)

    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must use major.minor.patch format (for example 1.2.3). Received: '$Value'."
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-DefaultVersion
}

Assert-SemVer -Value $Version

$projectPath = Join-Path $PSScriptRoot "..\src\costats.App\costats.App.csproj"
$outputBase = Join-Path $PSScriptRoot "..\publish"

$platforms = if ($Platform -eq "all") { @("win-x64", "win-arm64") } else { @("win-$Platform") }

Write-Host "Building costats v$Version" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Gray
Write-Host "Platforms: $($platforms -join ', ')" -ForegroundColor Gray
Write-Host ""

foreach ($rid in $platforms) {
    $outputPath = Join-Path $outputBase $rid

    Write-Host "Publishing for $rid..." -ForegroundColor Yellow

    dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        --output $outputPath `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -p:VersionPrefix=$Version `
        -p:Version=$Version

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to publish for $rid" -ForegroundColor Red
        exit 1
    }

    # Create ZIP archive
    $zipPath = Join-Path $outputBase "costats-$rid-v$Version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path "$outputPath\*" -DestinationPath $zipPath
    $zipHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$zipPath.sha256"
    Set-Content -Path $checksumPath -Value "$zipHash  $(Split-Path -Path $zipPath -Leaf)" -Encoding Ascii

    Write-Host "Created: $zipPath" -ForegroundColor Green
    Write-Host "Checksum: $checksumPath" -ForegroundColor Green
    Write-Host ""
}

Write-Host "Build complete!" -ForegroundColor Cyan
Write-Host "Output directory: $outputBase" -ForegroundColor Gray

# Show file sizes
Write-Host ""
Write-Host "Artifacts:" -ForegroundColor Yellow
Get-ChildItem $outputBase -Filter "*.zip" | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  $($_.Name) - $sizeMB MB" -ForegroundColor Gray
}
