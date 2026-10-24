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
### Changed
- UI/UX audit fixes (ISSUE_01-35 except 32): restore asks before replacing accounts; close is guarded while busy; `MessageBoxVisualStyle.Danger` (safe default button); friendly error text; card "⋯" menu + shared `AccountCardContextMenu`; banners/toolbar get their own rows; Settings popup replaces the Options expander; longer, hover-pausable toast with live-region announcements; inline validation with cached duplicate checks; account writes queued off the UI thread; dark implicit styles (CheckBox, MenuItem, ScrollBar, ToolTip, PasswordBox) and new tokens; tray notices; installer window no longer steals focus; reduced-motion support; zoom capped at 175% (`App.xaml`, `MainWindow.xaml(.cs)`, `MainViewModel*.cs`, all dialogs, `AppSettings` TrayHintShown/LastBackupFolder, `AccountModel` labels, `UpdateService`).
  - Public: Clearer, safer and more accessible app: confirmations before replacing accounts, plain-language messages, easier settings and account menus.

### Fixed
- Unreadable `accounts.json` no longer looks like "no accounts": `AccountService.GetAccounts` keeps a `accounts.json.unreadable_<time>` copy (or blocks `SaveAccounts` if no copy can be made), `TakeAccountsLoadProblem` + `MainViewModel.ShowAccountsLoadProblem` explain it and offer Restore.
  - Public: If your saved account list gets damaged, the app now tells you, keeps a copy, and offers to restore a backup instead of starting empty.
- `CaptureCurrentAccount` asks "Replace saved account?" before closing Epic Games Launcher; Cancel returns no error. Capture/exchange failures use plain-language messages (technical detail logged only).
  - Public: Cancelling "replace saved account" no longer closes Epic Games Launcher or shows an error, and save errors are easier to understand.
- Launching the app while it is hidden in the notification area now shows the running window (`App` show-request event `Local\EpicGamesAccountSwitcher.ShowRequest`, `MainWindow.ShowFromOtherInstance`).
  - Public: Opening the app again while it runs in the notification area now brings its window back.
- Add/Edit Account, Choose Avatar and Backup Password dialogs can be dragged, are capped to the screen and scroll (`DialogWindowHelper`); the avatar grid wraps at 4 per row.
  - Public: Dialogs can be moved and always fit on the screen, also on small or high-scaling displays.
- The sign-in window's temporary browser profile (Epic cookies) is erased on close and swept at startup (`EmbeddedLoginWindow.CleanupLeftoverProfiles`).
  - Public: Signing in to add an account no longer leaves sign-in data behind on your PC.
- A failed capture reopens the form with the entered details ("Try Again"); Edit confirms only after the file write succeeds and reverts on failure.
  - Public: If saving an account fails, what you typed is kept, and edits are only reported as saved when they really are.
- ADD SIGNED-IN ACCOUNT without a detected login asks "Sign In Here / Open Epic Games Launcher / Cancel" instead of silently opening the web sign-in.
  - Public: Adding an account when nobody is signed in to Epic now explains your options first.
- A successful switch shows "Switched to X" for a moment before the window minimizes.
  - Public: You now see a confirmation after switching accounts.
- Frame mode gets SETTINGS, "Save signed-in account" and "Update available" buttons; Ctrl+wheel zoom only affects the normal layout.
  - Public: The experimental frame mode now shows settings, update and save-account options.
- Cards show one status badge and one "Last used" line (usage bar and counts moved to the tooltip); relative times refresh every minute and use the Windows regional date/time format; skip-version toast says Settings.
  - Public: Account cards are tidier, and "last used" times stay current and follow your regional format.
- Launcher frame (`LauncherFrameController.cs`): follows the launcher through `SetWinEventHook` (global EVENT_OBJECT_SHOW + launcher-process move/minimize/hide/destroy events) with the 250 ms timer only as fallback; enter delay 1 s -> 300 ms; positions/stacks the frame with one `SetWindowPos` in device pixels and restores the saved pixel rect on exit; dragging/resizing the frame moves/resizes the launcher (`WM_MOVING`/`WM_SIZING`, async `SetWindowPos`); fade + side-card slide transitions (`MainWindow.FadeWindow`/`PlayFrameEnter`), skipped when Windows animations are off.
  - Public: Frame mode now follows Epic Games Launcher instantly, moves together with it as one window, and opens and closes with a smooth fade.

### Internal
- Docs audit: `project_structure.txt` gains runtime prerequisites (WebView2), `.sln`/`README.md` rows, dev.cmd side effects, App named events, Settings popup wording, new data-model paths, section 7 command line; installer status line compressed. `docs/RELEASING.md`: SETTINGS path, dev.cmd modes and side effects.

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
