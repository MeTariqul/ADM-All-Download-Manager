# ADM capability matrix

ADM 1.2.0 implements the download-manager features used in its supported
Windows workflow.

The published workspace image is [`screenshot.png`](screenshot.png). It shows
the final ADM light theme and logo used by this release.

| Capability | ADM 1.2.0 |
| --- | --- |
| Segmented HTTP/HTTPS downloads | Implemented: parallel ranges, dynamic splitting, resume and fallback for servers without ranges. |
| FTP and FTPS | Implemented: built-in client and `REST` resume. |
| Browser capture | Implemented for Chrome, Edge, Brave, Chromium and Firefox through the packaged extension and native host. |
| Download selected or multiple links | Implemented: extension page-link collection, multi-link paste and drag-and-drop. |
| Video, audio and playlists | Implemented through bundled yt-dlp, with format selection, MP3, subtitles and playlist selection. |
| Queue and scheduler | Implemented: priority, concurrency, start/stop schedule, retry backoff, pause/resume and exit-on-completion. |
| Download organization | Implemented: automatic file categories, search, context actions and saved list state. |
| Transfer controls | Implemented: global/per-download speed limits, connection count, proxy, HTTP authentication, cookie header, checksum and disk-space checks. |
| Notifications and tray | Implemented: completion notifications, resident tray controls and single-instance URL forwarding. |
| Site grabber / offline mirroring | Planned. ADM can collect direct links from the current page; recursive site crawling and mirroring need a separate, bounded crawler before release. |
| Scheduled site synchronization | Planned with the site-grabber project model. |
| Antivirus command on completion | Planned. This requires a configurable local scanner command and safe argument escaping. |
| Hourly download quota | Planned. ADM currently provides a live global speed limit. |
| Additional display languages | Planned. The current application UI is English. |

The planned items are intentionally not advertised as implemented. They require
their own user-facing design and verification before they can be included in a
release.
