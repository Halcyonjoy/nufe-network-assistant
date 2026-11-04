#ifndef PackageDir
  #define PackageDir "..\artifacts\preview-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef AppVersion
  #error Build with installer/build-installer.ps1 to read the project version.
#endif

[Setup]
AppId={code:InstallationId}
AppName=NUFE Campus 校园网助手
AppVersion={#AppVersion}
AppPublisher=NUFE Campus
DefaultDirName={localappdata}\Programs\NUFE Campus
DefaultGroupName=NUFE Campus
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dark
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\app.ico
OutputDir={#OutputDir}
OutputBaseFilename=NUFE-Campus-Setup-{#AppVersion}-x64
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=no
UsePreviousLanguage=no
UsePreviousGroup=no
UsePreviousTasks=no
UsePreviousSetupType=no
UsePreviousPrivileges=no
SetupLogging=yes
Uninstallable=yes
InfoBeforeFile=install-notes.txt

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
chinesesimp.WelcomeLabel2=即将在您的计算机上安装 [name/ver]。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Check: not IsIsolated

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\NufeCampus.SetupSupport.exe"; Flags: dontcopy

[Icons]
Name: "{userprograms}\NUFE Campus\NUFE Campus"; Filename: "{app}\NufeCampus.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"; Check: not IsIsolated
Name: "{userprograms}\NUFE Campus\卸载 NUFE Campus"; Filename: "{uninstallexe}"; IconFilename: "{app}\app.ico"; Check: not IsIsolated
Name: "{userdesktop}\NUFE Campus"; Filename: "{app}\NufeCampus.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon; Check: not IsIsolated

[Run]
Filename: "{app}\NufeCampus.App.exe"; Description: "打开 NUFE Campus"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked; Check: not IsIsolated and not IsUpdate

[Code]
var DeleteUserData: Boolean;

function InitializeUninstall: Boolean;
var Form: TSetupForm; Check: TNewCheckBox; Info: TNewStaticText;
    ProceedButton, CancelButton: TNewButton;
begin
  Result := True;
  DeleteUserData := False;
  if UninstallSilent then Exit;
  Form := CreateCustomForm(440, 190, False, False);
  try
    Form.Caption := '卸载 NUFE Campus';
    Info := TNewStaticText.Create(Form);
    Info.Parent := Form;
    Info.SetBounds(ScaleX(20), ScaleY(20), ScaleX(400), ScaleY(52));
    Info.AutoSize := False;
    Info.WordWrap := True;
    Info.Caption := '卸载将停止并移除本程序的保活任务。默认保留用户数据，方便以后重新安装。';
    Check := TNewCheckBox.Create(Form);
    Check.Parent := Form;
    Check.SetBounds(ScaleX(20), ScaleY(76), ScaleX(400), ScaleY(24));
    Check.Caption := '同时删除用户数据';
    Info := TNewStaticText.Create(Form);
    Info.Parent := Form;
    Info.SetBounds(ScaleX(20), ScaleY(104), ScaleX(400), ScaleY(32));
    Info.AutoSize := False;
    Info.WordWrap := True;
    Info.Caption := '删除账号配置、加密凭据、日志及保活记录。手动导出的文件不受影响。';
    ProceedButton := TNewButton.Create(Form);
    ProceedButton.Parent := Form;
    ProceedButton.SetBounds(ScaleX(252), ScaleY(150), ScaleX(80), ScaleY(26));
    ProceedButton.Caption := '继续卸载';
    ProceedButton.ModalResult := mrOk;
    ProceedButton.Default := True;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(340), ScaleY(150), ScaleX(80), ScaleY(26));
    CancelButton.Caption := '取消';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    Result := Form.ShowModal = mrOk;
    DeleteUserData := Result and Check.Checked;
  finally
    Form.Free;
  end;
end;

function UninstallDataArguments: String;
begin
  Result := '';
  if DeleteUserData then Result := ' --delete-user-data';
end;

function IsIsolated: Boolean;
begin
  Result := ExpandConstant('{param:TESTROOT|}') <> '';
end;

function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function InstallationId(Param: String): String;
begin
  Result := 'NUFE-Campus-9CB0D426-6145-468B-BEF1-309DDB462A6B';
  if IsIsolated then Result := Result + '-test-' + ExtractFileName(ExpandConstant('{param:TESTROOT|}'));
end;

function TestArguments: String;
begin
  Result := '';
  if IsIsolated then Result := ' --test-root "' + ExpandConstant('{param:TESTROOT|}') + '"';
end;

function InitializeSetup: Boolean;
var Root, Name: String; I: Integer;
begin
  Result := True;
  if IsIsolated then begin
    Root := ExpandFileName(ExpandConstant('{param:TESTROOT|}'));
    Name := ExtractFileName(Root);
    Result := (CompareText(ExtractFileDir(Root), RemoveBackslashUnlessRoot(GetEnv('TEMP'))) = 0) and
      (Length(Name) = 48) and (Copy(Name, 1, 16) = 'NufeCampus-test-');
    if Result then for I := 17 to 48 do
      if Pos(Name[I], '0123456789abcdef') = 0 then Result := False;
    if not Result then MsgBox('隔离安装目录无效。', mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer; Helper, Parameters: String;
begin
  Result := '';
  if IsIsolated and (CompareText(ExpandFileName(ExpandConstant('{app}')),
    ExpandFileName(AddBackslash(ExpandConstant('{param:TESTROOT|}')) + 'app')) <> 0) then begin
    Result := '隔离安装仅允许写入 TESTROOT\app。'; Exit;
  end;
  ExtractTemporaryFile('NufeCampus.SetupSupport.exe');
  Helper := ExpandConstant('{tmp}\NufeCampus.SetupSupport.exe');
  Parameters := 'prepare --target "' + ExpandConstant('{app}') + '"' + TestArguments;
  if not Exec(Helper, Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;
  if Code <> 0 then Result := '请先关闭 NUFE Campus 窗口及等待自助操作结束，再重试。维护返回码：' + IntToStr(Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer;
begin
  if CurStep = ssPostInstall then begin
    if not Exec(ExpandConstant('{app}\NufeCampus.SetupSupport.exe'),
      'installed --target "' + ExpandConstant('{app}') + '"' + TestArguments,
      '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;
    if Code <> 0 then RaiseException('迁移现有自启动失败，请重新运行安装程序。返回码：' + IntToStr(Code));
  end;
end;

procedure DeinitializeSetup;
var Code: Integer; Helper: String;
begin
  Helper := ExpandConstant('{tmp}\NufeCampus.SetupSupport.exe');
  if FileExists(Helper) then
    Exec(Helper, 'restore --target "' + ExpandConstant('{app}') + '"' + TestArguments,
      '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Code: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    if not Exec(ExpandConstant('{app}\NufeCampus.SetupSupport.exe'),
      'uninstall --target "' + ExpandConstant('{app}') + '"' + TestArguments + UninstallDataArguments,
      '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;
    if Code <> 0 then begin
      MsgBox('停止程序、移除保活任务或清理用户数据未完成。请关闭 NUFE Campus 并检查数据目录权限，再重试卸载。返回码：' + IntToStr(Code), mbError, MB_OK);
      Abort;
    end;
  end;
end;
