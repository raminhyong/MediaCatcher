# ==============================================================================
# Verify a Media Catcher install: native messaging host manifest schema, registry
# registration, extension ID pinning, and external tools.
#
# The manifest schema checks exist because two failure modes are silent:
#   - an "edge-extension://" entry in allowed_origins makes Chromium reject the
#     WHOLE manifest (log: "Invalid scheme"), surfacing as
#     "Specified native messaging host not found" in the extension;
#   - allowed_origins serialized as a scalar string instead of an array is
#     rejected the same way.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\scripts\verify-install.ps1
#   powershell -ExecutionPolicy Bypass -File .\scripts\verify-install.ps1 -ExtensionId <id>
# Exit code: 0 = no failures (warnings allowed), 1 = at least one failure.
# ==============================================================================
param(
    [string]$ExtensionId = "",
    [string]$HostName = "com.mediacatcher.host"
)

$ErrorActionPreference = "Stop"
$script:failures = 0
$script:warnings = 0

function Ok([string]$message) { Write-Host "  OK   $message" -ForegroundColor Green }
function Warn([string]$message) { Write-Host "  WARN $message" -ForegroundColor Yellow; $script:warnings++ }
function Fail([string]$message) { Write-Host "  FAIL $message" -ForegroundColor Red; $script:failures++ }
function Section([string]$title) { Write-Host "`n== $title" -ForegroundColor Cyan }

# --- extension folder + pinned ID ---------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$extensionDir = Join-Path $repoRoot "extension"
$extensionManifest = Join-Path $extensionDir "manifest.json"

Section "Extension folder"
$computedId = ""
if (-not (Test-Path -LiteralPath $extensionManifest)) {
    Fail "extension/manifest.json not found at $extensionManifest"
} else {
    Ok "extension folder found: $extensionDir"
    $ext = Get-Content -LiteralPath $extensionManifest -Raw | ConvertFrom-Json
    if ($ext.key) {
        $der = [Convert]::FromBase64String($ext.key)
        $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($der)
        $hex = ($hash | ForEach-Object { $_.ToString("x2") }) -join ""
        $map = "abcdefghijklmnop"
        $computedId = -join ($hex.Substring(0, 32).ToCharArray() | ForEach-Object { $map[[Convert]::ToInt32($_, 16)] })
        Ok "manifest.key present -> pinned extension ID $computedId"
    } else {
        Warn "manifest.json has no 'key': the extension ID changes on every reload, so allowed_origins will go stale"
    }
}

if ($ExtensionId -and $computedId -and ($ExtensionId -ne $computedId)) {
    Fail "-ExtensionId ($ExtensionId) does not match the ID computed from manifest.key ($computedId)"
}

# --- native host manifest ------------------------------------------------------
$manifestPath = Join-Path (Join-Path $env:LOCALAPPDATA "MediaCatcher") "$HostName.json"

Section "Native messaging host manifest"
$hostManifest = $null
if (-not (Test-Path -LiteralPath $manifestPath)) {
    Fail "manifest missing: $manifestPath (run scripts/register-native-host.ps1)"
} else {
    Ok "manifest present: $manifestPath"
    try {
        $raw = Get-Content -LiteralPath $manifestPath -Raw
        $hostManifest = $raw | ConvertFrom-Json
        Ok "manifest is valid JSON"
    } catch {
        Fail "manifest is not valid JSON: $($_.Exception.Message)"
    }
}

if ($hostManifest) {
    if ($hostManifest.name -eq $HostName) { Ok "name = $($hostManifest.name)" }
    else { Fail "name is '$($hostManifest.name)', expected '$HostName'" }

    if ($hostManifest.type -eq "stdio") { Ok "type = stdio" }
    else { Fail "type is '$($hostManifest.type)', expected 'stdio'" }

    if ($hostManifest.path -and (Test-Path -LiteralPath $hostManifest.path)) {
        Ok "host executable exists: $($hostManifest.path)"
    } else {
        Fail "host executable missing: '$($hostManifest.path)'"
    }

    $origins = $hostManifest.allowed_origins
    if ($origins -is [System.Array]) {
        Ok "allowed_origins is a JSON array ($($origins.Count) entr$(if ($origins.Count -eq 1) { 'y' } else { 'ies' }))"
        foreach ($origin in $origins) {
            if ($origin -notmatch '^chrome-extension://[a-p]{32}/$') {
                Fail "allowed_origins entry '$origin' is not 'chrome-extension://<32 chars>/' -- Chromium rejects the whole manifest"
            }
        }
        if ($computedId) {
            $expected = "chrome-extension://$computedId/"
            if ($origins -contains $expected) { Ok "allowed_origins contains the pinned ID" }
            else { Fail "allowed_origins is missing '$expected'" }
        }
        if ($ExtensionId -and (($origins -contains "chrome-extension://$ExtensionId/") -eq $false)) {
            Fail "allowed_origins does not contain -ExtensionId ($ExtensionId)"
        }
    } else {
        Fail "allowed_origins must be a JSON array; got $(if ($null -eq $origins) { 'null' } else { $origins.GetType().Name }) -- Chromium rejects a scalar"
    }
}

# --- registry registration -----------------------------------------------------
Section "Registry registration"
$browsers = @(
    @{ Name = "Chrome"; Key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName" },
    @{ Name = "Edge";   Key = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName" }
)
foreach ($browser in $browsers) {
    if (Test-Path -LiteralPath $browser.Key) {
        $registered = (Get-ItemProperty -LiteralPath $browser.Key).'(default)'
        if ($registered -eq $manifestPath) {
            Ok "$($browser.Name): registered -> $registered"
        } else {
            Fail "$($browser.Name): registered to '$registered', expected '$manifestPath'"
        }
    } else {
        Warn "$($browser.Name): no registration key (only a problem if you use $($browser.Name))"
    }
}

# --- external tools ------------------------------------------------------------
Section "External tools"
foreach ($tool in @("yt-dlp.exe", "ffmpeg.exe")) {
    $path = Join-Path (Join-Path $repoRoot "tools") $tool
    if (Test-Path -LiteralPath $path) {
        Ok "tools\$tool present"
    } else {
        Warn "tools\$tool missing (needed for stream/video engines): run scripts/fetch-tools.ps1"
    }
}

# --- summary -------------------------------------------------------------------
Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "RESULT: PASS ($($script:warnings) warning(s))" -ForegroundColor Green
    exit 0
}
Write-Host "RESULT: FAIL ($($script:failures) failure(s), $($script:warnings) warning(s))" -ForegroundColor Red
exit 1
