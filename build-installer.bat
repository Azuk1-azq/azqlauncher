@echo off
rem Builds the AZQLAUNCHER installer
rem Requires: .NET 8 SDK / Inno Setup 6 (https://jrsoftware.org/isdl.php)
rem Output: installer\Output\AZQLAUNCHER_Setup_beta<version>.exe
setlocal
cd /d "%~dp0"

echo [1/2] Publishing (self-contained, win-x64) ...
if exist publish rmdir /s /q publish
dotnet publish AZQLAUNCHER.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o publish
if errorlevel 1 goto :fail

echo [2/2] Building installer ...
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php
  goto :fail
)
"%ISCC%" installer\AZQLAUNCHER.iss
if errorlevel 1 goto :fail

echo.
echo Done: installer\Output
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
