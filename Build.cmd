@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
echo ###########################################################
echo Starting Epic Games Account Switcher Compilation (Release)
echo ###########################################################
set "BUILD_CONFIG=Release"
set "currentStage=Pre-flight Checks"
echo.
echo [0/4] !currentStage!...
dotnet --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] .NET SDK not found. Please install .NET 8 SDK.
    timeout /t 10 /nobreak >nul 2>nul
    exit /b 1
)
set "requiredDirs=Epic-Games_Account_Switcher-Client Epic-Games_Account_Switcher-Globals SingleFilePacker"
for %%d in (!requiredDirs!) do (
    if not exist "%%d" (
        echo [ERROR] Required source directory missing: %%d
        timeout /t 10 /nobreak >nul 2>nul
        exit /b 1
    )
)
set "currentStage=Cleaning"
echo.
echo [1/4] !currentStage! previous build...
if exist "compiled" rd /s /q "compiled"
if exist "Payload" rd /s /q "Payload"
mkdir compiled
mkdir Payload
set "currentStage=Compiling Client"
echo.
echo [2/4] !currentStage! (!BUILD_CONFIG!)...
dotnet publish "Epic-Games_Account_Switcher-Client\Epic-Games_Account_Switcher-Client.csproj" -c !BUILD_CONFIG! -r win-x64 --self-contained false -o "Payload" -v normal
if %ERRORLEVEL% NEQ 0 goto :error
if exist "Skin_Icons" (
    echo [IO] Copying Skin_Icons to Payload...
    xcopy /s /e /y /i "Skin_Icons" "Payload\Skin_Icons"
)
set "currentStage=Resource Packing"
echo.
echo [3/4] !currentStage!...
dotnet run --project "SingleFilePacker\SingleFilePacker.csproj" -- --pack
if %ERRORLEVEL% NEQ 0 goto :error
set "currentStage=Generating Distribution EXE"
echo.
echo [4/4] !currentStage!...
dotnet build "SingleFilePacker\SingleFilePacker.csproj" -c !BUILD_CONFIG! -r win-x64 -o "compiled" -v normal
if %ERRORLEVEL% NEQ 0 goto :error
if exist "compiled\SingleFilePacker.exe" (
    move /y "compiled\SingleFilePacker.exe" "compiled\Epic-Games_Account_Switcher.exe"
    echo [IO] Purging auxiliary build artifacts...
    for %%f in (compiled\*) do (
        if /i not "%%~nxf"=="Epic-Games_Account_Switcher.exe" del /f /q "%%f"
    )
)
if exist "SingleFilePacker\payload.zip" del /f /q "SingleFilePacker\payload.zip"
echo.
echo ###########################################################
echo SUCCESS: Build completed successfully.
echo Output location: .\compiled\Epic-Games_Account_Switcher.exe
echo ###########################################################
timeout /t 10 /nobreak >nul 2>nul
exit /b 0
:error
echo.
echo ###########################################################
echo ERROR: Build failed with exit code %ERRORLEVEL% at stage: !currentStage!
echo ###########################################################
timeout /t 10 /nobreak >nul 2>nul
exit /b %ERRORLEVEL%
