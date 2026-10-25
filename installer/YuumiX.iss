; YuumiX 安装包定义（Inno Setup 6）
;
; 由 .github/workflows/release.yml 在发布时调用：
;   ISCC.exe /DVersion=0.1.0-rc.1 /DNumericVersion=0.1.0.0 /DSourceDir=<publish 目录> YuumiX.iss
;
; 三条不可随意改动的设计（改之前先读 src/YuumiX.Core/Runtime/RuntimePaths.cs 与
; src/YuumiX.Core/Diagnostics/RotatingFileLoggerProvider.cs）：
;
;   1) PrivilegesRequired=lowest，装到 %LOCALAPPDATA%\Programs\YuumiX。
;      装到 Program Files 会让普通用户无法写 exe 旁的 log\ ——
;      RotatingFileLoggerProvider 捕获 UnauthorizedAccessException 后静默丢弃全部日志，
;      程序不崩，但用户报 bug 时没有任何诊断材料。用户级安装同时让静默原地更新无需提权。
;
;   2) 绝不打包 portable.flag。该文件是便携版的唯一判据，一旦随安装包落地，
;      RuntimePaths 会把数据目录判成 {app}\data 而不是 %APPDATA%\Yuumi，
;      并且应用内更新会走 zip 覆盖分支，而不是安装器分支。
;
;   3) 安装包文件名不得含 portable（OutputBaseFilename 已固定为 *_x64-setup）。
;      UpdateService 的便携资产判据是「名称含 portable 且 .zip 结尾」，
;      安装版判据是「名称含 setup 且 .exe 结尾」，两者互相排除，命名不能混。

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish-rc"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\setup"
#endif

[Setup]
; AppId 一经发布不可更改：它决定卸载项的注册键，改了会导致「新旧两份」同时存在。
AppId={{6B1F5A72-9C4E-4D38-9A1B-7E2F0C5D8A31}
AppName=YuumiX
AppVersion={#Version}
AppVerName=YuumiX {#Version}
; Inno 会把 AppVersion 直接塞进版本资源，遇到 -rc.1 这类后缀时降级成 "0.1.0-rc.1X"。
; 数字段归 VersionInfo*，完整版本号归 *TextVersion（控制台「应用和功能」显示的就是后者）。
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#Version}
VersionInfoTextVersion={#Version}
AppPublisher=ISuuuu
AppPublisherURL=https://github.com/ISuuuu/YuumiX
AppSupportURL=https://github.com/ISuuuu/YuumiX/issues
AppUpdatesURL=https://github.com/ISuuuu/YuumiX/releases
; 用户级安装：不弹 UAC，且 exe 旁目录可写（见文件头第 1 条）。
; 不声明 PrivilegesRequiredOverridesAllowed，避免用户选「为所有用户安装」装到 Program Files。
DefaultDirName={localappdata}\Programs\YuumiX
DefaultGroupName=YuumiX
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=YuumiX_v{#Version}_x64-setup
SetupIconFile=..\src\YuumiX.App\Assets\AppIcon.ico
UninstallDisplayName=YuumiX
UninstallDisplayIcon={app}\YuumiX.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 与 README「Windows 10 1809（17763）及以上」保持一致。
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 静默更新时由我们的批处理保证主进程已退出，这里再兜一层关掉残留实例；
; RestartApplications=no：重启由批处理显式执行，避免安装器与脚本各启动一份。
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 主程序与全部运行时（自包含，约 162 MB / 279 个文件）。
; Excludes 生效于 recursesubdirs，发布目录里的调试符号不进用户包。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
; 说明文档只在仓库根，publish 输出里没有，单独带进来。
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\YuumiX"; Filename: "{app}\YuumiX.exe"
Name: "{group}\卸载 YuumiX"; Filename: "{uninstallexe}"
Name: "{autodesktop}\YuumiX"; Filename: "{app}\YuumiX.exe"; Tasks: desktopicon

[Run]
; skipifsilent：静默更新时由 run-setup.cmd 负责启动新版本，这里不能再启一次。
Filename: "{app}\YuumiX.exe"; Description: "启动 YuumiX"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理 exe 旁的日志；%APPDATA%\Yuumi 是用户配置与战绩库，卸载不删。
Type: filesandordirs; Name: "{app}\log"
