; CO Desktop installer (TZ-CO-DESKTOP §5). Per-user install, no administrator rights.
; Build: makensis -DVERSION=0.2.0 -DAPPDIR=<publish dir> -DCLIENT=<oauth-client.json> co-desktop.nsi
; Neutral release installer (Freedom 0.4.0, release/build_installers.py): no -DCLIENT; the app asks for the user's site
; and desktop OAuth client at first start (SetupWindow) or reads them from the setup wizard's install.json.

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel user

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!define APPNAME "CO Desktop"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CODesktop"

Name "${APPNAME} ${VERSION}"
!ifdef OUTFILE
  OutFile "${OUTFILE}"
!else
  OutFile "CO-Desktop-Setup-${VERSION}.exe"
!endif
InstallDir "$LOCALAPPDATA\Programs\CO Desktop"
InstallDirRegKey HKCU "Software\CODesktop" "InstallDir"
Icon "..\src\CO.Desktop\co.ico"
UninstallIcon "..\src\CO.Desktop\co.ico"
BrandingText "AI Workbench"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "Freedom contributors"
VIAddVersionKey "LegalCopyright" "Freedom contributors"

!include "MUI2.nsh"
!define MUI_ICON "..\src\CO.Desktop\co.ico"
!define MUI_UNICON "..\src\CO.Desktop\co.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\CO.Desktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Start CO Desktop and sign in"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ; WebView2 Evergreen Runtime ships with Windows 11 and current Windows 10; without it the CO window stays empty.
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  StrCmp $0 "" 0 +4
  ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  StrCmp $0 "" 0 +2
  MessageBox MB_OK|MB_ICONINFORMATION "Microsoft Edge WebView2 Runtime was not found. CO Desktop installs, but its window needs WebView2: https://go.microsoft.com/fwlink/p/?LinkId=2124703"
FunctionEnd

Section "Install"
  ; A running copy locks the executable.
  nsExec::Exec 'taskkill /IM CO.Desktop.exe /F'
  Sleep 800
  SetOutPath "$INSTDIR"
  File "${APPDIR}\CO.Desktop.exe"
!ifdef CLIENT
  File "/oname=oauth-client.json" "${CLIENT}"
!endif
  File "..\src\CO.Desktop\co.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\CO Desktop"
  CreateShortCut "$SMPROGRAMS\CO Desktop\CO Desktop.lnk" "$INSTDIR\CO.Desktop.exe" "" "$INSTDIR\co.ico"
  CreateShortCut "$SMPROGRAMS\CO Desktop\Uninstall CO Desktop.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortCut "$DESKTOP\CO Desktop.lnk" "$INSTDIR\CO.Desktop.exe" "" "$INSTDIR\co.ico"

  WriteRegStr HKCU "Software\CODesktop" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "Freedom contributors"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\co.ico"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /IM CO.Desktop.exe /F'
  Sleep 800
  Delete "$INSTDIR\CO.Desktop.exe"
  Delete "$INSTDIR\oauth-client.json"
  Delete "$INSTDIR\co.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\CO Desktop\CO Desktop.lnk"
  Delete "$SMPROGRAMS\CO Desktop\Uninstall CO Desktop.lnk"
  RMDir "$SMPROGRAMS\CO Desktop"
  Delete "$DESKTOP\CO Desktop.lnk"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\CODesktop"
  ; %LOCALAPPDATA%\CO (keys, grants, audit log, snapshots) and the Google sign-in stay; see README.
SectionEnd
