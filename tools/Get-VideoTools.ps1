param(
    [switch]$Ffmpeg,
    [switch]$Force
)

# Downloads the external video engine used by ADM. yt-dlp is required for video
# pages; ffmpeg/ffprobe are required for merged video+audio and MP3 output.
# Run from the repository root:  powershell -ExecutionPolicy Bypass -File tools\Get-VideoTools.ps1
# Add -Ffmpeg to also fetch ffmpeg/ffprobe into vendor\.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $root 'vendor'
New-Item -ItemType Directory -Force -Path $vendor | Out-Null

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

# Streams a URL to disk. Invoke-WebRequest buffers the whole file in memory and
# stalls on large archives, so the download is done with HttpClient instead.
function Save-Download([string]$Uri, [string]$Path, [long]$MinimumBytes) {
    Add-Type -AssemblyName System.Net.Http

    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $true
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(30)

    try {
        $response = $client.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "The download failed with HTTP $($response.StatusCode)."
        }

        $total = $response.Content.Headers.ContentLength
        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $target = [System.IO.File]::Create($Path)

        try {
            $buffer = New-Object byte[] 1048576
            $read = 0
            $done = 0L
            $lastReport = 0L

            while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $target.Write($buffer, 0, $read)
                $done += $read

                if ($total -gt 0 -and ($done - $lastReport) -ge 10MB) {
                    $lastReport = $done
                    Write-Host ("  {0:N0} MB of {1:N0} MB" -f ($done / 1MB), ($total / 1MB))
                }
            }
        }
        finally {
            $target.Dispose()
            $source.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }

    if (-not (Test-Path $Path) -or (Get-Item $Path).Length -lt $MinimumBytes) {
        Remove-Item $Path -Force -ErrorAction SilentlyContinue
        throw "The download of '$Uri' is incomplete. Check your connection and try again."
    }
}

$ytDlp = Join-Path $vendor 'yt-dlp.exe'
if ($Force -or -not (Test-Path $ytDlp)) {
    Write-Step 'Downloading yt-dlp'
    Save-Download 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' $ytDlp 1MB
    Write-Host "Saved $ytDlp"
}
else {
    Write-Host "yt-dlp already present: $ytDlp"
}

if ($Ffmpeg) {
    # Note: $Ffmpeg is the switch parameter, so the paths use different names.
    $ffmpegPath = Join-Path $vendor 'ffmpeg.exe'
    $ffprobePath = Join-Path $vendor 'ffprobe.exe'

    if ($Force -or -not (Test-Path $ffmpegPath) -or -not (Test-Path $ffprobePath)) {
        Write-Step 'Downloading ffmpeg'
        $zip = Join-Path $env:TEMP ("adm-ffmpeg-" + [guid]::NewGuid().ToString('N') + '.zip')
        $extract = Join-Path $env:TEMP ("adm-ffmpeg-" + [guid]::NewGuid().ToString('N'))

        # The "essentials" build is much smaller than the full one and covers
        # everything ADM needs: muxing, merging and MP3 encoding.
        $sources = @(
            'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip',
            'https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip'
        )

        try {
            $downloaded = $false
            foreach ($source in $sources) {
                try {
                    Save-Download $source $zip 10MB
                    $downloaded = $true
                    break
                }
                catch {
                    Write-Warning $_.Exception.Message
                }
            }

            if (-not $downloaded) {
                throw 'No ffmpeg archive could be downloaded.'
            }

            Write-Host 'Extracting...'
            Expand-Archive -Path $zip -DestinationPath $extract -Force

            $ffmpegExe = Get-ChildItem $extract -Recurse -Filter 'ffmpeg.exe' |
                Select-Object -First 1
            if (-not $ffmpegExe) {
                throw 'The downloaded archive did not contain ffmpeg.exe.'
            }

            $probeExe = Get-ChildItem $ffmpegExe.Directory.FullName -Filter 'ffprobe.exe' |
                Select-Object -First 1
            if (-not $probeExe) {
                throw 'The downloaded archive did not contain ffprobe.exe.'
            }

            Copy-Item $ffmpegExe.FullName $ffmpegPath -Force
            Copy-Item $probeExe.FullName $ffprobePath -Force
            Write-Host "Saved $ffmpegPath"
            Write-Host "Saved $ffprobePath"
        }
        finally {
            Remove-Item $zip -Force -ErrorAction SilentlyContinue
            Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Host "ffmpeg already present: $ffmpegPath"
    }
}

Write-Host ''
Write-Host 'Video tools are ready.' -ForegroundColor Green
