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
        # Chromium (Chrome AND Edge) only accepts the chrome-extension:// scheme in
        # allowed_origins -- Edge uses it too. An "edge-extension://" entry makes the
        # whole manifest fail to parse ("Invalid scheme"), which surfaces as
        # "Specified native messaging host not found" in the extension.
        $origin = "chrome-extension://$trimmed/"
        if (-not $origins.Contains($origin)) { $origins.Add($origin) }
    }

    # Unary comma: without it PowerShell unrolls a one-element List into a bare
    # string on return, and the manifest ends up with a scalar allowed_origins.
    return ,$origins
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
    # Force a real JSON array: a single-element List/collection collapses to a
    # scalar string in ConvertTo-Json, and Chrome then rejects the manifest.
    allowed_origins   = @($origins.ToArray())
}

$manifestJson = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

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
