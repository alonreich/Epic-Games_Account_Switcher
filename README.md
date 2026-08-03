<div align="center">
  <img src="Application_Icons/Epic-Games_Account_Switcher.png" width="128" alt="Epic Games Account Switcher logo">
  <h1>Epic Games Account Switcher</h1>
  <p><strong>A lightning-fast, zero-friction desktop utility for atomic Epic Games account session switching.</strong></p>

  <p>
    <a href="https://github.com/alonreich/Epic-Games_Account_Switcher/releases/download/v2026.08.04.1/Epic-Games_Account_Switcher.exe"><img alt="Download Latest" src="https://img.shields.io/badge/Download_v2026.08.04.1-2ea043?style=for-the-badge&logo=github"></a>
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
- **Smart Game Settings Comparison**: *[NEW]* Automatically analyzes binary Fortnite `.sav` configurations across profiles on boot, actively alerting you to sensitivity and volume differences.
- **Intelligent Usage Tracking**: Analyzes and visualizes your account usage patterns with dynamic activity heat, switch counts, and last-played timestamps.
- **Zero-Friction Capture**: Seamlessly intercepts active unrecorded Epic sessions in real-time, suggesting 1-tap profile creation.
- **Disaster Recovery**: Employs streaming-cipher encrypted `.egbackup` exports to safely port your isolated sessions across hardware.
- **True System Integration**: Native Windows tray integration, custom-themed UI dialogs, and a flawless single-file executable architecture.

## 📥 Download & Install

**[Download v2026.08.04.1 (Latest)](https://github.com/alonreich/Epic-Games_Account_Switcher/releases/download/v2026.08.04.1/Epic-Games_Account_Switcher.exe)**

**Installation Requirements:**
- Windows 10/11 (x64)
- Epic Games Launcher
- Administrative privileges for profile injection

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

**Build Production Executable:**
```bat
Build.cmd
```
*Output will drop to `compiled\Epic-Games_Account_Switcher.exe`*

## 📁 Architecture

| Directory | Purpose |
| :--- | :--- |
| `Epic-Games_Account_Switcher-Client\` | The WPF presentation and view-model orchestration layer. |
| `Epic-Games_Account_Switcher-Globals\`| Core algorithmic engine handling atomic path swaps and binary serialization. |
| `SingleFilePacker\` | Proprietary build-time bundling pipeline for standalone executable generation. |

## 🛡️ Security Posture
- Total offline isolation: No tokens, passwords, or telemetry ever leave your machine.
- Local profiles are stored strictly within `%LOCALAPPDATA%` and `%PROGRAMDATA%`.
- Do not expose log paths containing localized UUIDs in public forums.
