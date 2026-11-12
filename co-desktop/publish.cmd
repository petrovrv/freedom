@echo off
rem Self-contained single-file build (TZ §5). Requires the .NET 8 SDK.
rem Output goes outside Drive: %LOCALAPPDATA%\AIWorkbenchBuild\co-desktop\publish (bin/obj: Directory.Build.props).
cd /d "%~dp0"
set "PUB=%LOCALAPPDATA%\AIWorkbenchBuild\co-desktop\publish"
dotnet test tests\CO.Core.Tests || exit /b 1
dotnet test ..\connectors\corporate\tests\CO.Corporate.Tests || exit /b 1
dotnet publish src\CO.Desktop -c Release -o "%PUB%" -p:EnableCompressionInSingleFile=true -p:DebugType=none || exit /b 1
echo.
echo BUILD OK: %PUB%\CO.Desktop.exe
powershell -NoProfile -ExecutionPolicy Bypass -File tools\co-sign.ps1 "%PUB%\CO.Desktop.exe" || exit /b 1
rem After makensis sign the installer too: powershell -File tools\co-sign.ps1 installer\CO-Desktop-Setup-x.y.z.exe
