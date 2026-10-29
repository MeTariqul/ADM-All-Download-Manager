# ADM — All Download Manager

ADM is a high-performance, IDM-style download manager built with **.NET 8**, **WPF**, and an MVVM architecture. It accelerates HTTP(S) downloads via concurrent segmented transfers, handles FTP/FTPS, integrates with browsers through a native-messaging extension, and extracts video/audio from supported sites using the bundled **yt-dlp** engine.

![ADM workspace](docs/screenshot.png)

*ADM workspace with the silver, navy, and cyan light theme.*

## Table of Contents

- [Features](#features)
- [Technical Architecture](#technical-architecture)
- [Prerequisites](#prerequisites)
- [Build Instructions (Visual Studio)](#build-instructions-visual-studio)
- [Building the Installer](#building-the-installer)
- [Browser Extension Setup (Chrome/Firefox)](#browser-extension-setup-chromefirefox)
- [Native Messaging Configuration](#native-messaging-configuration)
- [How Downloads Work (SegmentPlanner & SegmentWorker)](#how-downloads-work-segmentplanner--segmentworker)
- [Speed Limiting](#speed-limiting)
- [FTP Client](#ftp-client)
- [YT-DLP Media Extraction](#yt-dlp-media-extraction)
- [Licensing](#licensing)
- [Storage Locations](#storage-locations)
- [Diagnostics & Logs](#diagnostics--logs)
- [License & Third-Party Notices](#license--third-party-notices)

## Features

- **Segmented HTTP(S) downloads** – Probes servers, splits files into byte ranges, downloads in parallel (up to 32 connections). Slow segments can be dynamically split while downloading.
- **FTP/FTPS downloads** – Built-in FTP client with `REST` resume support. FTP transfers use a single stream (no range support).
- **Pause/resume** – Partial data written to `.admpart` files; downloads survive pauses and app restarts.
- **Queue management** – Limits concurrent downloads; remaining items stay queued.
- **Speed limiting** – Global token-bucket speed cap applied across all active connections.
- **Resilient transfers** – Automatic retries with exponential backoff, ETag/Last-Modified validation before resuming, single-stream fallback for servers without `Range` support, strict Content-Range validation.
- **Proxy & authentication** – HTTP/SOCKS proxy with credentials, HTTP Basic auth, custom cookie headers.
- **Smart guards** – Disk space checks, duplicate URL detection, configurable file-exists policy (rename/overwrite/skip).
- **Clipboard monitoring** – Prompts to download copied links.
- **Per-download options** – Speed limit, connection count, priority, checksum verification (MD5/SHA1/SHA256).
- **Scheduler** – Start queued downloads at a time and stop active downloads at a time.
- **Browser integration** – Extension + native messaging host captures links/media from Chrome, Edge, Brave, Chromium, and Firefox.
- **YT-DLP extraction** – Download video/audio/playlists from supported sites as MP4/MKV/MP3 with format selection, subtitles, and FFmpeg merging.
- **Ed25519 licensing** – Online daily verification with device binding.

## Technical Architecture

The solution consists of three main projects and tests:

### Projects

| Project | Type | Purpose |
| --- | --- | --- |
| `src/ADM.App/` | WPF EXE (`net8.0-windows`) | UI layer (MVVM), views, view models, services (tray, startup registration, app updates), and app lifecycle (`App.xaml.cs`). Depends on `ADM.Core`. |
| `src/ADM.Core/` | Class Library (`net8.0`) | Core domain logic: download engine, networking, media extraction orchestration, licensing, settings, storage, utilities. Platform-agnostic where possible. |
| `src/ADM.NativeHost/` | Console EXE (`net8.0-windows`) | Native messaging host for browser extensions. Communicates via stdin/stdout with Chrome/Firefox extensions and forwards requests to the ADM app via named pipes (`PipeServer`/`PipeClient`). Depends on `ADM.Core`. |
| `tests/ADM.Core.Tests/` | Test project | Engine/regression tests. |

### Separation of Concerns

- **ADM.App (Presentation)**: WPF views (`Views/*.xaml`), view models (`ViewModels/MainViewModel.cs`), app services (`App.xaml.cs`, `Services/*`). Handles UI state, user interactions, tray icon, and coordinates with `DownloadManager`. Uses MVVM pattern with `INotifyPropertyChanged`.
- **ADM.Core (Domain/Infrastructure)**: Business logic in `Engine/` (download orchestration), `Net/` (HTTP/FTP/probes), `Media/` (yt-dlp integration), `Licensing/` (Ed25519 verification, client), `Settings/` (persistence), `Storage/` (download state), `Util/`. Core has no WPF/UI dependencies.
- **ADM.NativeHost (IPC Bridge)**: Bridges browser extension (native messaging protocol over stdin/stdout) to the running ADM app. Uses `Adm.Core.Ipc.NativeMessagingProtocol` and named pipes to communicate with `Adm.Core.Ipc.PipeServer` in the app.

### Key IPC Components

- `Adm.Core.Ipc.PipeServer` / `PipeClient` – Named pipe communication between NativeHost and ADM.App.
- `Adm.Core.Ipc.NativeMessagingProtocol` – Native messaging message format for browser extensions.
- `Adm.Core.Ipc.PipeProtocol` – Pipe message format for app↔host communication.

### Storage Configuration

- **Settings**: `%APPDATA%\ADM\settings.json` via `Adm.Core.Settings.SettingsStore`
- **Downloads state**: `%APPDATA%\ADM\downloads.json` via `Adm.Core.Storage.DownloadStore`
- **License**: `%APPDATA%\ADM\license.json` via `Adm.Core.Licensing.LicenseStore`
- **Logs**: Next to settings file, `adm.log` with rotation at 2 MiB (retains one previous copy)
- **App key override**: `appkey.txt` next to executable for license key override

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** (or Visual Studio 2022 with .NET 8 workload)
- **Visual Studio 2022** (recommended) with:
  - .NET Desktop Development workload
  - Windows App SDK / WPF support
- **Inno Setup 6** (for building installer) – `C:\Users\<user>\AppData\Local\Programs\Inno Setup 6\ISCC.exe` or installed in Program Files
- **YT-DLP/FFmpeg/FFprobe** (optional but recommended for video downloads) – bundled via `tools/Get-VideoTools.ps1` or placed in `artifacts/publish/app/tools/`

## Build Instructions (Visual Studio)

1. **Clone the repository**
   ```bash
   git clone https://github.com/MeTariqul/ADM-All-Download-Manager.git
   cd "ADM All Download Manager"
   ```

2. **Open the solution**
   - Open `ADM.sln` in Visual Studio 2022
   - Restore NuGet packages (should happen automatically)

3. **Set startup project**
   - Set `ADM.App` as the startup project

4. **Build**
   - Build solution in `Debug` or `Release` configuration (`Build > Build Solution`)
   - Output goes to `src/ADM.App/bin/<Configuration>/net8.0-windows/`

5. **Run**
   - Press `F5` to run with debugging, or `Ctrl+F5` without debugging

### Build via PowerShell (alternative)

Use the provided build script:
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Release
```

Options:
- `-SkipTests` – skip engine tests
- `-SkipInstaller` – build/publish without creating installer
- `-FrameworkDependent` – publish framework-dependent instead of self-contained
- `-SkipVideoTools` – skip bundling video tools

The script builds all projects, runs tests (unless skipped), publishes app/native/extension, bundles video tools, and runs Inno Setup.

## Browser Extension Setup (Chrome/Firefox)

The browser extension is in the `extension/` directory.

### Chrome / Edge / Brave (Chromium)

1. Open `chrome://extensions/` (or `edge://extensions/`)
2. Enable **Developer mode** (toggle in top-right)
3. Click **Load unpacked**
4. Select the `extension/` folder from the repository (or the installed location)
5. The ADM extension should appear

### Firefox

Firefox uses `manifest.firefox.json`. For development/testing:

1. Open `about:debugging#/runtime/this-firefox`
2. Click **Load Temporary Add-on...**
3. Select `extension/manifest.firefox.json` (or copy it over `manifest.json` in a writable copy)
4. Note: Temporary add-ons are removed when Firefox closes; permanent installation requires Mozilla signing

See `extension/README.md` for more details.

## Native Messaging Configuration

The NativeHost (`ADM.NativeHost`) communicates with browsers via native messaging. The app/installer sets up registry keys pointing to the native host manifest.

### Manifest Locations

- **Chrome/Chromium**: `extension/com.adm.nativehost.json` (installed with app)
- **Firefox**: `extension/com.adm.nativehost.firefox.json`

### Registry Keys (set by installer or manually)

#### Windows (Current User)

For Chrome/Chromium:
```
HKEY_CURRENT_USER\Software\Google\Chrome\NativeMessagingHosts\com.adm.nativehost
```
Value: path to `com.adm.nativehost.json`

For Edge:
```
HKEY_CURRENT_USER\Software\Microsoft\Edge\NativeMessagingHosts\com.adm.nativehost
```

For Firefox:
```
HKEY_CURRENT_USER\Software\Mozilla\NativeMessagingHosts\com.adm.nativehost
```
Value: path to `com.adm.nativehost.firefox.json`

The manifest points to the `ADM.NativeHost.exe` executable. The installer handles this automatically; for development, ensure paths are correct and the app is running.

## How Downloads Work (SegmentPlanner & SegmentWorker)

### SegmentPlanner (`Adm.Core.Engine.SegmentPlanner`)

- **`MinimumSegmentSize = 512 * 1024`** – Segments smaller than this aren't worth a separate connection
- **`MaximumConnections = 32`**
- **`Plan(totalBytes, requestedConnections)`**:
  - If `totalBytes <= 0`: creates one segment `{Start=0, End=-1, Position=0}` (streaming/single-stream, no resume support)
  - Otherwise clamps connections to `[1, 32]` and limits by `totalBytes / MinimumSegmentSize`
  - Splits file evenly: `baseSize = totalBytes / connections`, remainder distributed to first segments
  - Creates `SegmentRecord` objects with `{Start, End, Position}`

### SegmentWorker (`Adm.Core.Engine.SegmentWorker`)

- Downloads a single `Segment` with HTTP Range requests
- **Retry logic**: Retries up to `MaxRetries` times on transient failures with exponential backoff (`delay = min(10, 2^attempt)`)
- **Transfer**:
  - For segmented downloads: requests `Range: bytes=start-end` with `IfRange` header (using ETag if strong)
  - Validates response is `206 PartialContent` (throws if server returns `200 OK` ignoring ranges)
  - Validates `Content-Range` matches expected range and `Content-Length` matches `end-start+1`
  - Writes to file at `position` using `RandomAccess.WriteAsync` with a shared file handle
  - Updates segment position as bytes are written
- **Streaming downloads** (unknown length, `End < 0`): No range requests, retries by restarting from beginning if connection drops mid-stream
- **Buffer**: 64KB buffers from `ArrayPool<byte>`, throttled via `SpeedLimiter`

### DownloadTask Orchestration (`Adm.Core.Engine.DownloadTask`)

1. **Probe**: `HttpProbe.ProbeAsync` sends a GET with `Range: bytes=0-0` to detect range support, total size, ETag, Last-Modified, Content-Disposition, Content-Type
2. **Prepare**: If no file path, resolves filename via `FileNameResolver` (prefers `filename*`, then `filename`, then URL; adds extension from MIME type if missing). Creates temp file preallocated to total size if ranges supported.
3. **Plan segments**: Uses `SegmentPlanner.Plan()` or creates single streaming segment
4. **Restore**: For paused downloads, validates temp file exists with correct size, probes server again, checks ETag/Last-Modified match, restores segment positions
5. **Transfer**: Starts `SegmentWorker` for each incomplete segment. Monitors workers, reports progress every 250ms. Dynamically splits large busy segments when connections available (`SplitBusySegments`)
6. **Complete**: Moves `.admpart` to final path per `FileExistsPolicy`, verifies checksum if configured, cleans up

## Speed Limiting

**`Adm.Core.Engine.SpeedLimiter`** (token bucket):

- **Burst**: 128 KB burst capacity
- **Token refill**: Tokens refill at rate `BytesPerSecond` based on elapsed time
- **Throttling**: `ThrottleAsync(byteCount)` acquires semaphore, refills tokens, sleeps if insufficient tokens, consumes tokens
- **Shared**: Single instance shared by all workers of a download
- **Disabled** when `BytesPerSecond == 0`

Workers call `context.Limiter.ThrottleAsync(read, cancellationToken)` after reading from the network stream and before writing to disk, applying back-pressure to TCP reads.

## FTP Client

**`Adm.Core.Net.FtpClient`**:

- **Connection**: TCP socket to host:port. Supports implicit TLS (encrypts entire session from start via `SslStream`).
- **Authentication**: `USER`/`PASS` (defaults to anonymous/`adm@localhost`)
- **Binary mode**: `TYPE I`
- **Passive mode**: `PASV` to get data channel address/port
- **Resume**: `REST <offset>` before `RETR` to resume from offset
- **Download**: Opens data connection, reads stream to destination, reports progress, waits for `226 Transfer complete`
- **Size**: `SIZE <path>` returns file size or -1

**`Adm.Core.Engine.FtpDownloadTask`**:

- Parses URL with `FtpUrl`, creates `FtpClient`, connects, gets size if available
- Resumes from existing `.admpart` file size if present
- Downloads to temp file, writes with progress updates every 300ms
- Handles pause/cancel by checking cancellation token
- Moves to final path per file-exists policy on completion

## YT-DLP Media Extraction

**`Adm.Core.Media.YtDlpService`**:

- **Locator**: `YtDlpLocator` finds `yt-dlp.exe`, `ffmpeg.exe`, `ffprobe.exe` in app directory, tools folder, or PATH
- **Process execution**: Spawns bundled `yt-dlp.exe` as a separate process with `UseShellExecute=false`, `CreateNoWindow=true`, captures stdout/stderr
- **Common arguments** (`AddCommonArguments`): `--no-playlist`, `--no-warnings`, `--no-colors`, `--no-mtime`, `--ffmpeg-location <dir>` if ffmpeg found
- **Proxy/cookies**: Adds `--proxy` and `--cookies-from-browser` if configured
- **GetVersionAsync()**: Runs `yt-dlp --version`
- **UpdateAsync()**: Copies yt-dlp to temp, runs `--update-to stable`, copies updated file back in place (yt-dlp updates itself)
- **GetInfoAsync(url, allowPlaylist)**: Runs `yt-dlp -J --skip-download --no-progress --socket-timeout 20 <url>`, parses JSON with `YtDlpFormatParser`. Throws `YtDlpException` on errors.
- **GetPlaylistAsync(url)**: Runs `yt-dlp -J --flat-playlist --skip-download --no-progress`, parses with `YtDlpPlaylistParser`
- **Error parsing**: Strips ANSI codes, extracts `ERROR:` lines, falls back to last lines

**`Adm.Core.Media.YtDlpDownloadTask`**:

- Orchestrates video/audio downloads via yt-dlp
- Uses format selection from parsed `VideoInfo`/formats
- Handles merging with FFmpeg when separate video/audio streams needed
- Writes progress by parsing yt-dlp output
- Supports subtitles (`--write-subs`, `--sub-langs`)

**`Adm.Core.Media.YtDlpFormatParser`** & **`YtDlpPlaylistParser`** parse yt-dlp JSON output into `VideoInfo`, `VideoFormat`, `PlaylistInfo` models.

## Licensing

**Ed25519-based licensing** (`Adm.Core.Licensing`):

- **`LicenseClient`** (`https://metariqul.vercel.app/api/license`): `ActivateAsync`, `VerifyAsync`, `RequestKeyAsync`, `GetPublicKeyAsync`. Sends app key in `x-app-key` header.
- **`LicenseService`**: Manages license state. **Checks license online once per day** (stores `LastVerifiedAt` in `license.json`). Verifies token signature offline with shipped public key (`LicenseConfiguration.PublicKeyPem`). Binds to device via `DeviceIdentity`. States: `Active`, `NotActivated`, `Expired`, `Suspended`, `DeviceLimit`, `RevokedDevice`, `Invalid`, `Unreachable`, `Tampered`.
- **`LicenseToken`**: Base64URL-encoded payload + Ed25519 signature (`.Base64Url`). Claims include device ID, expiry, recheck time.
- **App behavior**: `App.xaml.cs` calls `EnsureLicensedAsync()` before showing main window. If not licensed, shows `LicenseWindow`. When verified, shows thank-you note with option to **Remove licence** (clears stored license).

## Storage Locations

| Data | Location | Managed By |
| --- | --- | --- |
| Settings | `%APPDATA%\ADM\settings.json` | `SettingsStore` |
| Download state | `%APPDATA%\ADM\downloads.json` | `DownloadStore` |
| License | `%APPDATA%\ADM\license.json` | `LicenseStore` |
| Logs | `%APPDATA%\ADM\adm.log` (+ `adm.log.1` on rotation) | `DiagnosticsLog` |
| App key override | `<appdir>\appkey.txt` | `AppKeyStore` |

## Diagnostics & Logs

- **`Adm.Core.Util.DiagnosticsLog`**: Rotates at 2 MiB, retains one previous copy (`adm.log.1`). Logs only URL **origin** for download/clipboard events (omits credentials, path, query, fragment).
- Logs configured in `App.xaml.cs` during startup based on `settings.WriteLogFile`.

## License & Third-Party Notices

See `LICENSE` and `THIRD-PARTY-NOTICES.md`. Uses `yt-dlp`, `ffmpeg`, `ffprobe`. Browser extension is unpacked for development.
