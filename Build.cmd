@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ###########################################################
echo Starting Epic Games Account Switcher Compilation (DEBUG VERBOSE)
echo ###########################################################

echo.
echo [1/5] Cleaning previous build...
if exist "compiled" rd /s /q "compiled"
if exist "Payload" rd /s /q "Payload"
mkdir compiled
mkdir Payload

echo.
echo [2/5] Compiling Globals and Client (Debug)...
dotnet publish "Epic-Games_Account_Switcher-Client\Epic-Games_Account_Switcher-Client.csproj" -c Debug -r win-x64 --self-contained false -o "Payload" -v normal
if %ERRORLEVEL% NEQ 0 goto :error

:: Copy skin icons to payload
if exist "Skin_Icons" (
    echo [IO] Copying Skin_Icons to Payload...
    xcopy /s /e /y /i "Skin_Icons" "Payload\Skin_Icons"
)

:: Copy OpenCV anchors to payload
if exist "OpenCV_Anchors" (
    echo [IO] Copying OpenCV_Anchors to Payload...
    xcopy /s /e /y /i "OpenCV_Anchors" "Payload\OpenCV_Anchors"
)

echo.
echo [3/5] Compiling RunAs Utility (Debug)...
dotnet publish "runas\runas.csproj" -c Debug -r win-x64 --self-contained false -o "Payload\runas" -v normal
if %ERRORLEVEL% NEQ 0 goto :error

echo.
echo [4/5] Packing Payload into resource...
dotnet run --project "SingleFilePacker\SingleFilePacker.csproj" -- --pack
if %ERRORLEVEL% NEQ 0 goto :error

echo.
echo [5/5] Generating Single-File Distribution EXE...
dotnet publish "SingleFilePacker\SingleFilePacker.csproj" -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -o "compiled" -v normal
if %ERRORLEVEL% NEQ 0 goto :error

:: Rename output to something identifiable as debug
if exist "compiled\SingleFilePacker.exe" (
    move /y "compiled\SingleFilePacker.exe" "compiled\Epic-Games_Account_Switcher.exe"
) else if exist "compiled\Epic-Games_Account_Switcher.exe" (
    move /y "compiled\Epic-Games_Account_Switcher.exe" "compiled\Epic-Games_Account_Switcher.exe"
)

echo.
echo ###########################################################
echo SUCCESS: Build completed successfully.
echo Output location: .\compiled\Epic-Games_Account_Switcher.exe
echo ###########################################################
pause
exit /b 0

:error
echo.
echo ###########################################################
echo ERROR: Build failed with exit code %ERRORLEVEL% at stage: !currentStage!
echo ###########################################################
pause
exit /b %ERRORLEVEL%
