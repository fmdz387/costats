param(
    [string]$Url = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json",
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\costats.Infrastructure\Pricing\Resources\litellm-snapshot.json")
)

$ErrorActionPreference = "Stop"

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$tempPath = "$resolvedOutput.$([System.Guid]::NewGuid().ToString('N')).tmp"

Write-Host "Refreshing LiteLLM pricing snapshot"
Write-Host "Source: $Url"
Write-Host "Output: $resolvedOutput"

Invoke-WebRequest -Uri $Url -OutFile $tempPath

try {
    Get-Content -LiteralPath $tempPath -Raw | ConvertFrom-Json | Out-Null
    Move-Item -LiteralPath $tempPath -Destination $resolvedOutput -Force
    Write-Host "Snapshot refreshed at $(Get-Date -Format o)"
}
catch {
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    throw
}
