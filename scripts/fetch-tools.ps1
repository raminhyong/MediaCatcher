# ==============================================================================
# Fetch external download tools (yt-dlp, ffmpeg, ffprobe) used by Media Catcher.
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\fetch-tools.ps1
#   powershell -ExecutionPolicy Bypass -File .\fetch-tools.ps1 -OutputDir "C:\tools"
# ==============================================================================
param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $PSScriptRoot "..\tools"
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$ytDlpPath = Join-Path $OutputDir "yt-dlp.exe"
$ffmpegPath = Join-Path $OutputDir "ffmpeg.exe"
$ffprobePath = Join-Path $OutputDir "ffprobe.exe"

Write-Host "Tools directory: $OutputDir" -ForegroundColor Cyan

# yt-dlp
if (-not (Test-Path -LiteralPath $ytDlpPath)) {
    Write-Host "Downloading yt-dlp..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile $ytDlpPath -UseBasicParsing
} else {
    Write-Host "yt-dlp already present." -ForegroundColor Green
}

# ffmpeg / ffprobe (essentials build)
if ((-not (Test-Path -LiteralPath $ffmpegPath)) -or (-not (Test-Path -LiteralPath $ffprobePath))) {
    Write-Host "Downloading ffmpeg..." -ForegroundColor Yellow
    $zipPath = Join-Path $env:TEMP "ffmpeg-essentials.zip"
    $extractDir = Join-Path $env:TEMP "ffmpeg-essentials-extract"

    Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $zipPath -UseBasicParsing

    if (Test-Path -LiteralPath $extractDir) {
        Remove-Item -LiteralPath $extractDir -Recurse -Force
    }

    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

    $ffmpegSource = Get-ChildItem -LiteralPath $extractDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    $ffprobeSource = Get-ChildItem -LiteralPath $extractDir -Recurse -Filter "ffprobe.exe" | Select-Object -First 1

    if ($ffmpegSource) { Copy-Item -LiteralPath $ffmpegSource.FullName -Destination $ffmpegPath -Force }
    if ($ffprobeSource) { Copy-Item -LiteralPath $ffprobeSource.FullName -Destination $ffprobePath -Force }

    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "ffmpeg/ffprobe already present." -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Media Catcher resolves these tools from the app 'tools' folder or PATH."
Write-Host "  yt-dlp : $ytDlpPath"
Write-Host "  ffmpeg : $ffmpegPath"
Write-Host "  ffprobe: $ffprobePath"
