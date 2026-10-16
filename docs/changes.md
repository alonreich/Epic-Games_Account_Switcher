# Internal Change Log

<!--
AGENT / DEVELOPER RULES (mandatory, see project_structure.txt section 6)
1. EVERY change to code, build scripts or behaviour gets an entry under "## [Unreleased]" IN THE SAME COMMIT.
2. Put it under one of: ### Security, ### Added, ### Changed, ### Fixed, ### Removed, ### Internal
3. Line 1 ("- ...") is technical: what changed and where (files, classes). Be precise.
4. Line 2 ("  - Public: ...") is ONE short plain-English sentence a non-technical user understands:
   no file names, no class/method names, no jargon (registry, mutex, exception, DPAPI, ...), max 160 chars.
   Use "  - Public: -" for changes users never notice. ### Internal entries need no Public line.
5. Never edit released sections ("## [x.y.z] - date"). Release.cmd moves [Unreleased] into a version section,
   and developer_tools\Release-Notes.ps1 publishes the Public lines to docs\CHANGELOG.md and the GitHub release.
-->

## [Unreleased]

## [3.0.0] - 2026-10-16
### Security
- Installer no longer runs elevated code from the user-writable %TEMP%: `SingleFilePacker` is now `asInvoker`, verifies a SHA-256 of its embedded payload, extracts to `%ProgramFiles%\Epic-Games_Account_Switcher_Setup_*` when elevated, and removes it itself after the child exits. All `.bat` files in %TEMP% were replaced by an inline, tamper-proof `cmd /c` command restricted to our own folders (`App.xaml.cs`, `SingleFilePacker/Program.cs`, `app.manifest`).
  - Public: Setup is hardened so other programs on your PC can no longer interfere with it while it runs with administrator rights.
- Code signing with a free self-signed certificate (`ssl-certificate/`); builds pin the certificate thumbprint (`Directory.Build.props` -> `AssemblyMetadata("SigningThumbprint")`) and the updater checks `WinVerifyTrust` + signer thumbprint (`UpdateService.VerifySignature`).
  - Public: The app and installer are now digitally signed, and updates are only accepted when the signature matches.
- Every release publishes `Epic-Games_Account_Switcher.exe.sha256`; `Build.cmd` generates it.
  - Public: Each download now comes with a checksum so you can confirm the file is genuine.

### Added
- In-app updater (`Globals/UpdateService.cs`, `MainViewModel.Updates.cs`, banner in `MainWindow.xaml`): GitHub Releases check at startup (max once per 24h, 10s timeout, silent offline) plus OPTIONS -> "Check for updates now"; banner with Update now / Later / Skip version / What's new; download only on click, resumable reuse of verified downloads, free-space check, SHA-256 + signature verification, install only after explicit confirmation using `--update` mode.
  - Public: The app now tells you when a new version is out and can update itself in one click, only when you choose to.
- Experimental "launcher frame" (`LauncherFrameController.cs`, `MainViewModel.LauncherFrame.cs`): opt-in OPTIONS checkbox; the window wraps around the Epic Games Launcher with compact cards on both sides, restores the normal layout when the launcher closes.
  - Public: Try the new experimental frame mode that wraps the switcher around the Epic Games Launcher with your accounts on both sides.
- Graceful shutdown handshake: named event `Local\EpicGamesAccountSwitcher.ShutdownRequest`; the app finishes any running switch (`SessionOperationLock`) before exiting (`App.StartShutdownListener`, `RequestRunningAppsToExit`).
  - Public: Setup now waits for an account switch in progress to finish before closing the app.
- Version shown in the title bar (`MainViewModel.AppVersionLabel`).
  - Public: The app now shows its version number in the title bar.
- `AccountService.VerifyBackupFile` proves a backup decrypts and contains `accounts.json`.
  - Public: -

### Changed
- Versioning moved to SemVer 3.0.0 with a single source of truth (`Directory.Build.props` VersionPrefix -> assembly/file/product version of every project); `Globals.Version` now reads `AppVersion.Current`. Legacy calendar versions compare as older (`Globals/AppVersion.cs`).
  - Public: Version numbers now follow the standard format, starting with 3.0.0.
- Installer rewritten as two phases (`App.xaml.cs`): user phase (decisions, verified backup, user data, per-user cleanup) and elevated machine phase (staged copy to `*.new_*`, swap via `Directory.Move`, rollback on failure, uninstall registry entry, all-users shortcuts). Free-space check, payload size verification, `.pdb`/`payload.zip` no longer installed.
  - Public: Updates are now all-or-nothing: if anything goes wrong, your previous version keeps working.
- Downgrade guard: setup compares installed vs. setup version and requires explicit confirmation to install an older version (`RunUserPhase`, `RunMachinePhase`).
  - Public: Setup now warns you before replacing a newer version with an older one.
- Setup only says a backup exists after verifying it; Clean Install with a failed backup requires an explicit second choice. Automatic backups in Downloads are rotated (newest 3 kept) and only made when accounts exist.
  - Public: Setup now double-checks its safety backup and will never delete your accounts if the backup failed without asking you first.
- Uninstall keeps saved accounts by default ("Remove App, Keep My Accounts"); deleting accounts needs a second confirmation and makes a verified backup first. `UninstallString` no longer contains `--confirmed`/`--user-data`.
  - Public: Uninstalling no longer deletes your saved accounts unless you ask for it, and a backup is made first.
- Standard-user installs: user data operations run in the signed-in user's context; elevation only for Program Files/HKLM (`--stage2`, `--bootstrapper`).
  - Public: Installing from a standard Windows account now keeps your accounts in your own profile.
- `Build.cmd`: runs tests, strips debug symbols, signs, embeds payload hash, fails on missing payload, writes SHA-256; `dev_build.cmd` now wraps `Build.cmd --dev`; new `Release.cmd` (clean-tree check, version bump, notes promotion, annotated tag, `gh release create`).
  - Public: -
- README download link now always points to the latest release.
  - Public: -

### Fixed
- `IsMsiBusy` no longer treats the mere existence of `UpdateExeVolatile` as "busy" (checks `Flags`).
  - Public: Fixed setup sometimes refusing to run with a "System Busy" message.
- `Globals.InstalledToProgramFiles` only matches the real install folder (not setup staging folders).
  - Public: -

### Internal
- `.gitattributes`: CRLF enforced for `.cmd/.bat/.ps1`. `.gitignore`: private keys and payload artifacts. Docs: `docs/RELEASING.md`, `docs/CHANGELOG.md`, `docs/index.md`, updated `project_structure.txt`.
- Tests for `AppVersion` comparison (`Epic-Games_Account_Switcher-Tests/AppVersionTests.cs`).
