@echo off
rem Builds the app, then the installer (dist\ObsRecIndicator-Setup-<version>.exe).
rem Needs Inno Setup 6: winget install JRSoftware.InnoSetup
setlocal

call "%~dp0build.cmd" || exit /b 1

set ISCC=
for %%I in (ISCC.exe) do set ISCC=%%~$PATH:I
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if not defined ISCC if exist "%USERPROFILE%\scoop\apps\inno-setup\current\ISCC.exe" set ISCC=%USERPROFILE%\scoop\apps\inno-setup\current\ISCC.exe
if not defined ISCC (
  echo Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup
  exit /b 1
)

"%ISCC%" /Q "%~dp0installer\ObsRecIndicator.iss" || exit /b 1
echo Installer written to %~dp0dist
