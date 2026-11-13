@echo off
rem Self-contained single-file build (TZ §5). Requires the .NET 8 SDK.
rem Output goes outside Drive: %LOCALAPPDATA%\AIWorkbenchBuild\co-desktop\publish (bin/obj: Directory.Build.props).
cd /d "%~dp0"
set "PUB=%LOCALAPPDATA%\AIWorkbenchBuild\co-desktop\publish"
dotnet test tests\CO.Core.Tests || exit /b 1
rem Tests of the desktop parts of modules (modules\<id>\tests\<Name>.Tests, see ModuleParts.props).
rem (cmd expands a wildcard only in the last part of a path: one loop per level)
for /d %%m in (..\modules\*) do for /d %%t in ("%%m\tests\*") do if exist "%%t\%%~nxt.csproj" (dotnet test "%%t" || exit /b 1)
dotnet publish src\CO.Desktop -c Release -o "%PUB%" -p:EnableCompressionInSingleFile=true -p:DebugType=none || exit /b 1
echo.
echo BUILD OK: %PUB%\CO.Desktop.exe
powershell -NoProfile -ExecutionPolicy Bypass -File tools\co-sign.ps1 "%PUB%\CO.Desktop.exe" || exit /b 1
rem After makensis sign the installer too: powershell -File tools\co-sign.ps1 installer\CO-Desktop-Setup-x.y.z.exe
