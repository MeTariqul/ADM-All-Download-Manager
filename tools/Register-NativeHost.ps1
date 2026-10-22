param(
    [string]$Configuration = 'Debug',
    [string]$AppExe,
    [string]$HostExe
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $AppExe) {
    $AppExe = Join-Path $root "src\ADM.App\bin\$Configuration\net8.0-windows\ADM.exe"
}

if (-not $HostExe) {
    $HostExe = Join-Path $root "src\ADM.NativeHost\bin\$Configuration\net8.0-windows\ADM.NativeHost.exe"
}

if (-not (Test-Path $HostExe)) {
    throw "Native host not found at '$HostExe'. Build the solution first."
}

if (-not (Test-Path $AppExe)) {
    Write-Warning "Desktop app not found at '$AppExe'. The native host can still forward to a running ADM instance."
}

$nativeDir = Join-Path $env:LOCALAPPDATA 'ADM\native'
New-Item -ItemType Directory -Force $nativeDir | Out-Null

$chromeManifest = Join-Path $nativeDir 'com.adm.downloadmanager.json'
$firefoxManifest = Join-Path $nativeDir 'com.adm.downloadmanager.firefox.json'

function Write-Manifest {
    param(
        [string]$Path,
        [string]$AllowedKey,
        [string]$AllowedValue
    )

    $manifest = [ordered]@{
        name        = 'com.adm.downloadmanager'
        description = 'ADM All Download Manager native messaging host'
        path        = (Resolve-Path $HostExe).Path
        type        = 'stdio'
    }
    $manifest[$AllowedKey] = @($AllowedValue)

    $manifest | ConvertTo-Json -Depth 4 | Set-Content -Path $Path -Encoding UTF8
    Write-Host "Wrote $Path"
}

Write-Manifest -Path $chromeManifest -AllowedKey 'allowed_origins' `
    -AllowedValue 'chrome-extension://nanpfakhkjjfpmibmdapaeldgodbmebh/'

Write-Manifest -Path $firefoxManifest -AllowedKey 'allowed_extensions' `
    -AllowedValue 'adm@all-download-manager.local'

$chromiumKeys = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.adm.downloadmanager',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.adm.downloadmanager',
    'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.adm.downloadmanager',
    'HKCU:\Software\Chromium\NativeMessagingHosts\com.adm.downloadmanager'
)

foreach ($key in $chromiumKeys) {
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $chromeManifest
    Write-Host "Registered $key"
}

$firefoxKey = 'HKCU:\Software\Mozilla\NativeMessagingHosts\com.adm.downloadmanager'
New-Item -Path $firefoxKey -Force | Out-Null
Set-Item -Path $firefoxKey -Value $firefoxManifest
Write-Host "Registered $firefoxKey"

# The native host finds the desktop app on its own in a development checkout,
# so this variable is only a convenience. A browser that is already running
# does not inherit a variable that was set later, so the host does not rely
# on it.
if (Test-Path $AppExe) {
    [Environment]::SetEnvironmentVariable('ADM_EXE', (Resolve-Path $AppExe).Path, 'User')
    $env:ADM_EXE = (Resolve-Path $AppExe).Path
    Write-Host "ADM_EXE=$((Resolve-Path $AppExe).Path)"
}

Write-Host ''
Write-Host 'Native host registered for the current user.'
Write-Host 'Reload the extension folder, then open it: the header should report ADM ready.'
Write-Host 'Extension ID: nanpfakhkjjfpmibmdapaeldgodbmebh'
