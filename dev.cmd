@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "PROJECT=Epic-Games_Account_Switcher-Client\Epic-Games_Account_Switcher-Client.csproj"
set "CONFIG=Debug"
set "RUNTIME=win-x64"
set "DEV_ROOT=%TMP%\Epic_Games_Account_Switcher_DEV"
set "DEV_DATA=%DEV_ROOT%\.dev_data"
set "DEV_LOG_DIR=%DEV_ROOT%\logs"
set "DOTNET_WATCH_SUPPRESS_EMOJIS=1"

if "%~1"=="" goto WATCH
if /I "%~1"=="run" goto RUN
if /I "%~1"=="build" goto BUILD
if /I "%~1"=="restore" goto RESTORE
if /I "%~1"=="clean" goto CLEAN

echo Unknown option: %1
echo.
echo Usage:
echo   dev          Watch mode using isolated dev data.
echo   dev run      Single Debug launch using isolated dev data.
echo   dev build    Build only, no launch.
echo   dev restore  Restore NuGet packages.
echo   dev clean    Clean Debug output and isolated dev data.
goto :EOF

:WATCH
echo Starting isolated dev watch mode...
call :WIPE_DEV_STATE
call :NUKE_BUILD
dotnet watch run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% -- --dev --user-data "%DEV_DATA%" --log-dir "%DEV_LOG_DIR%"
goto :EOF

:RUN
echo Running isolated Debug launch...
call :WIPE_DEV_STATE
call :NUKE_BUILD
dotnet run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% -- --dev --user-data "%DEV_DATA%" --log-dir "%DEV_LOG_DIR%"
goto :EOF

:BUILD
echo Building Debug output...
call :NUKE_BUILD
dotnet build "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary
goto :EOF

:RESTORE
echo Restoring dependencies...
dotnet restore "%PROJECT%" -r %RUNTIME%
goto :EOF

:CLEAN
echo Cleaning Debug output and isolated dev state...
call :STOP_DEV_PROCESS
if exist "Epic-Games_Account_Switcher-Client\bin" rd /s /q "Epic-Games_Account_Switcher-Client\bin"
if exist "Epic-Games_Account_Switcher-Client\obj" rd /s /q "Epic-Games_Account_Switcher-Client\obj"
if exist "Epic-Games_Account_Switcher-Globals\bin" rd /s /q "Epic-Games_Account_Switcher-Globals\bin"
if exist "Epic-Games_Account_Switcher-Globals\obj" rd /s /q "Epic-Games_Account_Switcher-Globals\obj"
if exist "%DEV_ROOT%" rd /s /q "%DEV_ROOT%"
dotnet clean "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary
goto :EOF

:WIPE_DEV_STATE
if exist "%DEV_DATA%" (
    echo [DEV] Wiping isolated data: %DEV_DATA%
    rd /s /q "%DEV_DATA%" 2>nul
)
if exist "%DEV_LOG_DIR%" (
    echo [DEV] Clearing isolated logs: %DEV_LOG_DIR%
    del /q "%DEV_LOG_DIR%\*" 2>nul
)
if not exist "%DEV_DATA%" mkdir "%DEV_DATA%"
if not exist "%DEV_LOG_DIR%" mkdir "%DEV_LOG_DIR%"
goto :EOF

:STOP_DEV_PROCESS
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=(Resolve-Path '.').Path; Get-Process 'Epic-Games_Account_Switcher' -ErrorAction SilentlyContinue | Where-Object { try { $_.MainModule.FileName -like ($root + '*') } catch { $false } } | Stop-Process -Force" >nul 2>nul
dotnet build-server shutdown >nul 2>nul
goto :EOF

:NUKE_BUILD
echo [DEV] Wiping workspace build caches only...
call :STOP_DEV_PROCESS
if exist "Epic-Games_Account_Switcher-Client\bin" rd /s /q "Epic-Games_Account_Switcher-Client\bin"
if exist "Epic-Games_Account_Switcher-Client\obj" rd /s /q "Epic-Games_Account_Switcher-Client\obj"
if exist "Epic-Games_Account_Switcher-Globals\bin" rd /s /q "Epic-Games_Account_Switcher-Globals\bin"
if exist "Epic-Games_Account_Switcher-Globals\obj" rd /s /q "Epic-Games_Account_Switcher-Globals\obj"
goto :EOF
