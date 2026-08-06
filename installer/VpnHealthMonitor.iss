; VPN Health Monitor — установщик (T-334).
; Собирается скриптом build.ps1: сначала self-contained publish в ..\publish\win-x64\,
; потом ISCC компилирует этот файл. Вручную запускать ISCC можно, но публикация должна
; быть свежей — установщик просто берёт всё из ..\publish\win-x64\.

#define MyAppName "VPN Health Monitor"
#define MyAppVersion "1.0.0"
#define MyAppExeName "VpnHealthMonitor.exe"
#define MyPublishDir "..\publish\win-x64"

[Setup]
AppId={{C6DA431E-4099-4903-A105-58155C99EF72}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisherURL=https://github.com/afest/vpn-health-monitor
; Установка per-user — прав администратора установщик не просит; их разово запрашивает
; само приложение (UAC), и только когда человек сам применяет/снимает правила firewall.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\VpnHealthMonitor
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=VpnHealthMonitor-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\VpnHealthMonitor\Heart.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; T-324/README «Требование к установщику»: деинсталлятор обязан снимать правила kill switch,
; иначе после удаления приложения остаются висячие блокировки Windows Firewall, которые
; уже никто не свяжет с исчезнувшей программой. Скрипт сам просит права администратора одним
; UAC (Silent только убирает паузы/вопросы, не элевацию) и безопасен при повторном запуске.
[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\remove-protection.ps1"" -Silent"; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "RemoveFirewallRules"
