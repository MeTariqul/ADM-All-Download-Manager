param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$SignCertificate,
    [string]$SignPassword
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$dotnet = $null
$candidates = @()

$localSdk = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path $localSdk) {
    $candidates += $localSdk
}

$command = Get-Command dotnet -ErrorAction SilentlyContinue
if ($command -and $candidates -notcontains $command.Source) {
    $candidates += $command.Source
}

foreach ($candidate in $candidates) {
    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) {
        $dotnet = $candidate
        break
    }
}

if (-not $dotnet) {
    throw 'A working .NET SDK was not found. Install the .NET 8 SDK, then run this script again.'
}

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

if (-not $SkipTests) {
    Write-Step 'Running engine tests'
    & $dotnet run --project (Join-Path $root 'tests\ADM.Core.Tests\ADM.Core.Tests.csproj') `
        --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Tests failed; build aborted.'
    }
}

$publishRoot = Join-Path $root 'artifacts\publish'
if (Test-Path $publishRoot) {
    Remove-Item $publishRoot -Recurse -Force
}

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$publishArgs = @(
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', $selfContained,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None'
)

Write-Step 'Publishing desktop app'
& $dotnet publish (Join-Path $root 'src\ADM.App\ADM.App.csproj') @publishArgs `
    -o (Join-Path $publishRoot 'app')
if ($LASTEXITCODE -ne 0) {
    throw 'The desktop app failed to publish.'
}

Write-Step 'Bundling video tools'
$vendor = Join-Path $root 'vendor'
$ytDlp = Join-Path $vendor 'yt-dlp.exe'
if (-not (Test-Path $ytDlp)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\Get-VideoTools.ps1')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ytDlp)) {
        throw 'yt-dlp could not be downloaded. Video downloads need vendor\yt-dlp.exe.'
    }
}

$appTools = Join-Path $publishRoot 'app\tools'
New-Item -ItemType Directory -Force -Path $appTools | Out-Null
Copy-Item $ytDlp (Join-Path $appTools 'yt-dlp.exe') -Force

$ffmpeg = Join-Path $vendor 'ffmpeg.exe'
$ffprobe = Join-Path $vendor 'ffprobe.exe'
if (-not (Test-Path $ffmpeg)) {
    $ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($ffmpegCommand) {
        $ffmpeg = $ffmpegCommand.Source
    }
}
if (-not (Test-Path $ffprobe)) {
    $ffprobeCommand = Get-Command ffprobe.exe -ErrorAction SilentlyContinue
    if ($ffprobeCommand) {
        $ffprobe = $ffprobeCommand.Source
    }
}

if (-not (Test-Path $ffmpeg) -or -not (Test-Path $ffprobe)) {
    Write-Step 'Downloading ffmpeg for the publish'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\Get-VideoTools.ps1') -Ffmpeg
    $ffmpeg = Join-Path $vendor 'ffmpeg.exe'
    $ffprobe = Join-Path $vendor 'ffprobe.exe'
}

if ((Test-Path $ffmpeg) -and (Test-Path $ffprobe)) {
    Copy-Item $ffmpeg (Join-Path $appTools 'ffmpeg.exe') -Force
    Copy-Item $ffprobe (Join-Path $appTools 'ffprobe.exe') -Force
}
else {
    Write-Warning 'ffmpeg/ffprobe were not found. Video+audio merging and MP3 output will not work in this publish. Run tools\Get-VideoTools.ps1 -Ffmpeg to bundle them.'
}

Write-Step 'Publishing native messaging host'
& $dotnet publish (Join-Path $root 'src\ADM.NativeHost\ADM.NativeHost.csproj') @publishArgs `
    -o (Join-Path $publishRoot 'native')
if ($LASTEXITCODE -ne 0) {
    throw 'The native messaging host failed to publish.'
}

Write-Step 'Copying browser extension'
Copy-Item (Join-Path $root 'extension') (Join-Path $publishRoot 'extension') -Recurse

# Signing the executables removes the SmartScreen warning that unsigned
# builds get. The certificate is a PFX file; the password may come from the
# environment so it does not end up in the shell history.
function Sign-Executables([string]$Directory) {
    if (-not $SignCertificate) {
        return
    }

    if (-not (Test-Path $SignCertificate)) {
        Write-Warning "The signing certificate '$SignCertificate' was not found. The build is not signed."
        return
    }

    $password = $SignPassword
    if (-not $password) {
        $password = $env:ADM_SIGN_PASSWORD
    }

    foreach ($file in Get-ChildItem -Path $Directory -Filter '*.exe' -Recurse -File) {
        $arguments = @('sign', '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
        if ($password) {
            $arguments += @('/f', $SignCertificate, '/p', $password)
        }
        else {
            $arguments += @('/f', $SignCertificate)
        }
        $arguments += $file.FullName

        & signtool.exe @arguments
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Signing $($file.Name) failed. The build is not signed."
        }
        else {
            Write-Host "Signed $($file.Name)"
        }
    }
}

Sign-Executables (Join-Path $publishRoot 'app')
Sign-Executables (Join-Path $publishRoot 'native')

if ($SkipInstaller) {
    Write-Host ''
    Write-Host "Publish output: $publishRoot"
    return
}

Write-Step 'Building installer'
# Inno Setup may be installed per user or per machine.
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $iscc = $command.Source
    }
}

if (-not $iscc) {
    Write-Warning 'Inno Setup 6 was not found. Install it, then run: ISCC.exe installer\ADM.iss'
    Write-Host "Publish output: $publishRoot"
    return
}

& $iscc (Join-Path $root 'installer\ADM.iss')
if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup failed.'
}

Write-Step 'Done'
Write-Host "Installer: $(Join-Path $root 'artifacts\installer')"
