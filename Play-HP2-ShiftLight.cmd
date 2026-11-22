@echo off
setlocal
cd /d "%~dp0"
if not exist "HP2-ShiftLight.exe" exit /b 1
if not exist "%~dp0Play-Volumetric-Clouds.cmd" if not exist "%~dp0speed.exe" exit /b 1
start "" "%~dp0HP2-ShiftLight.exe"
if exist "%~dp0Play-Volumetric-Clouds.cmd" (
    call "%~dp0Play-Volumetric-Clouds.cmd"
) else if exist "%~dp0speed.exe" (
    start "" "%~dp0speed.exe"
)

