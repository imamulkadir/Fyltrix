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
var Directory, Registered, Link: String; MS, LS: Cardinal;
begin
  Directory := ExpandConstant('{app}');
  Result := DirectoryProblem(Directory, False);
  if Result <> '' then Exit;
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
end;

function InitializeUninstall(): Boolean;
var Problem: String;
begin
  Problem := DirectoryProblem(ExpandConstant('{app}'), True);
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;
