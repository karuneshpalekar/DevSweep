; DevSweep for Windows: per-user installer (Inno Setup 6).
;
; Built by scripts/release.ps1:
;   ISCC /DAppVersion=0.1.0 /DNumericVersion=0.1.0.0 /DSourceDir=<published app folder> /DOutputDir=<dir> DevSweep.iss
;
; Per user, no administrator and no UAC prompt: files in %LOCALAPPDATA%\Programs\DevSweep, shortcuts
; in the user's Start Menu (and optionally Desktop), the Apps & features entry under HKCU.
; Silent install:   DevSweep-<version>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/TASKS=desktopicon]
; Silent uninstall: "%LOCALAPPDATA%\Programs\DevSweep\unins000.exe" /VERYSILENT [/REMOVEUSERDATA]
; /REMOVEUSERDATA (uninstall only) also deletes %LOCALAPPDATA%\DevSweep: History, settings and the
; quarantine. Without it, user data is never touched.

#ifndef AppVersion
  #error AppVersion is required, for example /DAppVersion=0.1.0
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is required: the published app folder
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif

#define AppName "DevSweep"
#define AppExe "DevSweep.exe"

[Setup]
; Never change AppId: it is how an upgrade finds the installed version.
AppId={{3F6C7A52-8B1E-4D5A-9C3E-6D1F0A7B2E94}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName=DevSweep for Windows {#AppVersion}
AppPublisher=DevSweep
AppPublisherURL=https://github.com/karuneshpalekar/DevSweep
AppSupportURL=https://github.com/karuneshpalekar/DevSweep/issues
AppUpdatesURL=https://github.com/karuneshpalekar/DevSweep/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductName=DevSweep for Windows
VersionInfoDescription=DevSweep for Windows setup
VersionInfoCompany=DevSweep
; Per user: %LOCALAPPDATA%\Programs\DevSweep. "lowest" and no override means Windows never asks for elevation.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\DevSweep
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22621
OutputDir={#OutputDir}
OutputBaseFilename=DevSweep-{#AppVersion}-win-x64-setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\..\LICENSE
InfoAfterFile=after-install.txt
UninstallDisplayName=DevSweep for Windows
; The logo: Setup's and the uninstaller's own icon, and the icon Apps & features shows (DevSweep.exe has it embedded).
SetupIconFile=..\src\DevSweep.App\Assets\DevSweep.ico
UninstallDisplayIcon={app}\{#AppExe},0
; DevSweep is stopped by the [Code] below (gently first), not by the Restart Manager dialog.
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\DevSweep"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; IconIndex: 0; Comment: "Find out what is using your disk, and clean it safely"
Name: "{autodesktop}\DevSweep"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; IconIndex: 0; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start DevSweep"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  UserDataFolderName = 'DevSweep';

function IsDevSweepRunning: Boolean;
var
  ResultCode: Integer;
begin
  // find exits 0 when tasklist listed the process.
  Result := Exec(ExpandConstant('{cmd}'),
    '/C tasklist /FI "IMAGENAME eq {#AppExe}" /NH | find /I "{#AppExe}" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Ask DevSweep to close (WM_CLOSE), give it ten seconds, then end it. Only this user's processes
// can be touched without administrator rights, which is exactly what is wanted.
procedure StopDevSweep;
var
  ResultCode, Waited: Integer;
begin
  if not IsDevSweepRunning then Exit;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Waited := 0;
  while IsDevSweepRunning and (Waited < 10000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  if IsDevSweepRunning then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;
end;

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/' + Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopDevSweep;
  if IsDevSweepRunning then
    Result := 'DevSweep is still running and could not be closed. Close it, then run Setup again.'
  else
    Result := '';
end;

// An upgrade replaces the program folder's contents, so files an older version had and this one
// does not (and old resources) do not pile up. Only done when DevSweep.exe is already in the folder
// (that is what "installed here" means), and the uninstaller's own files are kept.
procedure RemoveOldProgramFiles;
var
  App, Path: String;
  FindRec: TFindRec;
begin
  App := ExpandConstant('{app}');
  if not FileExists(App + '\{#AppExe}') then Exit;
  if FindFirst(App + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') and (Pos('unins', LowerCase(FindRec.Name)) <> 1) then
        begin
          Path := App + '\' + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            DelTree(Path, True, True, True)
          else
            DeleteFile(Path);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then RemoveOldProgramFiles;
end;

function InitializeUninstall: Boolean;
begin
  StopDevSweep;
  Result := True;
end;

// User data (History, settings, the quarantine) is kept unless the user opts in: by answering Yes
// here (the default answer is No) or by passing /REMOVEUSERDATA. A silent uninstall never asks and
// only removes it with the switch.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  RemoveData: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}') + '\' + UserDataFolderName;
  if not DirExists(DataDir) then Exit;
  RemoveData := HasSwitch('REMOVEUSERDATA');
  if (not RemoveData) and (not UninstallSilent) then
    RemoveData := SuppressibleMsgBox(
      'DevSweep has been removed. Your data was kept:' + #13#10 + #13#10 +
      DataDir + #13#10 + #13#10 +
      'It holds your History, your settings and the quarantine. The quarantine may hold items you moved ' +
      'out of the way and can still restore, so deleting it makes those items gone for good. Quarantine ' +
      'folders on other drives ("DevSweep Quarantine" in the drive''s root) are not touched either way.' + #13#10 + #13#10 +
      'Also delete this data now?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  if RemoveData then
    DelTree(DataDir, True, True, True);
end;
