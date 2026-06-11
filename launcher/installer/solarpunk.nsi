; Solarpunk installer
; ------------------------
; Builds a Windows installer that:
;   - Installs Solarpunk.Launcher (+ runtime deps) into %LOCALAPPDATA%\Solarpunk\app
;   - Installs the client UE4SS overlay (dist/ue4ss-client: UE4SS.dll + Mods/SolarpunkConnect)
;   - Adds Start Menu and Desktop shortcuts
;   - Optionally registers "boot at startup" (configurable via launcher Settings)
;   - Registers the solarpunk:// URL scheme for hosting-panel hand-off links
;   - Provides a clean uninstaller that removes app files but PRESERVES user data
;     (servers.json, launcher-settings.json); a full wipe lives in the launcher.
;
; v1 transport is config-only (Engine.ini + UE4SS Lua overlay); there is NO
; Frida/Python injector step (the dwmapi proxy / launcher inject replaces it),
; so the Beacon "Python runtime for mod loader" section is intentionally dropped.
;
; Build (from WSL):
;   makensis -DSOLARPUNK_VERSION=0.1.0 installer/solarpunk.nsi
; Output:
;   installer/dist/SundialSetup-<version>.exe

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!ifndef SOLARPUNK_VERSION
  !define SOLARPUNK_VERSION "0.1.0"
!endif

!define PRODUCT_NAME "Sundial"
!define PRODUCT_VENDOR "HumanGenome"
!define PRODUCT_HOMEPAGE "https://github.com/HumanGenome/Sundial"
!define PRODUCT_UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Solarpunk"
!define PRODUCT_BUNDLE_ID "com.humangenome.solarpunk"
!define PRODUCT_REGKEY "Software\HumanGenome\Solarpunk"
!define STARTUP_REGKEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define STARTUP_REGNAME "Solarpunk"

Name "${PRODUCT_NAME}"
OutFile "dist\SundialSetup-${SOLARPUNK_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Solarpunk"
InstallDirRegKey HKCU "${PRODUCT_REGKEY}" "InstallDir"
RequestExecutionLevel user
BrandingText "Sundial | github.com/HumanGenome/Sundial"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${SOLARPUNK_VERSION}.0"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "CompanyName" "${PRODUCT_VENDOR}"
VIAddVersionKey "FileDescription" "Solarpunk Installer"
VIAddVersionKey "FileVersion" "${SOLARPUNK_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 HumanGenome"

; ---------- pages ----------

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\app\Solarpunk.Launcher.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch Solarpunk"
!define MUI_FINISHPAGE_LINK "Solarpunk on GitHub"
!define MUI_FINISHPAGE_LINK_LOCATION "${PRODUCT_HOMEPAGE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; ---------- install ----------

Section "Sundial (required)" SecLauncher
  SectionIn RO

  ; Reinstall hygiene: close any running launcher so files unlock, then sweep
  ; half-staged auto-update artifacts.
  DetailPrint "Closing any running Sundial launcher..."
  ExecWait 'taskkill /F /IM Solarpunk.Launcher.exe /T' $0
  Sleep 500

  IfFileExists "$INSTDIR\app.pending\*.*" 0 +2
    RMDir /r "$INSTDIR\app.pending"
  IfFileExists "$INSTDIR\app.old\*.*" 0 +2
    RMDir /r "$INSTDIR\app.old"
  Delete "$INSTDIR\update.zip"
  Delete "$INSTDIR\SolarpunkUpdater.cmd"
  Delete "$INSTDIR\SolarpunkUpdater.log"

  SetOutPath "$INSTDIR\app"
  ; Pull every file from the published launcher folder
  File /r "..\src\Solarpunk.Launcher\bin\Release\net8.0-windows\win-x64\publish\*.*"

  ; UE4SS client runtime: UE4SS.dll + Mods/SolarpunkConnect.
  ; Path matches what the launcher's FindUe4ssRoot probes for.
  SetOutPath "$INSTDIR\ue4ss"
  File /r /x "*.log" "..\dist\ue4ss-client\*.*"

  RMDir /r "$INSTDIR\app\ue4ss"
  SetOutPath "$INSTDIR\app\ue4ss"
  File /r /x "*.log" "..\dist\ue4ss-client\*.*"

  ; Branding assets shipped alongside the launcher for splash / about
  SetOutPath "$INSTDIR\branding"
  File /r "..\dist\branding\*.*"

  ; Reset OutPath back to the install root so the Finish-page hand-off runs
  ; with CWD=$INSTDIR.
  SetOutPath "$INSTDIR"

  ; Install metadata for uninstaller + auto-updater.
  ClearErrors
  WriteRegStr HKCU "${PRODUCT_REGKEY}" "InstallDir" "$INSTDIR"
  IfErrors 0 +3
    DetailPrint "WARNING: could not write HKCU\${PRODUCT_REGKEY}\InstallDir"
    DetailPrint "Auto-update will fall back to AppContext.BaseDirectory inference."
  WriteRegStr HKCU "${PRODUCT_REGKEY}" "Version" "${SOLARPUNK_VERSION}"
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "DisplayVersion" "${SOLARPUNK_VERSION}"
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "Publisher" "${PRODUCT_VENDOR}"
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "URLInfoAbout" "${PRODUCT_HOMEPAGE}"
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\app\Solarpunk.Launcher.exe,0"
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\uninstall.exe$\""
  WriteRegStr HKCU "${PRODUCT_UNINSTALL_KEY}" "QuietUninstallString" "$\"$INSTDIR\uninstall.exe$\" /S"
  WriteRegDWORD HKCU "${PRODUCT_UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${PRODUCT_UNINSTALL_KEY}" "NoRepair" 1

  ; Register the solarpunk:// URL scheme so links from a hosting provider's panel
  ; ("Open in Sundial" buttons on a game-service page or
  ; server manage pages) hand off to a running (or freshly launched)
  ; Sundial launcher with the server pre-filled. Format a panel emits:
  ;   solarpunk://add?host=X&port=Y&label=Z
  WriteRegStr HKCU "Software\Classes\solarpunk" "" "URL:Solarpunk Protocol"
  WriteRegStr HKCU "Software\Classes\solarpunk" "URL Protocol" ""
  WriteRegStr HKCU "Software\Classes\solarpunk\DefaultIcon" "" "$INSTDIR\app\Solarpunk.Launcher.exe,0"
  WriteRegStr HKCU "Software\Classes\solarpunk\shell\open\command" "" "$\"$INSTDIR\app\Solarpunk.Launcher.exe$\" $\"%1$\""

  WriteUninstaller "$INSTDIR\uninstall.exe"
SectionEnd

Section "Start Menu shortcut" SecStartMenu
  CreateDirectory "$SMPROGRAMS\Solarpunk"
  CreateShortCut "$SMPROGRAMS\Solarpunk\Solarpunk.lnk" "$INSTDIR\app\Solarpunk.Launcher.exe" "" "$INSTDIR\app\Solarpunk.Launcher.exe" 0
  CreateShortCut "$SMPROGRAMS\Solarpunk\Uninstall.lnk" "$INSTDIR\uninstall.exe"
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortCut "$DESKTOP\Solarpunk.lnk" "$INSTDIR\app\Solarpunk.Launcher.exe" "" "$INSTDIR\app\Solarpunk.Launcher.exe" 0
SectionEnd

Section "Start with Windows" SecAutostart
  ; On by default — Solarpunk should be immediately reachable after a reboot.
  ; --minimized keeps it out of the player's face until they need it.
  WriteRegStr HKCU "${STARTUP_REGKEY}" "${STARTUP_REGNAME}" "$\"$INSTDIR\app\Solarpunk.Launcher.exe$\" --minimized"
SectionEnd

LangString DESC_SecLauncher ${LANG_ENGLISH} "Solarpunk + bundled client mod runtime."
LangString DESC_SecStartMenu ${LANG_ENGLISH} "Add Solarpunk to the Start Menu."
LangString DESC_SecDesktop ${LANG_ENGLISH} "Place a Solarpunk shortcut on the desktop."
LangString DESC_SecAutostart ${LANG_ENGLISH} "Launch Solarpunk automatically when you sign in to Windows. Can be toggled later from Settings."

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecLauncher} $(DESC_SecLauncher)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartMenu} $(DESC_SecStartMenu)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_SecDesktop)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAutostart} $(DESC_SecAutostart)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- uninstall ----------

Section "Uninstall"
  ; Kill running launcher first so files unlock
  ExecWait 'taskkill /F /IM Solarpunk.Launcher.exe /T' $0
  Sleep 500

  ; Remove install tree but keep user data unless user explicitly opted in
  RMDir /r "$INSTDIR\app"
  RMDir /r "$INSTDIR\ue4ss"
  RMDir /r "$INSTDIR\branding"
  Delete "$INSTDIR\uninstall.exe"

  ; Leave servers.json / launcher-settings.json so a re-install resumes.
  RMDir "$INSTDIR"

  ; Shortcuts
  Delete "$SMPROGRAMS\Solarpunk\Solarpunk.lnk"
  Delete "$SMPROGRAMS\Solarpunk\Uninstall.lnk"
  RMDir  "$SMPROGRAMS\Solarpunk"
  Delete "$DESKTOP\Solarpunk.lnk"

  ; Registry
  DeleteRegValue HKCU "${STARTUP_REGKEY}" "${STARTUP_REGNAME}"
  DeleteRegKey HKCU "${PRODUCT_UNINSTALL_KEY}"
  DeleteRegKey HKCU "${PRODUCT_REGKEY}"
  DeleteRegKey HKCU "Software\Classes\solarpunk"

  ; AppData side — leave alone. A full "revert to vanilla Solarpunk" wipe
  ; lives in the launcher under Settings -> Remove Solarpunk.
SectionEnd
