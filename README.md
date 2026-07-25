<div align="center">
  <img src="Application_Icons/Epic-Games_Account_Switcher.png" width="120" alt="Epic Games Account Switcher Logo"/>
  <h1>🎮 Epic Games Account Switcher</h1>
  <p><strong>A high-performance, atomic session manager for the Epic Games Launcher</strong></p>
  
  [![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)](#)
  [![UI](https://img.shields.io/badge/UI-WPF%20%7C%20XAML-5C2D91?style=for-the-badge&logo=dotnet&logoColor=white)](#)
  [![Status](https://img.shields.io/badge/Status-Perfect%20&%20Stable-2EA043?style=for-the-badge)](#)
  [![Build](https://img.shields.io/badge/Build-Compiled%20Installer-brightgreen?style=for-the-badge)](#)
</div>

<br/>

## ⚡ Zero-UI, Instant Switching
**Epic Games Account Switcher** redefines how you switch accounts. It achieves true **"Zero-UI" authentication** by performing surgical injections of session tokens, web cache state, and registry identifiers. Bypassing all login and 2FA prompts entirely. 

Simply select your account and play. No credentials screen. No pop-ups. Just pure, immediate access. 

---

## ✨ Key Features
- 🚀 **Atomic Directory Swapping:** Utilizes robust `Directory.Move` on prepared temporary structures to guarantee zero session corruption during restoration. 
- 🔒 **Zero-UI Mandate:** Direct OAuth session restoration and persistent verification logic ensure 100% success rate without ever displaying a credential/login prompt.
- 🎨 **Modern WPF Dashboard:** A fully responsive, modern grid interface. Beautiful account cards, smart reordering, and custom skin icons.
- 🛡️ **Session Verification Engine:** Pre-validates OAuth expiration inside `PersistentAuthStorage.json` before attempting a swap. 
- ⚡ **Automated Build Pipeline:** Comes with an integrated, no-halt build script (`Build.cmd`) designed for CI/CD environments. Includes single-file packing.
- 🛠️ **Deep Integration:** Built on consolidated Win32 API hooks, registry encapsulations, and Windows Vault integrations.

---

## 🛠 Architecture & Core Principles
The engine is split into highly decoupled logical layers:

- `Epic-Games_Account_Switcher-Client/`: **The WPF UI Layer.** Orchestrates user interaction, app lifecycle, custom message boxes, and profile metadata configuration.
- `Epic-Games_Account_Switcher-Globals/`: **The Core Logic Layer.** Houses the critical `AccountService.cs` engine. Safely clones AppData roaming states, Epic Online Services data, and login caches. Handles registry P/Invoke tasks.
- `SingleFilePacker/` & `Installer/`: Bundles the `Payload` directory to generate the final single executable distribution.

> **Note on Atomicity:** We enforce a strict deterministic lifecycle. Epic processes are identified by installation path and terminated entirely before any atomic file I/O operations occur. 

---

## 🚀 Getting Started

### Ready-to-Use Installer
The application is pre-compiled and fully stabilized. Simply grab the latest installer from the `compiled` directory!

📂 [Download the Installer (`Epic-Games_Account_Switcher.exe`)](./compiled/Epic-Games_Account_Switcher.exe)

### Compilation from Source
If you wish to compile the source code yourself:
1. Open up a PowerShell or CMD terminal.
2. Run `Build.cmd` from the root directory.
3. The build pipeline handles cleaning, resource packing, publishing, and compiling the `.exe`. 

---

<div align="center">
  <sub>Built with passion for seamless gaming. No more typing passwords.</sub>
</div>
