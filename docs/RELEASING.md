# Build, Version, Release & Update Workflow

## 1. Version (single source of truth)
- `Directory.Build.props` -> `<VersionPrefix>MAJOR.MINOR.PATCH</VersionPrefix>` (SemVer). Every project inherits it; the app reads it at runtime (`Globals.Version` = `AppVersion.Current`).
- Never hard-code a version anywhere else. Bump only through `Release.cmd <version>` (or `developer_tools\Set-Version.ps1`).
- MAJOR = breaking change or data-format change, MINOR = new feature, PATCH = fixes only.
- Legacy calendar versions (`2026.x.y.z`) always compare as older than 3.0.0.

## 2. Day-to-day
| Command | What it does |
| :--- | :--- |
| `dev.cmd` (watch) / `dev.cmd run` (single) | Debug launch with isolated data in `%TMP%\Epic_Games_Account_Switcher_DEV` (never touches real profiles). Every launch wipes that dev data + logs, force-stops dev instances from this repo and deletes Client/Globals `bin`+`obj`. Also `dev.cmd build`, `restore`, `clean`. |
| `dev_build.cmd` | Local installer build (`Build.cmd --dev`): no tests, no Git. |
| `Build.cmd` | Full release build: clean -> tests -> publish (no .pdb) -> sign -> pack (+payload SHA-256) -> installer -> sign -> `compiled\*.exe.sha256`. |
| `Release.cmd 3.0.1` | Everything below, end to end. |

Every change must add an entry to `docs\changes.md` -> `## [Unreleased]` (rules at the top of that file).

## 3. Release (Release.cmd)
1. Refuses a dirty tree, any branch other than `main`, or being behind `origin/main`.
2. Bumps `VersionPrefix` if a version argument is given (must be higher).
3. Validates `docs\changes.md` [Unreleased]: entries exist, every user-facing entry has a jargon-free `Public:` line.
4. Runs `Build.cmd` (tests + signing are mandatory once `ssl-certificate\thumbprint.txt` exists).
5. `developer_tools\Release-Notes.ps1 -Promote`:
   - `changes.md`: `[Unreleased]` -> `[x.y.z] - date`, fresh empty `[Unreleased]`.
   - `CHANGELOG.md`: prepends the consumer notes. Never removes older versions (aborts if the count would not grow by exactly one).
   - Writes `compiled\release-notes.md` for GitHub.
6. Commits `chore(release): vX.Y.Z`, creates an **annotated** tag, pushes both.
7. `gh release create` uploads `Epic-Games_Account_Switcher.exe`, `.exe.sha256` and the public `.cer`.

Asset names are a contract with the in-app updater (`UpdateService.InstallerAssetName` / `ChecksumAssetName`). Do not rename them.

## 4. How users receive updates
- The app checks `releases/latest` at most once a day at startup (10 s timeout; silent when offline) plus SETTINGS -> *Check for updates now*.
- The banner offers **Update now / Later (24 h) / Skip version / What's new**. Nothing downloads until *Update now*.
- The download is verified with SHA-256, plus the Authenticode signature pinned to `ssl-certificate\thumbprint.txt`. A verified download is reused instead of downloaded again.
- Installation starts only after the user confirms *Install & Restart Now*. The installer runs with `--update`: no questions, accounts kept, app reopens.

## 5. Installer guarantees (App.xaml.cs)
- **User phase** (not elevated): downgrade guard -> Upgrade/Clean choice -> graceful app shutdown (any account switch in progress finishes first) -> verified safety backup (rotated, newest 3) -> elevation.
- **Machine phase** (elevated, extracted to an admin-only folder in Program Files): free-space check -> staged copy and size verification -> `Directory.Move` swap -> uninstall entry -> rollback on any failure.
- User data is touched only after the machine phase succeeds. Clean Install with a failed backup requires a second explicit choice.
- **Uninstall** keeps accounts by default; deleting them requires confirmation and makes a verified backup first.

## 6. If a release goes wrong
- Build or tests fail: nothing is committed or published; `Release.cmd` reverts the version/notes edits.
- Push or `gh` fails after tagging: the script prints the exact command to finish.
- Bad release already published: publish a higher PATCH version with the fix. Never delete or re-use a published tag, because users' updaters compare versions.
