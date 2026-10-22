# ADM — All Download Manager

A Windows download manager built with **.NET 8** and **WPF**, with a segmented
HTTP engine, a browser extension, and a per-user installer. It is an IDM-style
desktop app: multiple connections per file, pause/resume across restarts,
download queues, a speed limiter, and browser download capture. Video and audio
pages are handled by the bundled **yt-dlp** engine, so YouTube and many other
sites download as complete MP4/MKV/MP3 files instead of separate streams.

![ADM minimal UI](docs/screenshot.png)

## Features

- **Segmented downloads** — probes the server, splits the file into ranges and
  downloads them in parallel (up to 32 connections). Slow segments can be split
  again while the download runs.
- **FTP downloads** — `ftp://` and `ftps://` addresses are downloaded with a
  built-in FTP client, with resume through the `REST` command.
- **Pause and resume** — partial data is written to a preallocated `.admpart`
  file, so downloads survive pauses and app restarts without restarting from zero.
- **Queue** — limits how many downloads run at once, the rest stay queued.
- **Speed limiter** — global cap applied across all active connections.
- **Resilient transfers** — retries with backoff, `ETag`/size validation before
  resuming, single-stream fallback for servers without `Range` support.
- **Proxy and authentication** — an HTTP/SOCKS proxy with credentials, HTTP basic
  authentication, and a cookie header for servers that need a session.
- **Smart guards** — free disk space is checked before a download starts, the
  same link is never queued twice by accident, and an existing file is renamed
  instead of being overwritten.
- **Clipboard monitor** — copy a link anywhere and ADM asks whether to download it.
- **Per-download options** — set the speed limit, connection count, priority and
  an expected checksum for a single download.
- **Checksum verification** — compare the finished file with an MD5, SHA1 or
  SHA256 hash and see the result in the list.
- **Scheduler** — hold queued downloads until a start time and pause active ones
  at a stop time.
- **Search and context menu** — filter the list by name or link, and right-click
  a row to pause, restart, copy the link, change its options or remove it.
- **Notifications** — a notification when a download finishes or fails, with an
  optional "open the file" and "quit when the queue is empty".
- **Diagnostics log** — a rolling log in `%APPDATA%\ADM\adm.log` for support.
- **Dark theme** — a dark colour scheme next to the light one.
- **Video and audio downloads** — paste a YouTube/Vimeo/Twitch/etc. page or click
  the **Download** button attached to the playing video. A picker shows every
  available resolution, container (MP4/MKV), audio-only M4A and MP3. Video-only
  streams are merged with the best audio automatically. Playlists can be
  downloaded entry by entry, and subtitles are fetched when the site offers them.
- **Video engine updates** — the bundled yt-dlp updates itself in the
  background, because site extractors break regularly. A button in the settings
  window updates it on demand.
- **Browser capture** — Chromium and Firefox extension that hands downloads to
  ADM, detects page media, and adds a right-click **Download with ADM** menu plus
  a **Download video with ADM** item for videos. It also forwards the browser
  cookies, and can grab every file link on a page at once.
- **Desktop conveniences** — paste multiple links, drag and drop links, category
  filters, open file/folder, clear completed, single-instance URL forwarding.
- **System tray** — closing the window keeps ADM running in the notification
  area, so downloads continue and the browser extension stays reachable. The
  tray icon has **Show ADM**, **Pause all**, **Resume all** and **Exit ADM**.
- **Minimal light UI** — quiet layout, hairline rows, keyboard-friendly quick add.

## Requirements

- Windows 10 or 11
- To build from source: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Optional: [Inno Setup 6](https://jrsoftware.org/isinfo.php) to compile the installer
- For video downloads: `yt-dlp.exe` (required) and `ffmpeg.exe`/`ffprobe.exe`
  (needed for merged video+audio and MP3). Published builds include them in
  `tools\`; source builds look in `vendor\` or on `PATH`.

## Quick start

```powershell
# Fetch yt-dlp and ffmpeg into vendor\
powershell -ExecutionPolicy Bypass -File tools\Get-VideoTools.ps1 -Ffmpeg

# Build everything
dotnet build ADM.sln

# Run the app from source
dotnet run --project src\ADM.App\ADM.App.csproj
```

If `dotnet` is not on your PATH but the SDK is installed per-user, use
`%USERPROFILE%\.dotnet\dotnet.exe` in place of `dotnet`. ffmpeg is only needed
for merged video+audio and MP3 output; if it is already on your PATH the source
build uses it and the download can be skipped.

### Run the engine tests

The test project is a dependency-free console harness with its own local
range-capable HTTP server:

```powershell
dotnet run --project tests\ADM.Core.Tests\ADM.Core.Tests.csproj
```

It covers segment planning, file-name handling, probing, byte-for-byte
verification of a segmented download, single-stream fallback,
pause → app restart → resume, video format grouping, URL detection, progress
parsing, video record persistence, checksums, proxy and basic-auth headers,
free disk space, the duplicate policy, playlist parsing and the retry backoff.

To run the real yt-dlp probe/download and pause/resume tests (network access
required):

```powershell
$env:ADM_YTDLP_INTEGRATION = '1'
dotnet run --project tests\ADM.Core.Tests\ADM.Core.Tests.csproj
Remove-Item Env:ADM_YTDLP_INTEGRATION
```

The FTP test needs a local FTP server; point the test at one with
`ADM_FTP_URL` and enable it with `ADM_FTP_INTEGRATION=1`:

```powershell
$env:ADM_FTP_INTEGRATION = '1'
$env:ADM_FTP_URL = 'ftp://user:pass@127.0.0.1:2121/file.bin'
dotnet run --project tests\ADM.Core.Tests\ADM.Core.Tests.csproj
```

### Publish and build the installer

```powershell
# Tests + self-contained single-file publish (+ installer if Inno Setup is installed)
powershell -ExecutionPolicy Bypass -File build.ps1

# Publish only, skip Inno Setup
powershell -ExecutionPolicy Bypass -File build.ps1 -SkipInstaller

# Smaller build that requires the .NET 8 Desktop Runtime
powershell -ExecutionPolicy Bypass -File build.ps1 -FrameworkDependent -SkipInstaller
```

Output goes to `artifacts\publish\` (`app`, `native`, `extension`) and
`artifacts\installer\`. The build copies `yt-dlp.exe`, `ffmpeg.exe`, and
`ffprobe.exe` into `artifacts\publish\app\tools\`; missing tools are downloaded
into `vendor\` first, so the installed app does not depend on PATH. The installer
is per-user (`PrivilegesRequired=lowest`), so it does not need administrator
rights. The bundled ffmpeg build adds roughly 200 MB to the published folder.

The installer is produced by [Inno Setup 6](https://jrsoftware.org/isinfo.php).
`build.ps1` looks for `ISCC.exe` in the per-user and per-machine locations; when
Inno Setup is missing the publish still succeeds and only the installer step is
skipped. The script uses only plain Pascal Script, so it compiles with any
Inno Setup 6 release.

The app is not code-signed; Windows SmartScreen may show a warning for builds
you produced yourself.

## Browser extension

The extension lives in `extension\`. It communicates with the app through a
native messaging host named `com.adm.downloadmanager`. On supported video pages
it overlays a small **Download** button on the playing video; clicking it opens
ADM's format picker for that page. The popup has a **Download video** button
for the current tab, and a **Download video with ADM** entry in the right-click
menu.

**Chrome, Edge, Brave, Chromium**

1. Install ADM (the installer registers the native host), or register a local
   build during development:
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\Register-NativeHost.ps1
   ```
2. Open `chrome://extensions` (or `edge://extensions`) and enable
   **Developer mode**.
3. Choose **Load unpacked** and select the `extension` folder.
4. Open the extension popup — it should report **ADM ready**. If it reports
   **ADM not running**, click **Open ADM** to start the app.

The manifest pins the extension ID to
`nanpfakhkjjfpmibmdapaeldgodbmebh`, matching the native host manifest.

**Firefox**

Copy `extension\manifest.firefox.json` over `manifest.json` in a writable copy
of the folder, then load it from `about:debugging#/runtime/this-firefox`.
Temporary add-ons last until Firefox closes; permanent installation requires
Mozilla signing.

See [`extension/README.md`](extension/README.md) for extension behaviour and
development details.

## Project layout

```
src/ADM.Core/          Download engine, models, storage, pipe/native-messaging protocol
src/ADM.Core/Media/    yt-dlp locator/service, format parser, video download task
src/ADM.Core/Net/      HTTP probe, proxy/auth helpers, FTP client
src/ADM.Core/Licensing/  Ed25519 verification, licence tokens, activation client
src/ADM.Core/Util/     File names, categories, checksums, disk space, diagnostics log
src/ADM.App/           WPF desktop app (UI, view models, pipe server, settings)
src/ADM.App/Services/  Pipe server, native host helper, notification-area icon
src/ADM.NativeHost/    Native messaging host used by the browser extension
tests/ADM.Core.Tests/  Console test harness and local HTTP test server
extension/             Chromium/Firefox MV3 extension (background, content, popup)
installer/             Inno Setup script
tools/                 Dev helpers (native host registration, icon generation, video tools)
vendor/                Local yt-dlp/ffmpeg binaries (ignored by git)
build.ps1              Test + publish + installer pipeline
```

## How it works

1. `HttpProbe` sends a ranged request to learn the size, `ETag`, and whether the
   server supports ranges.
2. `SegmentPlanner` divides the file into non-overlapping ranges. A single
   preallocated `.admpart` file backs all segments, and each `SegmentWorker`
   writes only its own byte range (opened with shared access).
3. `DownloadTask` runs the workers, monitors progress, retries failures, and
   splits slow segments while data is still flowing. When every range is
   complete it validates the length and moves the file to its final name.
4. `DownloadManager` owns the queue, concurrency limit, persistence, and
   pause/resume orchestration. It also applies the duplicate policy, the
   file-exists policy, the free-space check, the retry policy and the queue
   priority.

### Network and safety

- `NetworkOptions` builds the proxy and the `Authorization` header once, and
  `HttpProbe` applies them to every request, including the segment workers.
- `DiskSpace.Check` refuses a download that does not fit on the target drive
  and leaves a small margin so the system stays usable.
- `Checksum.Compute` hashes the finished file and compares it with the expected
  value; the result is shown in the list.
- `DiagnosticsLog` writes a rolling log to `%APPDATA%\ADM\adm.log`.

### The notification-area icon

`TrayIconService` talks to the shell directly (`Shell_NotifyIcon`), so no
Windows Forms stack is needed. The main window owns the icon, which keeps the
callback plumbing simple: the window stays alive while hidden, and the shell
keeps sending the click messages to it. The tooltip mirrors the queue summary,
and the right-click menu offers **Show ADM**, **Pause all downloads**,
**Resume all downloads** and **Exit ADM**.

### Video downloads

1. A video-page URL is recognised by `VideoUrlDetector` and opens
   `VideoDownloadWindow` instead of the HTTP engine. Direct media files
   (`.mp4`, `.mp3`, ...) still use the segmented HTTP engine, which is faster
   for a single file.
2. `YtDlpService` runs `yt-dlp -J --no-playlist` and `YtDlpFormatParser` groups
   the raw formats into one row per resolution, preferring direct HTTPS streams
   over duplicate HLS variants. Each row is a yt-dlp selector such as
   `137+bestaudio[ext=m4a]/137+bestaudio/best`; audio-only rows produce M4A or
   MP3.
3. `YtDlpDownloadTask` runs yt-dlp with a machine-readable progress template,
   maps bytes/speed/ETA into the normal list, and lets ffmpeg merge the selected
   video and audio. Pausing kills the process tree; resuming runs yt-dlp again
   with `--continue`, reusing its `.part` files.
4. The final path reported by yt-dlp replaces the tentative `.mp4` path, so the
   list opens the actual MP4, MKV, M4A, or MP3 that was produced.

The in-page browser button sends `OPEN_VIDEO` through native messaging. The
native host forwards it to the running app over the named pipe; if ADM is not
running, it starts ADM and then opens the picker through the same pipe.

### Local communication

- **Desktop IPC** — named pipe `ADM.DownloadManager.v1`, one line of JSON per
  request. Used for single-instance URL forwarding and by the native host.
- **Native messaging** — 4-byte little-endian length prefix followed by JSON,
  as required by Chromium and Firefox.

The native host looks for `ADM.exe` next to itself, which covers the installed
layout. In a development checkout it walks up to the solution file and checks
the build outputs, so the extension can start ADM without a browser restart.
`ADM_EXE` overrides the search when it is set.

### Where data is stored

| What | Path |
| --- | --- |
| Download list and resume state | `%APPDATA%\ADM\downloads.json` |
| Settings | `%APPDATA%\ADM\settings.json` |
| Licence | `%APPDATA%\ADM\license.json` |
| Diagnostics log | `%APPDATA%\ADM\adm.log` |
| Default download folder | `%USERPROFILE%\Downloads\ADM` |
| Partial HTTP downloads | `<download folder>\<file>.admpart` |
| Partial video downloads | `<download folder>\<file>*.part` (managed by yt-dlp) |
| Bundled video tools | `tools\yt-dlp.exe`, `tools\ffmpeg.exe`, `tools\ffprobe.exe` |

Settings can be changed in the app (gear icon): folder, connections per
download, simultaneous downloads, speed limit, auto-resume, start with Windows,
delete confirmation, proxy and credentials, cookie header, clipboard monitor,
disk-space check, duplicate and file-exists policies, retry policy, schedule,
notifications, dark theme and the diagnostics log. The settings window also
reports whether yt-dlp and ffmpeg were found and can update yt-dlp.

### Closing the window

Closing the main window does not quit ADM. The app keeps running in the
notification area and the downloads continue; a balloon tip points this out the
first time. Use the tray icon to bring the window back, pause or resume the
queue, or to **Exit ADM** for real.

## Licensing

ADM needs a licence key. The key is exchanged for a signed token, and the token
is verified **offline** with a public key that ships inside the app, so the app
keeps working without a network connection.

| Step | What happens |
| --- | --- |
| Activate | `POST /api/license/activate` with the key, the device id and the app key. The server returns a signed token, which is stored in `%APPDATA%\ADM\license.json`. |
| Launch | The token is verified locally: the Ed25519 signature, the device id and the expiry. No network is used. |
| Re-check | When the token asks for it (`r`), `POST /api/license/verify` is called. A blocked, suspended, expired or revoked licence takes effect here. |

The token format is `base64url(payload).base64url(signature)`. The payload
carries the device id (`d`), the expiry (`e`) and the next re-check time (`r`),
all in Unix milliseconds.

**Device id** — a stable per-machine value. It is derived from the Windows
`MachineGuid` and the computer name and then hashed, so the raw identifier is
never sent to the server. The activation window shows it and can copy it to the
clipboard.

**Offline behaviour** — the signature check needs no network. If the server
cannot be reached when a re-check is due, ADM keeps running until the licence
itself expires and says so in the log, rather than locking a paying user out
because of a flaky connection. A server that *does* answer and refuses the
licence locks the app immediately.

**The app key** — every request carries an `x-app-key` header that identifies
this build. It is set in `LicenseConfiguration.AppKey`. It can also be supplied
at run time with the `ADM_APP_KEY` environment variable or an `appkey.txt` file
next to the executable, so it can be changed without a rebuild.

The Ed25519 verification is implemented in `Adm.Core.Licensing.Ed25519`
(RFC 8032), so the app needs no cryptography package. It can confirm a token
but never create one.

## Limitations

- Direct downloads are HTTP/HTTPS and FTP/FTPS. No BitTorrent.
- Video support depends on the bundled yt-dlp build and on site extractors. The
  video engine updates itself in the background, and the settings window has a
  button to update it on demand; if extraction still fails, run
  `tools\Get-VideoTools.ps1 -Force` to refresh it.
- A playlist is downloaded entry by entry at the best available quality; the
  format picker is used for single videos.
- Live streams are not supported yet; the picker reports them as such.
- DRM-protected services (Netflix, Prime Video, Disney+, etc.) are not supported.
- Merged video+audio and MP3 output require ffmpeg. Without it, choose a
  progressive MP4/M4A format.
- The browser cookies are forwarded to yt-dlp only when a browser is named in
  the settings; without them, age-restricted, private, or login-only videos may
  fail even though the normal user agent and referrer are used.
- Media sniffing is best-effort; some streaming sites split audio and video into
  separate requests that ADM cannot merge without yt-dlp.
- Servers without range support download in a single stream and cannot resume.
- The app update check needs a release feed; the built-in URL is a placeholder
  and must be pointed at your own feed before it reports anything.
- Firefox needs a signed package for a permanent extension install.
- The UI is English-only and the app is Windows-only.

Download only content you have the right to save. Site terms of service still
apply. Bundled `yt-dlp` and FFmpeg binaries remain under their own upstream
licenses; see the [yt-dlp](https://github.com/yt-dlp/yt-dlp) and
[FFmpeg](https://ffmpeg.org/legal.html) projects for details.
