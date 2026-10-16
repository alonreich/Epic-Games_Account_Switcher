<div align="center">
  <img src="Application_Icons/Epic-Games_Account_Switcher.png" width="128" alt="Epic Games Account Switcher logo">
  <h1>Epic Games Account Switcher</h1>
  <p><strong>A lightning-fast, zero-friction desktop utility for atomic Epic Games account session switching.</strong></p>

  <p>
    <a href="https://github.com/alonreich/Epic-Games_Account_Switcher/releases/latest/download/Epic-Games_Account_Switcher.exe"><img alt="Download Latest" src="https://img.shields.io/github/v/release/alonreich/Epic-Games_Account_Switcher?label=Download&style=for-the-badge&logo=github&color=2ea043"></a>
  </p>
  
  <p>
    <img alt="Windows" src="https://img.shields.io/badge/Windows-Desktop-0078D6?style=flat-square&logo=windows&logoColor=white">
    <img alt=".NET Framework 4.8" src="https://img.shields.io/badge/.NET_Framework-4.8-512BD4?style=flat-square&logo=dotnet&logoColor=white">
    <img alt="WPF" src="https://img.shields.io/badge/UI-WPF_+_XAML-5C2D91?style=flat-square">
    <img alt="Runtime" src="https://img.shields.io/badge/Runtime-win--x64-24292f?style=flat-square">
  </p>
</div>

---

## ⚡ What It Does

Epic Games Account Switcher is a high-performance orchestration tool designed for power users who manage multiple Epic Games accounts. It bypasses the tedious sign-in screens by executing surgical, atomic swaps of your local authentication tokens, caching state, and registry identifiers. 

Switch accounts instantly from a stunning, responsive dashboard without ever re-typing your credentials.

## 🔥 Key Features

- **Instant Atomic Switching**: One-click profile hopping via a highly polished WPF dashboard.
- **Direct Fast Login**: *[NEW]* Rapidly add new accounts by signing in directly through our secure embedded UI without opening the Epic Launcher.
- **Intelligent Usage Tracking**: Analyzes and visualizes your account usage patterns with dynamic activity heat, switch counts, and last-played timestamps.
- **Zero-Friction Capture**: Seamlessly intercepts active unrecorded Epic sessions in real-time, suggesting 1-tap profile creation.
- **Disaster Recovery**: Employs streaming-cipher encrypted `.egbackup` exports to safely port your isolated sessions across hardware.
- **True System Integration**: Native Windows tray integration, custom-themed UI dialogs, and a flawless single-file executable architecture.

## 📥 Download & Install

**[Download the latest version](https://github.com/alonreich/Epic-Games_Account_Switcher/releases/latest/download/Epic-Games_Account_Switcher.exe)** · [All releases & release notes](https://github.com/alonreich/Epic-Games_Account_Switcher/releases) · [What's new](docs/CHANGELOG.md)

**Installation Requirements:**
- Windows 10/11 (x64)
- Epic Games Launcher
- Administrator permission during setup (Windows asks once; the app itself runs normally)

**Verify your download (optional):** every release includes `Epic-Games_Account_Switcher.exe.sha256`. In PowerShell run `Get-FileHash .\Epic-Games_Account_Switcher.exe` and compare the two values.
The files are signed with the project's free self-signed certificate (`EpicGamesAccountSwitcher-CodeSigning.cer`, attached to each release). Windows may still show "Unknown publisher" or a SmartScreen notice, because only paid certificates remove those.

## 🔄 Updates

The app checks for a new version at most once a day and shows a small banner with **Update now / Later / Skip version / What's new**. Nothing is downloaded or installed until you click, every download is verified before it runs, and your saved accounts are always kept. You can also check manually in **OPTIONS → Check for updates now**, or turn automatic checks off there.

## 🗑️ Uninstall

Use Windows *Settings → Apps*. By default your saved accounts are **kept**, so reinstalling brings them back. If you choose to delete them, a backup is saved to your Downloads folder first.

## 🚀 Quick Start Guide

1. Download the `Epic-Games_Account_Switcher.exe` from the link above.
2. Launch the application. 
3. Log into the Epic Games Launcher manually one final time.
4. Return to the Switcher to instantly capture the active session.
5. You are now free to swap seamlessly from the dashboard.

## 🛠 Developer & Compilation

Built for maximum reliability and safety, developers can run sandboxed instances that do not interfere with live Epic Games directories.

**Run Sandboxed Environment:**
```bat
dev.cmd run
```

**Build Production Installer** (tests + signing + checksum):
```bat
Build.cmd
```
*Output: `compiled\Epic-Games_Account_Switcher.exe` and `.sha256`. Local quick build: `dev_build.cmd`.*

**Publish a Release** (maintainers, needs GitHub CLI):
```bat
Release.cmd 3.0.1
```
*See [docs/RELEASING.md](docs/RELEASING.md). Every change must be logged in [docs/changes.md](docs/changes.md). Start with [project_structure.txt](project_structure.txt).*

## 📁 Architecture

| Directory | Purpose |
| :--- | :--- |
| `Epic-Games_Account_Switcher-Client\` | The WPF presentation and view-model orchestration layer. |
| `Epic-Games_Account_Switcher-Globals\`| Core algorithmic engine handling atomic path swaps and binary serialization. |
| `SingleFilePacker\` | Single-file installer bootstrapper (payload integrity check, safe extraction). |
| `ssl-certificate\` | Free self-signed code-signing scripts and public certificate. |
| `docs\` | Release process, internal change log and public changelog. |

## 🛡️ Security Posture
- No tokens, passwords, or telemetry ever leave your machine. The only request the app makes on its own is a once-a-day version check to GitHub, which you can turn off in OPTIONS.
- Releases are signed and come with SHA-256 checksums; the in-app updater refuses any file that fails verification.
- Local profiles are stored strictly within `%LOCALAPPDATA%` and `%PROGRAMDATA%`.
- Do not expose log paths containing localized UUIDs in public forums.
