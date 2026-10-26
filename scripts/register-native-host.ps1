# ==============================================================================
# Register the Media Catcher Chrome/Edge Native Messaging Host.
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\register-native-host.ps1
#   powershell -ExecutionPolicy Bypass -File .\register-native-host.ps1 -ExtensionId "abcdefghijklmnopqrstuvwxyzabcdef"
# ==============================================================================
param(
    [string[]]$ExtensionId = @(),
    [ValidateSet("chrome", "edge", "all")]
    [string]$Browser = "all"
)

$ErrorActionPreference = "Stop"
$HostName = "com.mediacatcher.host"

function Find-NativeHost {
    $candidates = @(
        (Join-Path $PSScriptRoot "..\src\MediaCatcher.NativeHost\bin\Release\net10.0-windows\MediaCatcher.NativeHost.exe"),
        (Join-Path $PSScriptRoot "..\src\MediaCatcher.NativeHost\bin\Debug\net10.0-windows\MediaCatcher.NativeHost.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\MediaCatcher\MediaCatcher.NativeHost.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\MediaCatcher\bin\MediaCatcher.NativeHost.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "MediaCatcher.NativeHost.exe was not found. Build the solution first or set the installed location."
}

function Get-Origins {
    $origins = New-Object System.Collections.Generic.List[string]
    foreach ($id in $ExtensionId) {
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        $trimmed = $id.Trim()
        $origins.Add("chrome-extension://$trimmed/")
        $origins.Add("edge-extension://$trimmed/")
    }

    return $origins
}

$nativeHost = Find-NativeHost
$manifestDirectory = Join-Path $env:LOCALAPPDATA "MediaCatcher"
$manifestPath = Join-Path $manifestDirectory "$HostName.json"

New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null

$origins = Get-Origins
if ($origins.Count -eq 0) {
    Write-Warning "No -ExtensionId provided. Native messaging will not accept connections until you re-run with the extension ID (chrome://extensions)."
}

$manifest = @{
    name              = $HostName
    description       = "Media Catcher Native Messaging Host"
    path              = $nativeHost
    type              = "stdio"
    allowed_origins   = $origins
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if ($Browser -in @("chrome", "all")) {
    $chromeKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
    New-Item -Path $chromeKey -Force | Out-Null
    Set-ItemProperty -Path $chromeKey -Name "(default)" -Value $manifestPath
    Write-Host "Registered for Chrome: $chromeKey" -ForegroundColor Green
}

if ($Browser -in @("edge", "all")) {
    $edgeKey = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"
    New-Item -Path $edgeKey -Force | Out-Null
    Set-ItemProperty -Path $edgeKey -Name "(default)" -Value $manifestPath
    Write-Host "Registered for Edge: $edgeKey" -ForegroundColor Green
}

Write-Host "Host manifest: $manifestPath"
Write-Host "Host executable: $nativeHost"
Write-Host "Done."
