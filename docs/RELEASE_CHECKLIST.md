# ADM Release Checklist

Use this checklist for each Windows release. The product version comes from
`Directory.Build.props`; `build.ps1` passes it to Inno Setup and checks required
publish files before attempting to package them.

## Build and package

- [ ] Start from a clean checkout and confirm the intended version in `Directory.Build.props`.
- [ ] Install the .NET 8 SDK and Inno Setup 6.
- [ ] Run `powershell -ExecutionPolicy Bypass -File build.ps1`.
- [ ] Confirm the engine harness passes before publishing.
- [ ] Confirm the summary shows the expected version, runtime, self-contained mode, and signing result.
- [ ] Confirm `artifacts\publish\app\ADM.exe`, `app\tools\yt-dlp.exe`, `native\ADM.NativeHost.exe`, and `extension\manifest.json` exist.
- [ ] If video merging or MP3 output is part of the release, confirm `app\tools\ffmpeg.exe` and `app\tools\ffprobe.exe` exist.
- [ ] Confirm the installer name and version match the product version.

For a framework-dependent package, run `build.ps1 -FrameworkDependent`. To
publish without an installer, run `build.ps1 -SkipInstaller`. Those options do
not replace the fresh-install check below.

## Install and browser smoke check

- [ ] Install the generated setup executable as a standard user on a supported Windows 10 or Windows 11 machine.
- [ ] Start ADM, add a small direct HTTP download, pause it, resume it, and open the completed file.
- [ ] Confirm the browser native host registry entries point to manifests under the installed ADM folder.
- [ ] Load the unpacked extension in a clean Chromium profile and confirm the popup reports **ADM ready**.
- [ ] Repeat the popup handoff check in Firefox using `manifest.firefox.json` as a temporary add-on.
- [ ] Confirm failed handoff leaves the browser download available and the popup explains how to reconnect.
- [ ] Uninstall ADM and confirm its per-user native host registrations are removed.

## Release notes and support

- [ ] Record the product version, supported Windows version, self-contained or framework-dependent mode, and notable changes.
- [ ] Record whether the binaries are signed and whether optional video tools are bundled.
- [ ] If requesting diagnostics, ask for `%APPDATA%\ADM\adm.log` and, when present, `adm.log.1`. Logs omit download URL credentials, paths, queries, and fragments.
