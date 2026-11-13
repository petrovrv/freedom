@echo off
setlocal

rem Usage: build.cmd [output dir]. Default output is outside Drive: %LOCALAPPDATA%\AIWorkbenchBuild\bridge
cd /d "%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%LOCALAPPDATA%\AIWorkbenchBuild\bridge"
if not exist "%OUT%" mkdir "%OUT%"

set "CSC64=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "CSC32=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if exist "%CSC64%" (
    set "CSC=%CSC64%"
) else if exist "%CSC32%" (
    set "CSC=%CSC32%"
) else (
    echo ERROR: csc.exe was not found.
    echo Expected one of:
    echo   %CSC64%
    echo   %CSC32%
    exit /b 1
)

echo Using: %CSC%

"%CSC%" ^
  /nologo ^
  /target:winexe ^
  /optimize+ ^
  /platform:anycpu ^
  /out:"%OUT%\BridgeAgent.exe" ^
  /win32icon:BridgeAgent.ico ^
  /resource:BridgeAgent.ico,AiWorkbenchBridge.BridgeAgent.ico ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Web.Extensions.dll ^
  BridgeAgent.cs BridgeAgentRuntimes.cs BridgeAgentParsers.cs GoogleTransport.cs BridgeDevices.cs DeviceWire.cs BridgeUsage.cs BridgeModules.cs BridgeSchedules.cs BridgePush.cs BridgeDbQueue.cs BridgeFollowups.cs BridgePcTools.cs

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo.
echo BUILD OK: %OUT%\BridgeAgent.exe
echo Install: release.ps1 signs it into bridge-updates; the launcher picks it up.
endlocal
