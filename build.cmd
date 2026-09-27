@echo off
rem Builds with the C# compiler that ships with Windows (.NET Framework 4.x) - no SDK needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=%~dp0bin

if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /target:winexe /optimize+ /out:"%OUT%\ObsRecIndicator.exe" ^
  /win32icon:"%~dp0assets\app.ico" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  "%~dp0src\*.cs"
if errorlevel 1 exit /b 1

echo Built %OUT%\ObsRecIndicator.exe
