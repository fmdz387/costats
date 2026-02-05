<#
.SYNOPSIS
    Builds a signed MSIX package and .appinstaller file for one-click install.

.DESCRIPTION
    Publishes the WPF app, generates MSIX assets, builds an MSIX package using
    Windows SDK tools, signs it, and (optionally) generates an .appinstaller
    file for auto-updates.

.PARAMETER Version
    Version for the package. Accepts 1.2.3 or 1.2.3.0 (normalized to 4-part for MSIX).

.PARAMETER Platform
    Target platform: x64 or arm64. Defaults to x64.

.PARAMETER Configuration
    Build configuration: Release or Debug. Defaults to Release.

.PARAMETER Publisher
    Certificate subject, e.g., "CN=Your Company".

.PARAMETER PublisherDisplayName
    Friendly publisher name in the manifest.

.PARAMETER PackageName
    Package identity name (must match your cert subject).

.PARAMETER DisplayName
    App display name in Windows.

.PARAMETER Description
    App description in the manifest.

.PARAMETER AppInstallerUri
    Public URL where the .appinstaller will be hosted (optional).

.PARAMETER MsixUri
    Public URL where the MSIX will be hosted (optional).

.PARAMETER CertificatePath
    Path to a code signing certificate (.pfx).

.PARAMETER CertificatePassword
    Password for the .pfx file (optional if not required).

.PARAMETER GenerateTestCertificate
    Creates a self-signed certificate for local testing (not for production).
#>

param(
    [string]$Version = "",
    [ValidateSet("x64", "arm64")]
    [string]$Platform = "x64",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$Publisher = "CN=costats",
    [string]$PublisherDisplayName = "fmdz",
    [string]$PackageName = "fmdz387.costats",
    [string]$DisplayName = "costats",
    [string]$Description = "Usage statistics for Claude and Codex AI coding assistants",
    [string]$AppInstallerUri,
    [string]$MsixUri,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$GenerateTestCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $root "src\\costats.App\\costats.App.csproj"
$publishRoot = Join-Path $root "publish\\msix"
$stagingRoot = Join-Path $publishRoot "staging-$Platform"
$appLayout = Join-Path $stagingRoot "layout"
$assetsDir = Join-Path $appLayout "Assets"
$manifestTemplate = Join-Path $root "packaging\\AppxManifest.xml.template"
$appinstallerTemplate = Join-Path $root "packaging\\costats.appinstaller.template"

function Get-DefaultVersion {
    $propsPath = Join-Path $root "src\\Directory.Build.props"
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

function Convert-ToVersionInfo {
    param([string]$Value)

    if ($Value -match '^(?<maj>\d+)\.(?<min>\d+)\.(?<patch>\d+)$') {
        $semVer = "$($matches.maj).$($matches.min).$($matches.patch)"
        return @{
            SemVerVersion = $semVer
            MsixVersion = "$semVer.0"
        }
    }

    if ($Value -match '^(?<maj>\d+)\.(?<min>\d+)\.(?<patch>\d+)\.(?<rev>\d+)$') {
        return @{
            SemVerVersion = "$($matches.maj).$($matches.min).$($matches.patch)"
            MsixVersion = "$($matches.maj).$($matches.min).$($matches.patch).$($matches.rev)"
        }
    }

    throw "Version must be major.minor.patch or major.minor.patch.revision (for example 1.2.3 or 1.2.3.0). Received: '$Value'."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-DefaultVersion
}

$versionInfo = Convert-ToVersionInfo -Value $Version
$SemVerVersion = $versionInfo.SemVerVersion
$Version = $versionInfo.MsixVersion
Write-Host "Using app version $SemVerVersion (MSIX: $Version)" -ForegroundColor Cyan

function Get-WindowsSdkTool {
    param([string]$ToolName)

    $base = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\\10\\bin"
    if (-not (Test-Path $base)) { return $null }

    $versions = Get-ChildItem -Path $base -Directory | Sort-Object Name -Descending
    foreach ($ver in $versions) {
        $candidate = Join-Path $ver.FullName "x64\\$ToolName"
        if (Test-Path $candidate) {
            return $candidate
        }
    }
    return $null
}

function New-AssetPng {
    param(
        [string]$Path,
        [int]$Width,
        [int]$Height,
        [string]$Text = "c"
    )

    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml("#1E1B2E"))

    $fontSize = [Math]::Max(10, [Math]::Min($Width, $Height) * 0.45)
    $font = New-Object System.Drawing.Font "Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = "Center"
    $format.LineAlignment = "Center"

    $rect = New-Object System.Drawing.RectangleF 0, 0, $Width, $Height
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.DrawString($Text, $font, $brush, $rect, $format)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)

    $g.Dispose()
    $bmp.Dispose()
    $brush.Dispose()
    $font.Dispose()
}

function Ensure-Assets {
    if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null }

    $assetMap = @(
        @{ Name = "StoreLogo.png"; Width = 50; Height = 50 },
        @{ Name = "Square44x44Logo.png"; Width = 44; Height = 44 },
        @{ Name = "Square150x150Logo.png"; Width = 150; Height = 150 },
        @{ Name = "Wide310x150Logo.png"; Width = 310; Height = 150 },
        @{ Name = "Square310x310Logo.png"; Width = 310; Height = 310 },
        @{ Name = "SplashScreen.png"; Width = 620; Height = 300 }
    )

    foreach ($asset in $assetMap) {
        $path = Join-Path $assetsDir $asset.Name
        if (-not (Test-Path $path)) {
            New-AssetPng -Path $path -Width $asset.Width -Height $asset.Height
        }
    }
}

function Write-Manifest {
    param([string]$OutPath)
    $template = Get-Content $manifestTemplate -Raw
    $exeName = "costats.App.exe"
    $appId = "costats"
    $escape = { param($value) [System.Security.SecurityElement]::Escape([string]$value) }

    $content = $template `
        -replace "\{\{PACKAGE_NAME\}\}", (& $escape $PackageName) `
        -replace "\{\{PUBLISHER\}\}", (& $escape $Publisher) `
        -replace "\{\{VERSION\}\}", (& $escape $Version) `
        -replace "\{\{DISPLAY_NAME\}\}", (& $escape $DisplayName) `
        -replace "\{\{PUBLISHER_DISPLAY_NAME\}\}", (& $escape $PublisherDisplayName) `
        -replace "\{\{DESCRIPTION\}\}", (& $escape $Description) `
        -replace "\{\{APP_ID\}\}", (& $escape $appId) `
        -replace "\{\{EXECUTABLE\}\}", (& $escape $exeName)

    Set-Content -Path $OutPath -Value $content -Encoding UTF8
}

function Write-AppInstaller {
    param([string]$OutPath)
    if ([string]::IsNullOrWhiteSpace($AppInstallerUri) -or [string]::IsNullOrWhiteSpace($MsixUri)) {
        return
    }

    $template = Get-Content $appinstallerTemplate -Raw
    $arch = if ($Platform -eq "arm64") { "arm64" } else { "x64" }
    $escape = { param($value) [System.Security.SecurityElement]::Escape([string]$value) }

    $content = $template `
        -replace "\{\{VERSION\}\}", (& $escape $Version) `
        -replace "\{\{APPINSTALLER_URI\}\}", (& $escape $AppInstallerUri) `
        -replace "\{\{PACKAGE_NAME\}\}", (& $escape $PackageName) `
        -replace "\{\{PUBLISHER\}\}", (& $escape $Publisher) `
        -replace "\{\{ARCH\}\}", (& $escape $arch) `
        -replace "\{\{MSIX_URI\}\}", (& $escape $MsixUri)

    Set-Content -Path $OutPath -Value $content -Encoding UTF8
}

function Ensure-Certificate {
    if ($CertificatePath -and (Test-Path $CertificatePath)) {
        return
    }

    if (-not $GenerateTestCertificate) {
        throw "CertificatePath is required unless -GenerateTestCertificate is specified."
    }

    $certDir = Join-Path $publishRoot "cert"
    New-Item -ItemType Directory -Force -Path $certDir | Out-Null
    $CertificatePath = Join-Path $certDir "costats-test.pfx"

    Write-Host "Generating self-signed certificate (test only)..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher -KeyAlgorithm RSA -KeyLength 2048 -CertStoreLocation "Cert:\\CurrentUser\\My"
    $securePassword = if ($CertificatePassword) { ConvertTo-SecureString $CertificatePassword -AsPlainText -Force } else { ConvertTo-SecureString "costats" -AsPlainText -Force }
    Export-PfxCertificate -Cert $cert -FilePath $CertificatePath -Password $securePassword | Out-Null

    if (-not $CertificatePassword) {
        $script:CertificatePassword = "costats"
    }
}

$makeAppx = Get-WindowsSdkTool -ToolName "makeappx.exe"
$signTool = Get-WindowsSdkTool -ToolName "signtool.exe"

if (-not $makeAppx -or -not $signTool) {
    throw "Windows SDK tools not found. Install the Windows 10/11 SDK."
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
if (Test-Path $stagingRoot) { Remove-Item -Recurse -Force $stagingRoot }
New-Item -ItemType Directory -Force -Path $appLayout | Out-Null

Write-Host "Publishing app..." -ForegroundColor Yellow
dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime "win-$Platform" `
    --self-contained true `
    --output $appLayout `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:VersionPrefix=$SemVerVersion `
    -p:Version=$SemVerVersion | Out-Null

Ensure-Assets
Write-Manifest -OutPath (Join-Path $appLayout "AppxManifest.xml")

$msixName = "costats-win-$Platform-v$Version.msix"
$msixPath = Join-Path $publishRoot $msixName

Write-Host "Packing MSIX..." -ForegroundColor Yellow
& $makeAppx pack /d $appLayout /p $msixPath /o | Out-Null

Ensure-Certificate
Write-Host "Signing MSIX..." -ForegroundColor Yellow

$signArgs = @("sign", "/fd", "SHA256", "/f", $CertificatePath)
if ($CertificatePassword) { $signArgs += @("/p", $CertificatePassword) }
$signArgs += $msixPath
& $signTool @signArgs | Out-Null

$appinstallerPath = Join-Path $publishRoot "costats-win-$Platform.appinstaller"
Write-AppInstaller -OutPath $appinstallerPath

Write-Host "MSIX ready: $msixPath" -ForegroundColor Green
if (Test-Path $appinstallerPath) {
    Write-Host "AppInstaller ready: $appinstallerPath" -ForegroundColor Green
} else {
    Write-Host "AppInstaller not generated (set -AppInstallerUri and -MsixUri)." -ForegroundColor Gray
}
