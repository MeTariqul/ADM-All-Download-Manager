# ADM Update Plan

**Prepared:** 2026-10-24  
**Product baseline:** ADM 1.3.1 (.NET 8, WPF)

This is the current delivery plan for ADM. The older
[`idm_clone_master_architecture_development_plan.md`](../idm_clone_master_architecture_development_plan.md)
is an initial design sketch and predates much of the current implementation.

## Update work in this cycle

### Release confidence

- [x] Define the product version once and apply it to all .NET projects and the installer.
- [x] Check required publish artifacts and report optional video-tool availability.
- [x] Summarize runtime mode, version, and signing outcome after publishing.
- [x] Add a repeatable build, install, browser, and uninstall checklist in
  [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md).
- [x] Apply the final ADM logo and its silver, navy and cyan light theme across
  the desktop app, installer and browser extension.
- [x] Require a matching strong ETag or saved Last-Modified value before restoring a ranged partial download.
- [x] Reject partial responses whose Content-Range or declared length does not match the requested segment.
- [x] Add a local HTTP-server regression case for a misreported range.
- [x] Verify Google Drive filename and extension detection via the UC download endpoint.
- [x] Verify the licence online once per day and block the app until the server confirms it.
- [x] Add a **Remove licence** button so a previous key can be unregistered before entering a new one.
- [x] Add an hourly silent app-update check with one-click auto download and install from settings.
- [ ] Run the checklist on clean Windows 10 and Windows 11 systems before a public release.

### Download recovery and data integrity

- [x] Require a matching strong ETag or saved Last-Modified value before restoring a ranged partial download.
- [x] Reject partial responses whose Content-Range or declared length does not match the requested segment.
- [x] Add a local HTTP-server regression case for a misreported range.
- [ ] Expand restart/recovery coverage for changed validators, zero-byte files, and stale partial files.
- [ ] Validate the full HTTP and FTP recovery matrix on supported Windows systems.

### Browser integration

- [x] Document recovery steps for an unregistered/unreachable native host and clarify Firefox's temporary add-on lifecycle.
- [x] Keep the extension popup's distinct **ADM not running** and **Extension not connected** states documented.
- [ ] Verify install, update, and removal using clean Chrome, Edge, Brave, Chromium, and Firefox profiles.

### Download workflow

- [x] Use status colors for row state and expose the full error as a tooltip/help text when the row is truncated.
- [ ] Check keyboard navigation, high-DPI scaling, light-theme contrast, and narrow layouts on Windows.

### Diagnostics and maintenance

- [x] Log only the URL origin for download and clipboard events; omit credentials, path, query, and fragment.
- [x] Rotate `adm.log` at 2 MiB and retain one previous copy.
- [x] Document safe log collection and retention.
- [ ] Audit all future diagnostic messages before adding new request or authentication data.

## Release gate

Before publishing, build from a clean checkout, run the existing engine harness,
verify the required publish files and product version, then complete the fresh
install, browser handoff, and uninstall checklist. Record any unavailable browser
or packaging tools in the release notes.
