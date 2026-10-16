@echo off
REM Local developer build (no tests, keeps compiled\, never touches Git or GitHub).
REM Thin wrapper so there is exactly ONE build pipeline to maintain: Build.cmd.
call "%~dp0Build.cmd" --dev
exit /b %errorlevel%
