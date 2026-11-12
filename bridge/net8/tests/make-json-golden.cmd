@echo off
setlocal
rem Freedom F3a: writes tests\json-golden.txt with the REAL JavaScriptSerializer (.NET Framework 4.8, csc as build.cmd).
rem Run again after a change of JsonGolden.cs, config.json.example, the Bridge record types or tests\samples.
cd /d "%~dp0..\.."
set "OUT=%LOCALAPPDATA%\AIWorkbenchBuild\bridge-net8-golden"
if not exist "%OUT%" mkdir "%OUT%"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

"%CSC%" /nologo /target:exe /optimize+ /platform:anycpu /out:"%OUT%\JsonGolden.exe" ^
  /main:AiWorkbenchBridge.Tests.JsonGolden ^
  /resource:BridgeAgent.ico,AiWorkbenchBridge.BridgeAgent.ico ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll ^
  BridgeAgent.cs BridgeAgentRuntimes.cs BridgeAgentParsers.cs GoogleTransport.cs BridgeDevices.cs DeviceWire.cs BridgeUsage.cs BridgeCorporate.cs BridgeSchedules.cs BridgePush.cs BridgeDbQueue.cs BridgeFollowups.cs BridgePcTools.cs ^
  net8\tests\JsonGolden.cs
if errorlevel 1 (
    echo GOLDEN BUILD FAILED
    exit /b 1
)
"%OUT%\JsonGolden.exe" . net8\tests\json-golden.txt
if errorlevel 1 exit /b 1
echo WROTE net8\tests\json-golden.txt
