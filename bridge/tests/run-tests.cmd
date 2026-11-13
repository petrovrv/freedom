@echo off
setlocal
rem Builds the AgentRuntime test harness (Bridge sources + tests\*.cs) and runs it. No agent CLI is started.
rem Usage: tests\run-tests.cmd [--samples DIR]... [--config config.json]...
rem config.json.example is always checked; real streams: tests\collect-samples.py (see tests\README.md).
cd /d "%~dp0.."
set "OUT=%LOCALAPPDATA%\AIWorkbenchBuild\bridge-tests"
if not exist "%OUT%" mkdir "%OUT%"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

"%CSC%" /nologo /target:exe /optimize+ /platform:anycpu /out:"%OUT%\AgentRuntimeTests.exe" ^
  /main:AiWorkbenchBridge.Tests.AgentRuntimeTests ^
  /resource:BridgeAgent.ico,AiWorkbenchBridge.BridgeAgent.ico ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll ^
  BridgeAgent.cs BridgeAgentRuntimes.cs BridgeAgentParsers.cs GoogleTransport.cs BridgeDevices.cs DeviceWire.cs BridgeUsage.cs BridgeModules.cs BridgeSchedules.cs BridgePush.cs BridgeDbQueue.cs BridgeFollowups.cs BridgePcTools.cs ^
  tests\Legacy135.cs tests\AgentRuntimeTests.cs tests\AgentRuntimeTests.Parsers.cs tests\AgentRuntimeTests.Arguments.cs tests\SupervisorTests.cs tests\RelayTests.cs tests\ModulesTests.cs tests\PcToolsTests.cs
if errorlevel 1 (
    echo TEST BUILD FAILED
    exit /b 1
)
"%OUT%\AgentRuntimeTests.exe" --config config.json.example --samples tests\samples %*
exit /b %errorlevel%
