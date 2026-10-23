# ADM browser extension

The extension hands downloads and page media to the ADM desktop app through a
native messaging host.

## Chrome / Edge / Brave

1. Install ADM with `ADM-Setup-*.exe`. The installer registers the native host
   for Chrome, Edge, Brave and Chromium (per-user registry, no admin needed).
2. Open `chrome://extensions` (or `edge://extensions`).
3. Enable **Developer mode**.
4. Click **Load unpacked** and select this `extension` folder (normally
   `%LOCALAPPDATA%\Programs\ADM\extension`).
5. Open the extension. The header should show **ADM ready**. If it shows
   **ADM not running**, click **Open ADM** to start the desktop app. If it shows
   **Extension not connected**, reinstall ADM or register the host with
   `tools\Register-NativeHost.ps1`, then restart the browser and reload the
   extension.

The manifest contains a fixed public key, so the extension ID is always
`nanpfakhkjjfpmibmdapaeldgodbmebh` and matches the native host manifest.

## Firefox

Firefox needs a manifest that uses `background.scripts` instead of a service
worker:

1. Copy the extension folder somewhere writable.
2. Replace `manifest.json` with `manifest.firefox.json`.
3. Open `about:debugging#/runtime/this-firefox`.
4. Choose **Load Temporary Add-on** and select `manifest.json`.

Firefox loads temporary add-ons only until the browser closes. For permanent
use the extension must be signed by Mozilla; the native host registration is
already created by the installer.

To verify Firefox registration, open `about:debugging`, load the temporary
extension, and confirm its popup reports **ADM ready**. A temporary Firefox
extension must be loaded again after each browser restart.

## Using it

- **Catch browser downloads** (default on): when the browser starts a download,
  ADM pauses it, asks the desktop app to take over, and only then cancels the
  browser copy. If ADM cannot be reached the browser download resumes, so a
  download is never lost. The setting lives in the desktop app and is synced
  with the extension.
- **Download video button**: on supported video pages (YouTube, Vimeo, Twitch,
  X/Twitter, Facebook, Instagram, TikTok, Reddit, Bilibili, Rumble, Odysee) a
  small **Download** button is attached to the playing video, and on any page
  with a direct MP4/WebM video it is attached to that video. Clicking it opens
  ADM's format picker for the page, or queues the file directly when the video
  is a plain media URL.
- **Media on this page**: audio/video responses are sniffed by the service
  worker and `<video>`/`<audio>` sources by the content script. Use the arrow
  button to send a stream to ADM.
- **File links on this page**: the content script collects links to archives,
  documents, installers and other files. The popup lists them and **Download
  all files** sends them to ADM in one go; the right-click menu has the same
  entry.
- **Right-click** a video for **Download video with ADM** (opens the picker), or
  a link/audio/image for **Download with ADM**.
- **Popup** — the **Download video** button opens the format picker for the
  current tab, **Send page link** queues the current page (useful for direct
  file pages), and **Open ADM** brings the app to the front.

The extension forwards the cookies of the page, so downloads that need a
session work as well.

## Development

Register the native host for your local build:

```powershell
powershell -ExecutionPolicy Bypass -File tools\Register-NativeHost.ps1
```

The script writes manifests to `%LOCALAPPDATA%\ADM\native`, points the native
host at your Debug/Release build, sets `ADM_EXE` for the current user, and
registers Chrome/Edge/Brave/Firefox. Restart the browser afterwards.

Reload the unpacked extension after changing any extension file.
