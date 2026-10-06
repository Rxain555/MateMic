; =============================================================================
;  MateMic 安装程序脚本（Inno Setup 6）
; =============================================================================
;  编译方式（二选一）：
;    ① 推荐：installer\build-installer.ps1 -WithSetup  一键完成"发布 + 编译"
;    ② 手动：发布到 dist\MateMic 后，用 Inno Setup 打开本文件直接编译
;       （源目录默认取 ..\dist\MateMic，可用 /DMySourceDir=... 覆盖）
;
;  为什么用 Inno Setup 而不是 VS 自带的安装项目 / MSIX：
;    · 脚本化、纯文本，可读可改可进版本库；改一行不会牵动别的东西
;    · 对中文安装界面是一等公民（自带官方简体中文语言包）
;    · 默认**按用户安装**，不碰 Program Files、不需要管理员
;
;  ⚠ 关于"装完之后改不动 / 老是报错"：
;    本程序是绿色便携式设计：配置、降噪模型、录音、日志都放在 exe 同级的 data\ 里。
;    装到 Program Files（或任何非用户可写目录）后，写配置会失败，程序只好退到
;    %LocalAppData%，于是出现"设置不生效 / 配置找不到"这类怪问题。
;    因此这里默认装到 %LocalAppData%\Programs\MateMic（每个用户各自一份），
;    既不需要管理员，也能随意读写自己的数据目录。
;
;  ⚠ 开机自启刻意**不**在安装阶段写注册表：程序自己的「开机自启」开关以注册表为准，
;    安装时写进去反而可能与配置状态不一致。让用户在那个开关上点一下更干净。
; =============================================================================

#ifndef MySourceDir
  #define MySourceDir "..\dist\MateMic"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "0.3.4"
#endif

[Setup]
AppId={{8F3A6D21-4C7E-4B9A-9E52-1D6B7C4A55E1}
AppName=MateMic
AppVersion={#MyAppVersion}
AppVerName=MateMic {#MyAppVersion}
AppPublisher=MateMic
DefaultDirName={localappdata}\Programs\MateMic
DefaultGroupName=MateMic
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=MateMic-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 中文安装界面：Inno Setup 6 自带官方简体中文语言包
ShowLanguageDialog=auto

; 按用户安装：不需要管理员权限
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Windows 10 1809 及以上（本程序依赖 .NET 9 与 WASAPI）
MinVersion=10.0.17763

; 卸载时若程序还在运行，提示用户关掉
CloseApplications=yes
RestartApplications=no

; ---------------------------------------------------------------------------
; 语言
; ---------------------------------------------------------------------------
; ⚠ Inno Setup 6 自带的 Languages\ 目录里**没有**简体中文——官方把它挪到了官网的
;   "非官方翻译" 页面（jrsoftware.org/files/istrans/）。因此这里做成条件包含：
;     · 找得到 ChineseSimplified.isl  → 中英双语，中文环境直接进中文向导
;     · 找不到                        → 只用英文，保证一定能编过
;   想要中文向导：从上面那个页面下载 ChineseSimplified.isl，放进
;     <Inno Setup 安装目录>\Languages\
;   或放到本目录（installer\ChineseSimplified.isl）即可，脚本会自动挑一个。
; ---------------------------------------------------------------------------
#ifdef HaveChinese
[Languages]
Name: "chinese"; MessagesFile: "{#ChineseIslPath}"
Name: "english"; MessagesFile: "compiler:Default.isl"
#else
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

; ---------------------------------------------------------------------------
; 任务与文件
; ---------------------------------------------------------------------------
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序、依赖与随程序内置的降噪模型（models\ 子目录一并带上）
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; TestNoIcons 仅供自动化测试：受限环境（例如只允许写某个目录的沙箱）建不了开始菜单项，
; 安装会整体回滚、卸载器也就无从产生。加上这个开关就能在那种环境里跑完整的
; "安装 → 卸载"验证。正常打包不会定义它。
#ifndef TestNoIcons
Name: "{group}\MateMic"; Filename: "{app}\MateMic.exe"
Name: "{group}\卸载 MateMic"; Filename: "{uninstallexe}"
#endif
Name: "{autodesktop}\MateMic"; Filename: "{app}\MateMic.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MateMic.exe"; Description: "立即运行 MateMic"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理程序运行期产生的临时内容。
; data\ 下的配置/模型/录音**默认保留** —— 是否删除由下面的 [Code] 段询问用户。
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\runtime"

; ---------------------------------------------------------------------------
; 卸载时询问：是否连同配置数据一起删除
; ---------------------------------------------------------------------------
; 为什么不写在 [UninstallDelete] 里直接删：
;   data\ 里的 config.json / models\ / recordings\ 都是**程序运行期**生成的，
;   安装程序并不知道它们的存在（Inno 只删自己装过的文件）。对用户来说，
;   这些恰恰是最不该被静默抹掉的东西——删了就找不回来。
;
; 时序安排（两个回调各司其职）：
;   · InitializeUninstall —— 卸载刚启动、还没动任何文件时**先问**。
;     放在这里而不是 usPostUninstall，是为了让用户尽早看到选择；
;     若等到最后才问，用户已经看着进度条走完了，体验很怪。
;   · usPostUninstall —— 程序文件都清完之后再删数据目录。
;     放在最后是因为此前的任何一步（关闭正在运行的程序、删文件）都可能失败回滚，
;     过早删掉用户数据就再也回不来了。
; ---------------------------------------------------------------------------
[Code]
var
  DeleteUserData: Boolean;

function InitializeUninstall(): Boolean;
var
  DataPath: String;
begin
  Result := True;
  DeleteUserData := False;

  DataPath := ExpandConstant('{app}\data');

  // 没有数据目录就没什么可问的
  if not DirExists(DataPath) then
    Exit;

  // 静默卸载（/SILENT、/VERYSILENT）不弹窗，采用**保留数据**这个安全默认值：
  // 用户数据删了就找不回来，宁可留着让用户自己清理。
  if UninstallSilent() then
    Exit;

  case ActiveLanguage() of
    'chinese':
      DeleteUserData :=
        MsgBox('是否同时删除配置数据？' + #13#10 + #13#10 +
               '包含：你的设置、降噪模型、录音、日志' + #13#10 +
               DataPath + #13#10 + #13#10 +
               '选择「否」则保留，方便你以后重装时继续使用。',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  else
    DeleteUserData :=
      MsgBox('Also delete your settings and data?' + #13#10 + #13#10 +
             'Includes: settings, denoise models, recordings, logs' + #13#10 +
             DataPath + #13#10 + #13#10 +
             'Choose No to keep them for a future reinstall.',
             mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // 程序文件都删完之后再动用户数据
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    DelTree(ExpandConstant('{app}\data'), True, True, True);

    // 补删程序目录本身。
    // 到了这一步，Inno"删除自己创建的空目录"的阶段早已结束——它执行时 data\ 还在，
    // 所以 {app} 被判为非空而跳过；等 data\ 在这里被删掉，{app} 就成了孤儿空目录。
    // 注意：靠 [UninstallDelete] 的 "dirifempty" 解决不了，那条属于主卸载阶段，
    // 执行时 data\ 仍在，同样不会被删。
    // RemoveDir 只在目录确实为空时成功；非空（数据保留、或还有别的残留）时静默失败。
    RemoveDir(ExpandConstant('{app}'));
  end;
end;
