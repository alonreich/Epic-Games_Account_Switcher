@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
REM ==========================================================================================
REM  Build.cmd          Release build: clean, test, publish, sign, pack, sign, SHA-256 checksum.
REM  Build.cmd --dev    Local build: same pipeline but keeps compiled\ and skips the tests.
REM  Output: compiled\Epic-Games_Account_Switcher.exe (+ .sha256). Publishing is done by Release.cmd.
REM  Version: single source of truth is Directory.Build.props (VersionPrefix). Never edit it elsewhere.
REM ==========================================================================================
set "MODE=release"
if /i "%~1"=="--dev" set "MODE=dev"
set "BUILD_CONFIG=Release"
set "OUT_EXE=compiled\Epic-Games_Account_Switcher.exe"
set "SIGN_SCRIPT=%~dp0ssl-certificate\Sign-File.ps1"
set "SIGN_REQUIRED="
if exist "%~dp0ssl-certificate\thumbprint.txt" set "SIGN_REQUIRED=-Required"
set "SIGNED=no"
echo ###########################################################
echo Epic Games Account Switcher - Build (!MODE!)
echo ###########################################################

set "currentStage=Pre-flight Checks"
echo.
echo [0/7] !currentStage!...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found. Please install the .NET 8 SDK.
    goto :error
)
for %%d in (Epic-Games_Account_Switcher-Client Epic-Games_Account_Switcher-Globals SingleFilePacker) do (
    if not exist "%%d" (
        echo [ERROR] Required source directory missing: %%d
        goto :error
    )
)
set "APP_VERSION="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content -Raw 'Directory.Build.props')).Project.PropertyGroup[0].VersionPrefix"`) do set "APP_VERSION=%%v"
if "!APP_VERSION!"=="" (
    echo [ERROR] Could not read VersionPrefix from Directory.Build.props.
    goto :error
)
echo [INFO] Version: !APP_VERSION!
set "DIRTY="
for /f "delims=" %%s in ('git status --porcelain 2^>nul') do set "DIRTY=1"
if defined DIRTY echo [WARN] Working tree has uncommitted changes. Release.cmd refuses to publish from a dirty tree.

set "currentStage=Cleaning"
echo.
echo [1/7] !currentStage!...
if /i "!MODE!"=="release" if exist "compiled" rd /s /q "compiled"
if not exist "compiled" mkdir compiled
if exist "Payload" rd /s /q "Payload"
mkdir Payload
if exist "SingleFilePacker\payload.zip" del /f /q "SingleFilePacker\payload.zip"
if exist "SingleFilePacker\payload.sha256" del /f /q "SingleFilePacker\payload.sha256"

set "currentStage=Running Tests"
echo.
echo [2/7] !currentStage!...
if /i "!MODE!"=="release" (
    dotnet test "Epic-Games_Account_Switcher-Tests\Epic-Games_Account_Switcher-Tests.csproj" -c !BUILD_CONFIG! -r win-x64 -v minimal
    if errorlevel 1 goto :error
) else (
    echo [SKIP] Tests skipped in --dev mode.
)

set "currentStage=Compiling Client"
echo.
echo [3/7] !currentStage! (!BUILD_CONFIG!)...
dotnet publish "Epic-Games_Account_Switcher-Client\Epic-Games_Account_Switcher-Client.csproj" -c !BUILD_CONFIG! -r win-x64 --self-contained false -o "Payload" -p:DebugType=none -p:DebugSymbols=false -v minimal
if errorlevel 1 goto :error
if not exist "Payload\Epic-Games_Account_Switcher.exe" (
    echo [ERROR] Publish did not produce Payload\Epic-Games_Account_Switcher.exe
    goto :error
)
del /f /q "Payload\*.pdb" >nul 2>nul
if exist "Skin_Icons" xcopy /s /e /y /i /q "Skin_Icons" "Payload\Skin_Icons" >nul

set "currentStage=Signing Application"
echo.
echo [4/7] !currentStage!...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SIGN_SCRIPT%" -Path "Payload\Epic-Games_Account_Switcher.exe","Payload\Epic-Games_Account_Switcher-Globals.dll" !SIGN_REQUIRED!
set "SIGN_RC=!errorlevel!"
if "!SIGN_RC!"=="1" goto :error

set "currentStage=Resource Packing"
echo.
echo [5/7] !currentStage!...
dotnet run --project "SingleFilePacker\SingleFilePacker.csproj" -- --pack
if errorlevel 1 goto :error

set "currentStage=Generating Installer EXE"
echo.
echo [6/7] !currentStage!...
dotnet build "SingleFilePacker\SingleFilePacker.csproj" -c !BUILD_CONFIG! -r win-x64 -o "compiled\_packer" -p:RequirePayload=true -p:DebugType=none -p:DebugSymbols=false -v minimal
if errorlevel 1 goto :error
if not exist "compiled\_packer\SingleFilePacker.exe" (
    echo [ERROR] Installer build did not produce SingleFilePacker.exe
    goto :error
)
move /y "compiled\_packer\SingleFilePacker.exe" "%OUT_EXE%" >nul
rd /s /q "compiled\_packer"
del /f /q "SingleFilePacker\payload.zip" >nul 2>nul
del /f /q "SingleFilePacker\payload.sha256" >nul 2>nul

set "currentStage=Signing Installer + Checksum"
echo.
echo [7/7] !currentStage!...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SIGN_SCRIPT%" -Path "%OUT_EXE%" !SIGN_REQUIRED!
set "SIGN_RC=!errorlevel!"
if "!SIGN_RC!"=="1" goto :error
if "!SIGN_RC!"=="0" set "SIGNED=yes"
set "SHA="
for /f "usebackq delims=" %%h in (`powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 -LiteralPath '%OUT_EXE%').Hash"`) do set "SHA=%%h"
if "!SHA!"=="" goto :error
> "%OUT_EXE%.sha256" echo !SHA!  Epic-Games_Account_Switcher.exe

echo.
echo ###########################################################
echo SUCCESS: Build completed (!MODE!).
echo Version : !APP_VERSION!
echo Signed  : !SIGNED!
echo SHA-256 : !SHA!
echo Output  : .\%OUT_EXE%  (+ .sha256)
echo ###########################################################
if not defined NO_PAUSE timeout /t 10 /nobreak >nul 2>nul
if exist "%~dp0developer_tools\SetConsoleFont.ps1" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0developer_tools\SetConsoleFont.ps1" <nul
exit /b 0

:error
set "RC=%errorlevel%"
if "%RC%"=="0" set "RC=1"
echo.
echo ###########################################################
echo ERROR: Build failed with exit code %RC% at stage: !currentStage!
echo ###########################################################
if not defined NO_PAUSE timeout /t 10 /nobreak >nul 2>nul
if exist "%~dp0developer_tools\SetConsoleFont.ps1" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0developer_tools\SetConsoleFont.ps1" <nul
exit /b %RC%
