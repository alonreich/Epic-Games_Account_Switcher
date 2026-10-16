@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
REM ==========================================================================================
REM  Release.cmd 3.0.1   Bump version to 3.0.1, build (tests + signing + checksum), promote release notes,
REM                      commit, create annotated tag v3.0.1, push, and publish the GitHub release.
REM  Release.cmd         Release the version currently in Directory.Build.props.
REM  Requirements: clean Git tree on main, GitHub CLI (gh) logged in, docs\changes.md has [Unreleased] entries.
REM  Full procedure: docs\RELEASING.md
REM ==========================================================================================
set "NEW_VERSION=%~1"
set "NO_PAUSE=1"
set "OUT_EXE=compiled\Epic-Games_Account_Switcher.exe"

where git >nul 2>&1 || (echo [ERROR] git not found. & exit /b 1)
where gh >nul 2>&1 || (echo [ERROR] GitHub CLI 'gh' not found. Install from https://cli.github.com and run: gh auth login & exit /b 1)
gh auth status >nul 2>&1 || (echo [ERROR] GitHub CLI is not logged in. Run: gh auth login & exit /b 1)

set "DIRTY="
for /f "delims=" %%s in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY (
    echo [ERROR] Working tree is not clean. Commit or stash your changes first.
    git status --short
    exit /b 1
)
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BRANCH=%%b"
if /i not "!BRANCH!"=="main" (
    echo [ERROR] Releases are only made from 'main' ^(current: !BRANCH!^).
    exit /b 1
)
git fetch origin main --tags --quiet || (echo [ERROR] git fetch failed. & exit /b 1)
for /f %%c in ('git rev-list --count HEAD..origin/main') do set "BEHIND=%%c"
if not "!BEHIND!"=="0" (
    echo [ERROR] Local main is behind origin/main by !BEHIND! commit^(s^). Pull first.
    exit /b 1
)

if not "!NEW_VERSION!"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "developer_tools\Set-Version.ps1" -Version "!NEW_VERSION!"
    if errorlevel 1 exit /b 1
)
set "APP_VERSION="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content -Raw 'Directory.Build.props')).Project.PropertyGroup[0].VersionPrefix"`) do set "APP_VERSION=%%v"
if "!APP_VERSION!"=="" (echo [ERROR] Could not read version. & goto :rollback)
set "TAG=v!APP_VERSION!"
echo [INFO] Releasing !TAG!

git rev-parse -q --verify "refs/tags/!TAG!" >nul 2>&1 && (echo [ERROR] Tag !TAG! already exists. Bump the version. & goto :rollback)
gh release view "!TAG!" >nul 2>&1 && (echo [ERROR] GitHub release !TAG! already exists. & goto :rollback)

powershell -NoProfile -ExecutionPolicy Bypass -File "developer_tools\Release-Notes.ps1" -Version "!APP_VERSION!" -Check
if errorlevel 1 goto :rollback

call "%~dp0Build.cmd"
if errorlevel 1 (echo [ERROR] Build failed; nothing was published. & goto :rollback)
if not exist "%OUT_EXE%.sha256" (echo [ERROR] Checksum file missing. & goto :rollback)

powershell -NoProfile -ExecutionPolicy Bypass -File "developer_tools\Release-Notes.ps1" -Version "!APP_VERSION!" -Promote -NotesOut "compiled\release-notes.md"
if errorlevel 1 goto :rollback

git add Directory.Build.props docs\changes.md docs\CHANGELOG.md || goto :rollback
git commit -m "chore(release): !TAG!" || goto :rollback
git tag -a "!TAG!" -m "Epic Games Account Switcher !TAG!" || goto :rollback
git push origin main || (echo [ERROR] Push failed. The release commit and tag exist locally only. Fix and run: git push origin main ^&^& git push origin !TAG! & exit /b 1)
git push origin "!TAG!" || (echo [ERROR] Tag push failed. Run: git push origin !TAG! & exit /b 1)

set "ASSETS="%OUT_EXE%" "%OUT_EXE%.sha256""
if exist "ssl-certificate\EpicGamesAccountSwitcher-CodeSigning.cer" set "ASSETS=!ASSETS! "ssl-certificate\EpicGamesAccountSwitcher-CodeSigning.cer""
gh release create "!TAG!" !ASSETS! --title "Epic Games Account Switcher !TAG!" --notes-file "compiled\release-notes.md" --verify-tag
if errorlevel 1 (
    echo [ERROR] GitHub release creation failed. The tag is pushed. Retry with:
    echo   gh release create !TAG! !ASSETS! --title "Epic Games Account Switcher !TAG!" --notes-file compiled\release-notes.md --verify-tag
    exit /b 1
)
echo.
echo ###########################################################
echo SUCCESS: !TAG! is published. Users get it through the in-app updater.
echo ###########################################################
exit /b 0

:rollback
echo [INFO] Nothing was published. Reverting local version/notes edits...
git checkout -- Directory.Build.props docs\changes.md docs\CHANGELOG.md >nul 2>&1
exit /b 1
