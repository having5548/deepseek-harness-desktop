; DeepSeek Harness 桌面客户端（Rust / Tauri 版）安装脚本 (Inno Setup 7)
; 用法: iscc.exe installer\setup.iss
;   （先运行 scripts\build-all.cmd 完成编译，产物在 src-tauri\target\release）
#define MyAppName "DeepSeek Harness Desktop"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "DeepSeek AI"
#define MyAppExeName "DshDesktop.exe"
#define SourceDir "..\src-tauri\target\release"
#define OutputDir "..\artifacts"

[Setup]
AppId={{7A3E5C81-2B4D-4F6A-8E29-C1D0B9F4A617}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\DeepSeek Harness
DefaultGroupName=DeepSeek Harness
DisableProgramGroupPage=yes
; 无需管理员权限，安装到用户 Program Files（无 UAC 弹窗）
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=DshDesktop-Setup-{#MyAppVersion}-rust
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src-tauri\icons\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; 自包含 x64 应用，仅支持 64 位系统
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
CloseApplications=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序 + WebView2Loader + resources\（捆绑 node/npm/pnpm 运行时）整体打包
Source: "{#SourceDir}\DshDesktop.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\resources\*"; DestDir: "{app}\resources"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
{ 检测 WebView2 Runtime 是否已安装（Windows 11 通常自带，Windows 10 可能缺失） }
function IsWebView2Installed: Boolean;
var
  key: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', key) or
            RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', key) or
            RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', key);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not IsWebView2Installed) then
  begin
    MsgBox('未检测到 Microsoft Edge WebView2 Runtime。' + #13#10 +
           'DeepSeek Harness 桌面客户端依赖 WebView2 渲染界面。' + #13#10 + #13#10 +
           '请访问 https://developer.microsoft.com/microsoft-edge/webview2/ 下载安装后重新启动应用。',
           mbInformation, MB_OK);
  end;
end;
