# What's New in Epic Games Account Switcher

Release notes for every public version, newest first. Older versions are never removed from this page.
Download the latest version from the [Releases page](https://github.com/alonreich/Epic-Games_Account_Switcher/releases/latest).

<!-- releases:start -->

## v3.0.0 - October 16, 2026

### Security
- Setup is hardened so other programs on your PC can no longer interfere with it while it runs with administrator rights.
- The app and installer are now digitally signed, and updates are only accepted when the signature matches.
- Each download now comes with a checksum so you can confirm the file is genuine.

### New
- The app now tells you when a new version is out and can update itself in one click, only when you choose to.
- Try the new experimental frame mode that wraps the switcher around the Epic Games Launcher with your accounts on both sides.
- Setup now waits for an account switch in progress to finish before closing the app.
- The app now shows its version number in the title bar.

### Improved
- Version numbers now follow the standard format, starting with 3.0.0.
- Updates are now all-or-nothing: if anything goes wrong, your previous version keeps working.
- Setup now warns you before replacing a newer version with an older one.
- Setup now double-checks its safety backup and will never delete your accounts if the backup failed without asking you first.
- Uninstalling no longer deletes your saved accounts unless you ask for it, and a backup is made first.
- Installing from a standard Windows account now keeps your accounts in your own profile.

### Fixed
- Fixed setup sometimes refusing to run with a "System Busy" message.

## v2026.08.05.1 - August 5, 2026

### New
- Sign in directly through a secure built-in login window to add accounts without opening the Epic Games Launcher.

### Improved
- Setup now shows clear, step-by-step progress while it installs.

### Removed
- The game settings comparison was removed because it was not reliable.

## v2026.08.04.1 - August 4, 2026

### New
- A notification now tells you when accounts share the same game settings.

### Fixed
- Fixed a crash when clicking dialog buttons several times quickly.

### Improved
- Clearer, easier-to-understand buttons and messages.

## v2026.08.03.1 - August 3, 2026

### New
- Setup automatically backs up your saved accounts when it detects an existing installation.

## v2026.08.03 - August 3, 2026

### New
- Compare Fortnite game settings between accounts.

## v2026.04.01.1310 - July 26, 2026

### New
- Encrypted backups of all your accounts, protected by a password or by your Windows account.
- Usage tracking: see when each account was last played and how often you switch to it.

### Improved
- Polished design with helpful empty screens, zoom controls and an optional system tray icon.
- The app log moved to a shared location so it is easier to find when asking for help.
