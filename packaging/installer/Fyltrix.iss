; First per-user installer. Sandbox and production identities are intentionally separate.
; Compile through tools/Build-Installer.ps1 so the layout is validated before ISCC reads it.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef LayoutDir
  #error LayoutDir is required
#endif
#ifndef MarkerFile
  #error MarkerFile is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef VersionMS
  #error VersionMS is required
#endif
#ifndef VersionLS
  #error VersionLS is required
#endif
#ifndef BinaryVersion
  #error BinaryVersion is required
#endif
#ifdef Sandbox
  #define InstallerId "Fyltrix.Sandbox.PerUser"
  #define DisplayName "Fyltrix Installer Sandbox"
  #define FolderName "Fyltrix Installer Sandbox"
  #define SetupName "Fyltrix-Sandbox-Setup-win-x64"
  #define ModeName "Sandbox"
#else
  #define InstallerId "Fyltrix.PerUser"
  #define DisplayName "Fyltrix"
  #define FolderName "Fyltrix"
  #define SetupName "Fyltrix-Setup-win-x64"
  #define ModeName "Production"
#endif

[Setup]
AppId={#InstallerId}
AppName={#DisplayName}
AppVersion={#AppVersion}
VersionInfoVersion={#BinaryVersion}
VersionInfoTextVersion={#BinaryVersion}
AppVerName={#DisplayName} {#AppVersion}
AppPublisher=Fyltrix
AppPublisherURL=https://github.com/imamulkadir/Fyltrix
DefaultDirName={localappdata}\Programs\{#FolderName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir={#OutputDir}
OutputBaseFilename={#SetupName}
SetupIconFile=..\..\src\Fyltrix\Assets\Fyltrix.ico
UninstallDisplayIcon={app}\Fyltrix.exe
UninstallDisplayName={#DisplayName}
UninstallLogMode=append
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupMutex={#InstallerId}.Setup
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; Static ownership marker first; no profile files or wildcard deletion rules.
Source: "{#MarkerFile}"; DestDir: "{app}"; DestName: ".fyltrix-install.ini"; Flags: onlyifdoesntexist
Source: "{#LayoutDir}\UPDATE_PROTOCOL"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#LayoutDir}\startup.lock"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#LayoutDir}\app-{#AppVersion}\*"; DestDir: "{app}\app-{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\packaging\installer\INSTALLATION.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#LayoutDir}\Fyltrix.exe"; DestDir: "{app}"; Flags: ignoreversion
; Select the new complete payload last. Old payload files stay in the appended uninstall log.
Source: "{#LayoutDir}\current.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#FolderName}\{#DisplayName}"; Filename: "{app}\Fyltrix.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\{#DisplayName}"; Filename: "{app}\Fyltrix.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; Deliberately no [Run]: installation never launches an experimental copy or touches its profile.
; Deliberately no [InstallDelete]/[UninstallDelete]: remove only files recorded by Inno, never user data.

[Code]
const
  MarkerName = '.fyltrix-install.ini';
  OwnedId = '{#InstallerId}';
  ReparseAttribute = $400;

var
  StartupGate: THandle;
  UpdatePaths, UpdateHashes: TArrayOfString;
  InventoryHash: String;
  UninstallAuthorized: Boolean;

function CreateFileW(const FileName: String; DesiredAccess, ShareMode: LongWord; Security: Integer;
  Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function AcquireStartupGate(Directory: String): Boolean;
begin
  Result := True;
  if not FileExists(AddBackslash(Directory) + 'startup.lock') then Exit;
  if (StartupGate <> 0) and (StartupGate <> THandle(-1)) then Exit;
  { Allow deletion by our uninstaller, but no readers/writers while files change. }
  StartupGate := CreateFileW(AddBackslash(Directory) + 'startup.lock', $C0000000, 4, 0, 3, $80, 0);
  Result := StartupGate <> THandle(-1);
end;

procedure ReleaseStartupGate();
begin
  if (StartupGate <> 0) and (StartupGate <> THandle(-1)) then CloseHandle(StartupGate);
  StartupGate := 0;
end;

function GetFileAttributesW(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function Canonical(Path: String): String;
begin
  Result := Lowercase(RemoveBackslashUnlessRoot(ExpandFileName(Path)));
end;

function Within(Path, Parent: String): Boolean;
begin
  Path := Canonical(Path);
  Parent := Canonical(Parent);
  Result := (Path = Parent) or (Pos(AddBackslash(Parent), AddBackslash(Path)) = 1);
end;

function IsRedirect(Path: String): Boolean;
var Attributes: LongWord;
begin
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> $FFFFFFFF) and ((Attributes and ReparseAttribute) <> 0);
end;

function TreeProblem(Directory: String; Depth: Integer): String;
var
  Item: TFindRec;
  Path: String;
  Probe: TFileStream;
begin
  Result := '';
  if Depth > 32 then begin Result := 'The installation directory tree is unexpectedly deep.'; Exit; end;
  if IsRedirect(Directory) then begin Result := 'Installation paths must not contain junctions or symbolic links.'; Exit; end;
  if FindFirst(AddBackslash(Directory) + '*', Item) then
  try
    repeat
      if (Item.Name <> '.') and (Item.Name <> '..') then
      begin
        Path := AddBackslash(Directory) + Item.Name;
        if IsRedirect(Path) then begin Result := 'The installation contains a junction or symbolic link. No files were changed.'; Exit; end;
        if (Item.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then Result := TreeProblem(Path, Depth + 1)
        else if not ((Depth = 0) and (Pos('unins', Lowercase(Item.Name)) = 1)) then
        begin
          try
            Probe := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
            Probe.Free;
          except
            Result := 'Close all instances of this installed copy and release locked files before continuing. Check directory write permissions. The installer will not force-close applications.';
          end;
        end;
        if Result <> '' then Exit;
      end;
    until not FindNext(Item);
  finally
    FindClose(Item);
  end;
end;

function OwnedDirectory(Directory: String): Boolean;
begin
  Result := (GetIniString('Installation', 'AppId', '', AddBackslash(Directory) + MarkerName) = OwnedId)
    and (GetIniString('Installation', 'Mode', '', AddBackslash(Directory) + MarkerName) = '{#ModeName}')
    and (GetIniString('Installation', 'Schema', '', AddBackslash(Directory) + MarkerName) = '1');
end;

function DirectoryEmpty(Directory: String): Boolean;
var Item: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Directory) + '*', Item) then
  try
    repeat
      if (Item.Name <> '.') and (Item.Name <> '..') then begin Result := False; Exit; end;
    until not FindNext(Item);
  finally FindClose(Item); end;
end;

function DirectoryProblem(Directory: String; RequireOwned: Boolean): String;
var
  Ancestor, Parent, DataDir: String;
begin
  Result := '';
  DataDir := ExpandConstant('{userappdata}\FileLens');
  if Within(Directory, DataDir) or Within(DataDir, Directory) then begin Result := 'Installation must not overlap roaming application data.'; Exit; end;
  DataDir := ExpandConstant('{localappdata}\FileLens');
  if Within(Directory, DataDir) or Within(DataDir, Directory) then begin Result := 'Installation must not overlap local application data.'; Exit; end;
  Ancestor := Canonical(Directory);
  repeat
    if IsRedirect(Ancestor) then begin Result := 'Installation paths must not traverse junctions or symbolic links.'; Exit; end;
    if DirExists(AddBackslash(Ancestor) + '.git') or FileExists(AddBackslash(Ancestor) + '.git')
      or FileExists(AddBackslash(Ancestor) + 'publish.ps1') then
      begin Result := 'Installation inside a development checkout is not allowed.'; Exit; end;
    if (Canonical(Directory) <> Ancestor) and
       (FileExists(AddBackslash(Ancestor) + 'Fyltrix.exe') or FileExists(AddBackslash(Ancestor) + MarkerName)) then
      begin Result := 'Nested installation inside another Fyltrix copy is not allowed.'; Exit; end;
    Parent := ExtractFileDir(Ancestor);
    if Parent = Ancestor then Break;
    Ancestor := Parent;
  until Ancestor = '';
  if RequireOwned and not OwnedDirectory(Directory) then begin Result := 'The installation ownership marker is missing or does not match. No files were removed.'; Exit; end;
  if DirExists(Directory) and not DirectoryEmpty(Directory) and not OwnedDirectory(Directory) then
    begin Result := 'Choose an empty directory. Existing unowned files or a portable/development copy will not be overwritten.'; Exit; end;
  if DirExists(Directory) then Result := TreeProblem(Directory, 0);
end;

function ShortcutProblem(Path, Target: String): String;
var Shell, Link: Variant;
begin
  Result := '';
  if not FileExists(Path) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    if Canonical(Link.TargetPath) <> Canonical(Target) then Result := 'An existing shortcut belongs to another copy. It will not be replaced.';
  except Result := 'An existing shortcut could not be inspected. It will not be replaced.'; end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Directory, Registered, Link: String; MS, LS: Cardinal; Major, Minor, Patch: Integer;
begin
  ReleaseStartupGate();
  Directory := ExpandConstant('{app}');
  Result := DirectoryProblem(Directory, False);
  if Result <> '' then Exit;
  if FileExists(AddBackslash(Directory) + 'update-transaction.json') then begin Result := 'Recover the interrupted update before installing.'; Exit; end;
  Major := GetIniInt('Installation', 'VersionMajor', -1, 0, 65534, AddBackslash(Directory) + MarkerName);
  Minor := GetIniInt('Installation', 'VersionMinor', -1, 0, 65534, AddBackslash(Directory) + MarkerName);
  Patch := GetIniInt('Installation', 'VersionPatch', -1, 0, 65534, AddBackslash(Directory) + MarkerName);
  if (Major > {#VersionMajor}) or ((Major = {#VersionMajor}) and ((Minor > {#VersionMinor}) or ((Minor = {#VersionMinor}) and (Patch > {#VersionPatch})))) then
    begin Result := 'An older installer cannot replace the committed updated version.'; Exit; end;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + OwnedId + '_is1', 'InstallLocation', Registered)
    and (Canonical(Registered) <> Canonical(Directory)) then
    begin Result := 'This installer identity already uses another directory. Uninstall it first; relocation will not orphan the existing copy.'; Exit; end;
  if FileExists(AddBackslash(Directory) + 'Fyltrix.exe') then
  begin
    if not GetVersionNumbers(AddBackslash(Directory) + 'Fyltrix.exe', MS, LS) then
      begin Result := 'The installed executable version cannot be verified.'; Exit; end;
    if (MS > {#VersionMS}) or ((MS = {#VersionMS}) and (LS > {#VersionLS})) then
      begin Result := 'An older installer cannot replace a newer installed binary. Use the future verified rollback flow instead.'; Exit; end;
  end;
  Link := ExpandConstant('{userprograms}\{#FolderName}\{#DisplayName}.lnk');
  Result := ShortcutProblem(Link, AddBackslash(Directory) + 'Fyltrix.exe');
  if Result <> '' then Exit;
  if WizardIsTaskSelected('desktopicon') then
    Result := ShortcutProblem(ExpandConstant('{userdesktop}\{#DisplayName}.lnk'), AddBackslash(Directory) + 'Fyltrix.exe');
  if (Result = '') and not AcquireStartupGate(Directory) then Result := 'Close this installed copy before installing.';
end;

procedure DeinitializeSetup();
begin
  ReleaseStartupGate();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Marker: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Marker := ExpandConstant('{app}\') + MarkerName;
  if not OwnedDirectory(ExpandConstant('{app}')) then RaiseException('Installation ownership changed.');
  if not SetIniInt('Installation', 'VersionMajor', {#VersionMajor}, Marker) then RaiseException('Could not record installed version.');
  if not SetIniInt('Installation', 'VersionMinor', {#VersionMinor}, Marker) then RaiseException('Could not record installed version.');
  if not SetIniInt('Installation', 'VersionPatch', {#VersionPatch}, Marker) then RaiseException('Could not record installed version.');
end;

function LoadUpdateInventory(): Boolean;
var Inventory, Relative, Hash, Part, Rest: String; Count, I, J, N, Components: Integer;
begin
  Result := False;
  Inventory := ExpandConstant('{app}\update-files.ini');
  if not FileExists(Inventory) then begin Result := True; Exit; end;
  if GetIniString('UpdateFiles', 'Schema', '', Inventory) <> '1' then Exit;
  if GetIniString('UpdateFiles', 'Owner', '', Inventory) <> OwnedId then Exit;
  Count := GetIniInt('UpdateFiles', 'Count', -1, 0, 20000, Inventory);
  if (Count < 0) or (Count > 20000) then Exit;
  SetArrayLength(UpdatePaths, Count); SetArrayLength(UpdateHashes, Count);
  for I := 0 to Count - 1 do
  begin
    Relative := GetIniString('File' + IntToStr(I), 'Path', '', Inventory);
    Hash := GetIniString('File' + IntToStr(I), 'Hash', '', Inventory);
    if (Length(Relative) > 1024) or (Pos('app-', Relative) <> 1) or (Pos(':', Relative) > 0) or (Length(Hash) <> 64) then Exit;
    for J := 1 to Length(Hash) do if Pos(Lowercase(Hash[J]), '0123456789abcdef') = 0 then Exit;
    StringChangeEx(Relative, '/', '\', True);
    J := Pos('\', Relative); if J < 6 then Exit;
    Rest := Copy(Relative, 5, J - 5); Components := 0;
    repeat
      J := Pos('.', Rest);
      if J = 0 then begin Part := Rest; Rest := ''; end
      else begin Part := Copy(Rest, 1, J - 1); Delete(Rest, 1, J); end;
      N := StrToIntDef(Part, -1); if (N < 0) or (N > 65534) then Exit;
      Components := Components + 1;
    until Rest = '';
    if Components <> 3 then Exit;
    Rest := Relative;
    repeat
      J := Pos('\', Rest);
      if J = 0 then begin Part := Rest; Rest := ''; end
      else begin Part := Copy(Rest, 1, J - 1); Delete(Rest, 1, J); end;
      if (Part = '') or (Part = '.') or (Part = '..') then Exit;
    until Rest = '';
    UpdatePaths[I] := ExpandFileName(ExpandConstant('{app}\') + Relative);
    if not Within(UpdatePaths[I], ExpandConstant('{app}')) then Exit;
    UpdateHashes[I] := Lowercase(Hash);
  end;
  InventoryHash := GetSHA256OfFile(Inventory);
  Result := True;
end;

function InitializeUninstall(): Boolean;
var Problem: String;
begin
  Problem := DirectoryProblem(ExpandConstant('{app}'), True);
  if (Problem = '') and FileExists(ExpandConstant('{app}\update-transaction.json')) then Problem := 'Recover the interrupted update before uninstalling.';
  if (Problem = '') and not LoadUpdateInventory() then Problem := 'The owned update inventory is invalid. No files were removed.';
  if (Problem = '') and not AcquireStartupGate(ExpandConstant('{app}')) then Problem := 'Close this installed copy before uninstalling.';
  Result := Problem = '';
  UninstallAuthorized := Result;
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var I: Integer; Directory, Parent, Inventory: String; Safe: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Inventory := ExpandConstant('{app}\update-files.ini');
  if not FileExists(Inventory) then Exit;
  if (InventoryHash = '') or (GetSHA256OfFile(Inventory) <> InventoryHash) then Exit;
  for I := 0 to GetArrayLength(UpdatePaths) - 1 do
  begin
    Directory := UpdatePaths[I]; Safe := True;
    repeat
      if IsRedirect(Directory) then Safe := False;
      Parent := ExtractFileDir(Directory); if Parent = Directory then Break;
      Directory := Parent;
    until Canonical(Directory) = Canonical(ExpandConstant('{app}'));
    if Safe and FileExists(UpdatePaths[I]) and (Lowercase(GetSHA256OfFile(UpdatePaths[I])) = UpdateHashes[I]) then
    begin
      DeleteFile(UpdatePaths[I]);
      Directory := ExtractFileDir(UpdatePaths[I]);
      while Canonical(Directory) <> Canonical(ExpandConstant('{app}')) do
      begin
        if not RemoveDir(Directory) then Break;
        Parent := ExtractFileDir(Directory); if Parent = Directory then Break; Directory := Parent;
      end;
    end;
  end;
  DeleteFile(Inventory);
end;

procedure DeinitializeUninstall();
begin
  ReleaseStartupGate();
  if UninstallAuthorized then RemoveDir(ExpandConstant('{app}'));
end;
