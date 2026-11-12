@echo off
setlocal
rem Freedom F3a: the .NET 8 Bridge from the same sources as build.cmd (build.cmd / net48 stays the release path).
rem Usage: net8\build-net8.cmd [output dir]   (default %LOCALAPPDATA%\AIWorkbenchBuild\bridge-net8-publish)
rem   1. dotnet build  net8.0-windows (WinForms tray) and net8.0 (headless: macOS / Linux)
rem   2. tests: net8\tests (adapters, JSON parity with net48, the existing tests\*.cs suite) on both
rem   3. dotnet publish, self-contained single file: win-x64 (net8.0-windows), osx-arm64, osx-x64, linux-x64 (net8.0)
rem Macs: the osx-* output is unsigned; see --platform-info and --install-autostart (LaunchAgent).
cd /d "%~dp0.."
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%LOCALAPPDATA%\AIWorkbenchBuild\bridge-net8-publish"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

dotnet build net8\Bridge.Net8.csproj -c Release || goto :fail
dotnet build net8\tests -c Release || goto :fail
for %%F in (net8.0-windows net8.0) do (
    "%LOCALAPPDATA%\AIWorkbenchBuild\Bridge.Net8.Tests\bin\Release\%%F\BridgeNet8Tests.exe" || goto :fail
)

dotnet publish net8\Bridge.Net8.csproj -c Release -f net8.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%OUT%\win-x64" || goto :fail
for %%R in (osx-arm64 osx-x64 linux-x64) do (
    dotnet publish net8\Bridge.Net8.csproj -c Release -f net8.0 -r %%R --self-contained true -p:PublishSingleFile=true -o "%OUT%\%%R" || goto :fail
)
echo.
echo BUILD OK: %OUT%\{win-x64,osx-arm64,osx-x64,linux-x64}
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
