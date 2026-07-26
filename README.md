<div align="center">
  <img src="Application_Icons/Epic-Games_Account_Switcher.png" width="112" alt="Epic Games Account Switcher logo">
  <h1>Epic Games Account Switcher</h1>
  <p><strong>A fast Windows desktop utility for switching Epic Games Launcher accounts without retyping credentials.</strong></p>

  <p>
    <a href="https://github.com/alonreich/Epic-Games_Account_Switcher/releases/download/v2026.04.01.1310/Epic-Games_Account_Switcher.exe"><img alt="Download v2026.04.01.1310 installer" src="https://img.shields.io/badge/Download-v2026.04.01.1310-2ea043?style=for-the-badge&logo=github"></a>
  </p>

  <p>
    <strong><a href="https://github.com/alonreich/Epic-Games_Account_Switcher/releases/download/v2026.04.01.1310/Epic-Games_Account_Switcher.exe">Direct download: Epic-Games_Account_Switcher.exe</a></strong>
  </p>

  <p>
    <img alt="Windows" src="https://img.shields.io/badge/Windows-Desktop-0078D6?style=flat-square&logo=windows&logoColor=white">
    <img alt=".NET Framework 4.8" src="https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?style=flat-square&logo=dotnet&logoColor=white">
    <img alt="WPF" src="https://img.shields.io/badge/UI-WPF%20%2B%20XAML-5C2D91?style=flat-square">
    <img alt="Runtime" src="https://img.shields.io/badge/Runtime-win--x64-24292f?style=flat-square">
  </p>
</div>

---

## What It Does

Epic Games Account Switcher keeps multiple Epic Games Launcher account sessions ready and lets you move between them from one clean desktop dashboard.

It is built for one job: make account switching feel instant, predictable, and low-friction while keeping the actual session orchestration local to your Windows machine.

## Highlights

- **One-click account switching** from a visual WPF dashboard.
- **Usage-aware account cards** with last-played labels, switch counts, and visual activity heat.
- **Smart capture suggestions** when the app detects a current launcher login that is not saved yet.
- **Password-encrypted `.egbackup` exports** with legacy ZIP restore support.
- **Isolated dev mode** through `dev.cmd`, so testing does not overwrite the installed app or installed app data.
- **Single-file installer output** through `Build.cmd` and `SingleFilePacker`.
- **Local-first storage model** using the Windows filesystem, registry handling, and Credential Manager integration.
- **Custom themed dialogs** for consistent app messages instead of default system popups.

## Download

Use the current public installer:

**[Download v2026.04.01.1310](https://github.com/alonreich/Epic-Games_Account_Switcher/releases/download/v2026.04.01.1310/Epic-Games_Account_Switcher.exe)**

The release installer is expected to be named:

```text
Epic-Games_Account_Switcher.exe
```

Release page:

```text
https://github.com/alonreich/Epic-Games_Account_Switcher/releases/tag/v2026.04.01.1310
```

SHA-256:

```text
15ABFE32BBD3879CB98A41EF192A972E5D075B73BA327BE17725E6721EB7910A
```

## Requirements

- Windows x64
- Epic Games Launcher installed
- Administrator approval when the app needs to install, upgrade, uninstall, or touch protected launcher state
- .NET SDK only if you want to build from source

## Quick Start

1. Download `Epic-Games_Account_Switcher.exe` from the release link above.
2. Run the installer.
3. Open the app.
4. Capture your currently signed-in Epic Games account.
5. Switch accounts from the dashboard whenever needed.

## Usage Tracker

The dashboard is designed to help you choose the right account without thinking:

- **Last played** shows when an account was last used.
- **Switch count** shows how often that account has been used.
- **Activity heat** gives frequently used accounts stronger visual weight.
- **Unsaved login suggestions** help capture the account that is already active in the launcher.

## Developer Workflow

For safe local testing without touching the installed app:

```bat
dev.cmd
```

Useful variants:

```bat
dev.cmd run
dev.cmd build
dev.cmd restore
dev.cmd clean
```

`dev.cmd` uses isolated temporary data under `%TMP%\Epic_Games_Account_Switcher_DEV`, including separate dev logs and dev user data.

Run automated safety tests:

```bat
dotnet test Epic-Games_Account_Switcher-Tests\Epic-Games_Account_Switcher-Tests.csproj
```

## Build From Source

To produce the packaged installer:

```bat
Build.cmd
```

The final installer is written to:

```text
compiled\Epic-Games_Account_Switcher.exe
```

## Project Map

```text
Epic-Games_Account_Switcher-Client\    WPF app, windows, view models, app lifecycle
Epic-Games_Account_Switcher-Globals\   Account model, session orchestration, platform helpers
SingleFilePacker\                      Build-time packer for the final executable
Skin_Icons\                            Built-in account skin images
Application_Icons\                     App icon assets
compiled\                              Release output folder
```

## Safety Notes

- Do not log passwords, tokens, API keys, or private secrets.
- Treat full local paths and full process commands as sensitive diagnostic data.
- Close or let the app manage Epic Games Launcher before switching accounts.
- Keep backups of important account/session data when testing changes.

## License

No license file is currently included in this repository.
