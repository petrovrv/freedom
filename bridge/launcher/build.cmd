@echo off
setlocal
rem Usage: build.cmd [output dir]. Default output is outside Drive: %LOCALAPPDATA%\AIWorkbenchBuild\launcher
cd /d "%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%LOCALAPPDATA%\AIWorkbenchBuild\launcher"
if not exist "%OUT%" mkdir "%OUT%"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /out:"%OUT%\AIWorkbenchBridge.exe" /win32icon:..\BridgeAgent.ico ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll ^
  Launcher.cs
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo BUILD OK: %OUT%\AIWorkbenchBridge.exe
endlocal
