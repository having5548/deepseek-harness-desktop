; DeepSeek Harness 桌面客户端安装脚本 (Inno Setup 7)
; 用法: iscc.exe setup.iss
#define MyAppName "DeepSeek Harness Desktop"
#define MyAppVersion "0.6.0"
#define MyAppPublisher "DeepSeek AI"
#define MyAppExeName "DshDesktop.exe"
#define SourceDir "..\artifacts\win-x64"
#define OutputDir "..\artifacts"

[Setup]
AppId={{B4E1D6C2-9A3F-4F7E-8B5A-2C4D9E1F6A30}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\DeepSeek Harness
DefaultGroupName=DeepSeek Harness
DisableProgramGroupPage=yes
; 无需管理员权限，安装到用户 Program Files（无 UAC 弹窗）
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=DshDesktop-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\DshDesktop\Assets\AppIcon.ico
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
; 排除运行时产生的 WebView2 用户数据缓存（本地开发残留，不应随安装包分发）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "DshDesktop.exe.WebView2"

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
