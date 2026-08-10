# Cursivis Windows installer

`Cursivis.iss` packages the self-contained WinUI app, self-contained native
browser host, and unpacked Chromium extension. It installs per user, creates a
Start Menu entry, offers an optional desktop shortcut, registers background
startup and the Chrome/Edge native host, and launches Settings onboarding.

Build from the repository root:

```powershell
.\scripts\build-release.ps1 -Version 0.1.0-beta.1
```

The build writes `artifacts/release/Cursivis-Setup-x64.exe`, a versioned
installer, and its SHA-256 checksum. GitHub Releases is the only distribution
host for these application binaries.
The browser extension remains an explicit user-installed extension; the setup
program does not bypass Chrome or Edge extension-install protections.
