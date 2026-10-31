; Compile through ../build-installer.ps1: it validates the payload and emits the
; exact file list. Never use a recursive wildcard in this mixed app/data folder.
#ifndef AppVersion
  #error AppVersion is required
#endif
#if VER < EncodeVer(6, 7, 3)
  #error Inno Setup 6.7.3 is required
#endif
#if VER >= EncodeVer(7, 0, 0)
  #error This script uses the 32-bit Inno Setup 6 Windows API ABI
#endif
[Setup]
#ifdef TestRoot
AppId={{AF20777F-58B9-45F7-8E55-200AC00ED0E7}
DefaultDirName={#TestRoot}
OutputBaseFilename=VibeRails-Fixture-{#AppVersion}-win-x64
#else
AppId={{E61B94C0-874A-437E-AAF5-B2909E225AE8}
DefaultDirName={code:UserProfile}\.vibe_rails
OutputBaseFilename=VibeRails-Setup-{#AppVersion}-win-x64
#endif
AppName=VibeRails
AppVersion={#AppVersion}
AppPublisher=VibeRails
AppPublisherURL=https://viberails.ai/
DisableDirPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DefaultGroupName=VibeRails
DisableProgramGroupPage=yes
OutputDir={#OutputDirectory}
SetupIconFile=..\..\docs\brand\viberails-mark.ico
UninstallDisplayIcon={app}\viberails.ico
UninstallFilesDir={app}\installer
UninstallDisplayName=VibeRails
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
ChangesEnvironment=yes
; User data is intentionally left in place, even when this is the last install.
DirExistsWarning=no
#ifdef ReleaseSigning
SignTool=trusted
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
#include FileEntries

[InstallDelete]
Type: files; Name: "{app}\models\bertv2\model.onnx.gz"
Type: files; Name: "{app}\models\bertv2\model.onnx.zip"
Type: files; Name: "{app}\models\bertv2\model.onnx.br"

[Icons]
#ifdef TestRoot
Name: "{userprograms}\VibeRails Installer Test"; Filename: "{app}\vb.exe"; Parameters: "--web"; WorkingDir: "{code:UserProfile}"; IconFilename: "{app}\viberails.ico"; Flags: runminimized
Name: "{userdesktop}\VibeRails Installer Test"; Filename: "{app}\vb.exe"; Parameters: "--web"; WorkingDir: "{code:UserProfile}"; IconFilename: "{app}\viberails.ico"; Tasks: desktopicon; Flags: runminimized
#else
Name: "{userprograms}\VibeRails"; Filename: "{app}\vb.exe"; Parameters: "--web"; WorkingDir: "{code:UserProfile}"; IconFilename: "{app}\viberails.ico"; Flags: runminimized
Name: "{userdesktop}\VibeRails"; Filename: "{app}\vb.exe"; Parameters: "--web"; WorkingDir: "{code:UserProfile}"; IconFilename: "{app}\viberails.ico"; Tasks: desktopicon; Flags: runminimized
#endif

[Run]
Filename: "{app}\vb.exe"; Parameters: "--web"; WorkingDir: "{code:UserProfile}"; Description: "Open VibeRails"; Flags: postinstall nowait skipifsilent runminimized unchecked

[Code]
const
#ifdef TestRoot
  PathOwnerKey = 'Software\VibeRails\InstallerFixture';
#else
  PathOwnerKey = 'Software\VibeRails\Installer';
#endif
  ReparsePoint = $400;
  InvalidAttributes = $FFFFFFFF;

type
  TTokenBuffer = array[0..255] of LongWord;
  TSecurityAttributes = record
    Length: LongWord;
    Descriptor: LongWord;
    InheritHandle: LongWord;
  end;

var
  PayloadPaths: TArrayOfString;
  ReplacePaths: TArrayOfString;
  InstallerMutex: THandle;

function ModelNeedsUpdate(Path, ExpectedHash: String): Boolean;
begin
  Path := ExpandConstant(Path);
  if not FileExists(Path) then
    Result := True
  else
    Result := CompareText(GetSHA256OfFile(Path), ExpectedHash) <> 0;
end;

function GetFileAttributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenFile(Name: String; Access, Share: LongWord; Security: LongWord;
  Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function NewMutex(Security: LongWord; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function GetCurrentProcess: THandle;
  external 'GetCurrentProcess@kernel32.dll stdcall';
function OpenProcessToken(Process: THandle; Access: LongWord; var Token: THandle): Boolean;
  external 'OpenProcessToken@advapi32.dll stdcall';
function GetTokenInformation(Token: THandle; InfoClass: Integer;
  var Buffer: TTokenBuffer; BufferSize: LongWord; var Required: LongWord): Boolean;
  external 'GetTokenInformation@advapi32.dll stdcall';
function GetNamedSecurityInfo(Name: String; ObjectType, SecurityInfo: LongWord;
  var Owner: LongWord; Group, Dacl, Sacl: LongWord; var Descriptor: LongWord): LongWord;
  external 'GetNamedSecurityInfoW@advapi32.dll stdcall';
function ConvertSidToStringSid(Sid: LongWord; var Text: LongWord): Boolean;
  external 'ConvertSidToStringSidW@advapi32.dll stdcall';
function StringLength(Text: LongWord): Integer;
  external 'lstrlenW@kernel32.dll stdcall';
function CopyString(Destination: String; Source: LongWord; Size: Integer): LongWord;
  external 'lstrcpynW@kernel32.dll stdcall';
function ConvertSecurityDescriptor(Text: String; Revision: LongWord;
  var Descriptor: LongWord; var Size: LongWord): Boolean;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall';
function CreateUserDirectory(Name: String; var Security: TSecurityAttributes): Boolean;
  external 'CreateDirectoryW@kernel32.dll stdcall';
function EqualSid(First, Second: LongWord): Boolean;
  external 'EqualSid@advapi32.dll stdcall';
function LocalFree(Memory: LongWord): LongWord;
  external 'LocalFree@kernel32.dll stdcall';
function ExpandEnv(Source, Destination: String; Size: LongWord): LongWord;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';
function ComparePathText(First: String; FirstLength: Integer; Second: String;
  SecondLength: Integer; IgnoreCase: Boolean): Integer;
  external 'CompareStringOrdinal@kernel32.dll stdcall';

function SamePath(const First, Second: String): Boolean;
begin
  { Windows handles Unicode path casing; Pascal CompareText is ASCII-only. }
  Result := ComparePathText(First, -1, Second, -1, True) = 2;
end;

function UserProfile(Param: String): String;
begin
  Result := GetShellFolderByCSIDL($0028, True);
  if Result = '' then RaiseException('Could not determine the current user profile directory.');
end;

procedure InitializePaths;
begin
#include PathEntries
end;

function LockInstaller: Boolean;
begin
  { The profile suffix separates different users; Global also covers their other
    login sessions. Both setup and uninstall hold the same mutex until exit. }
  InstallerMutex := NewMutex(0, False,
    'Global\VibeRailsInstaller-' + GetSHA256OfUnicodeString(ExpandConstant('{code:UserProfile}')));
  Result := (InstallerMutex <> 0) and (DLLGetLastError <> 183);
  if not Result then
    SuppressibleMsgBox('Another VibeRails installer or uninstaller is running. Wait for it to finish and retry.', mbError, MB_OK, IDOK);
end;

function OwnedByCurrentUser(const Path: String): Boolean;
var
  Token: THandle;
  Buffer: TTokenBuffer;
  Required: LongWord;
  Owner, Descriptor: LongWord;
begin
  Result := False;
  if not OpenProcessToken(GetCurrentProcess, 8, Token) then Exit;
  try
    if not GetTokenInformation(Token, 1, Buffer, SizeOf(Buffer), Required) then Exit;
    if GetNamedSecurityInfo(Path, 1, 1, Owner, 0, 0, 0, Descriptor) <> 0 then Exit;
    try
      { Inno Setup 6 is a 32-bit process, also in 64-bit install mode. The first
        DWORD of TOKEN_USER is its SID pointer into Buffer. }
      Result := EqualSid(Owner, Buffer[0]);
    finally
      LocalFree(Descriptor);
    end;
  finally
    CloseHandle(Token);
  end;
end;

procedure CreateUserOwnedInstallDirectory;
var
  Root: String;
  Token: THandle;
  Buffer: TTokenBuffer;
  Required, SidText, Descriptor: LongWord;
  OwnerSid: String;
  Security: TSecurityAttributes;
begin
  Root := ExpandConstant('{app}');
  { Existing folders must pass Preflight; never take ownership of one. An
    elevated process otherwise creates new folders owned by Administrators,
    which would make this per-user install fail its own upgrade/uninstall. }
  if DirExists(Root) then begin
    if ((GetFileAttributes(Root) and ReparsePoint) <> 0) or not OwnedByCurrentUser(Root) then
      RaiseException('The existing installation folder must belong to the current user and must not be a link.');
    Exit;
  end;
  if not OpenProcessToken(GetCurrentProcess, 8, Token) then
    RaiseException('Could not read the current user for the installation folder.');
  try
    if not GetTokenInformation(Token, 1, Buffer, SizeOf(Buffer), Required) then
      RaiseException('Could not read the current user for the installation folder.');
    if not ConvertSidToStringSid(Buffer[0], SidText) then
      RaiseException('Could not read the current user SID for the installation folder.');
    try
      OwnerSid := StringOfChar(#0, StringLength(SidText) + 1);
      CopyString(OwnerSid, SidText, Length(OwnerSid));
      SetLength(OwnerSid, Length(OwnerSid) - 1);
      if not ConvertSecurityDescriptor('O:' + OwnerSid, 1, Descriptor, Required) then
        RaiseException('Could not prepare current-user ownership for the installation folder.');
      try
        { Inno Setup 6 uses the 32-bit SECURITY_ATTRIBUTES ABI. Set the owner
          during creation; do not create then take ownership by pathname. A
          competing creation fails without modifying that existing object. }
        Security.Length := 12;
        Security.Descriptor := Descriptor;
        Security.InheritHandle := 0;
        if not CreateUserDirectory(Root, Security) then
          RaiseException('Could not create the current-user installation folder. Windows error: ' + IntToStr(DLLGetLastError));
      finally
        LocalFree(Descriptor);
      end;
    finally
      LocalFree(SidText);
    end;
  finally
    CloseHandle(Token);
  end;
end;

function ValidatePath(const Path: String): String;
var
  Current, Root: String;
  Attr: LongWord;
begin
  Result := '';
  Root := ExpandConstant('{app}');
  Current := Path;
  repeat
    Attr := GetFileAttributes(Current);
    if (Attr <> InvalidAttributes) and ((Attr and ReparsePoint) <> 0) then begin
      Result := 'Remove the symlink or junction at ' + Current + ' and retry.';
      Exit;
    end;
    if SamePath(Current, Root) then Exit;
    Current := ExtractFileDir(Current);
  until Length(Current) < Length(Root);
end;

function Preflight: String;
var
  I: Integer;
  Path, Root: String;
  Handle: THandle;
begin
  Root := ExpandConstant('{app}');
#ifdef TestRoot
  if not SamePath(Root, '{#TestRootPascal}') then begin
#else
  if not SamePath(Root, ExpandConstant('{code:UserProfile}\.vibe_rails')) then begin
#endif
    Result := 'The installer must use the fixed current-user installation directory.';
    Exit;
  end;
  Result := ValidatePath(Root);
  if Result <> '' then Exit;
  if FileExists(Root) then begin
    Result := 'The installation path is a file: ' + Root;
    Exit;
  end;
  if DirExists(Root) and not OwnedByCurrentUser(Root) then begin
    Result := 'The installation folder must be owned by the current user: ' + Root;
    Exit;
  end;
  for I := 0 to GetArrayLength(PayloadPaths) - 1 do begin
    Path := AddBackslash(Root) + PayloadPaths[I];
    Result := ValidatePath(Path);
    if Result <> '' then Exit;
    if DirExists(Path) then begin
      Result := 'An application file path is occupied by a folder: ' + Path;
      Exit;
    end;
  end;
  for I := 0 to GetArrayLength(ReplacePaths) - 1 do begin
    Path := AddBackslash(Root) + ReplacePaths[I];
    if FileExists(Path) then begin
      Handle := OpenFile(Path, $C0000000, 0, 0, 3, 0, 0);
      if Handle = THandle(-1) then begin
        Result := 'VibeRails files are in use or not writable: ' + Path + #13#10 +
          'Close the VibeRails windows or sessions using this installation, then retry. No application files have been changed.';
        Exit;
      end;
      CloseHandle(Handle);
    end;
  end;
  Result := ValidatePath(AddBackslash(Root) + 'installer\unins000.exe');
end;

function InitializeSetup: Boolean;
begin
  InitializePaths;
  Result := LockInstaller;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := Preflight;
end;

function NormalizePathEntry(Value: String): String;
var
  Expanded: String;
  Required: LongWord;
begin
  Result := RemoveBackslashUnlessRoot(RemoveQuotes(Trim(Value)));
  StringChangeEx(Result, '/', '\', True);
  { Expand environment references for comparison, while leaving their original
    spelling intact in the value we save. }
  Expanded := StringOfChar(#0, 32767);
  Required := ExpandEnv(Result, Expanded, Length(Expanded));
  if (Required = 0) or (Required > 32767) then Exit;
  SetLength(Expanded, Required - 1);
  Result := RemoveBackslashUnlessRoot(Expanded);
end;

function ContainsInstallPath(const Value: String): Boolean;
var
  Entries: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Entries := StringSplit(Value, [';'], stAll);
  for I := 0 to GetArrayLength(Entries) - 1 do
    if SamePath(NormalizePathEntry(Entries[I]), ExpandConstant('{app}')) then begin
      Result := True;
      Exit;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Value, NewValue: String;
begin
  if CurStep = ssInstall then CreateUserOwnedInstallDirectory;
  if CurStep <> ssPostInstall then Exit;
  RegQueryStringValue(HKCU, 'Environment', 'Path', Value);
  if not ContainsInstallPath(Value) then begin
    NewValue := Value;
    if (NewValue <> '') and (NewValue[Length(NewValue)] <> ';') then NewValue := NewValue + ';';
    NewValue := NewValue + ExpandConstant('{app}');
    { Save ownership first: a failed PATH write cannot claim an existing entry. }
    if not RegWriteStringValue(HKCU, PathOwnerKey, 'PathEntry', ExpandConstant('{app}')) then
      RaiseException('Could not record ownership of the VibeRails PATH entry.');
    if not RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewValue) then begin
      RegDeleteValue(HKCU, PathOwnerKey, 'PathEntry');
      RaiseException('Could not add VibeRails to the current user PATH.');
    end;
  end;
end;

function InitializeUninstall: Boolean;
var
  Problem: String;
begin
  InitializePaths;
  Result := LockInstaller;
  if not Result then Exit;
  repeat
    Problem := Preflight;
    Result := Problem = '';
    if Result then Exit;
  until SuppressibleMsgBox(Problem, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Owned, Value, NewValue: String;
  Entries: TArrayOfString;
  I: Integer;
  First: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if not RegQueryStringValue(HKCU, PathOwnerKey, 'PathEntry', Owned) then Exit;
  if not SamePath(Owned, ExpandConstant('{app}')) then Exit;
  if RegQueryStringValue(HKCU, 'Environment', 'Path', Value) then begin
    Entries := StringSplit(Value, [';'], stAll);
    NewValue := '';
    First := True;
    for I := 0 to GetArrayLength(Entries) - 1 do begin
      if SamePath(NormalizePathEntry(Entries[I]), Owned) then Continue;
      if not First then NewValue := NewValue + ';';
      NewValue := NewValue + Entries[I];
      First := False;
    end;
    if not RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewValue) then begin
      Log('Could not remove the installer-owned PATH entry; ownership retained.');
      Exit;
    end;
  end;
  RegDeleteValue(HKCU, PathOwnerKey, 'PathEntry');
  RegDeleteKeyIfEmpty(HKCU, PathOwnerKey);
end;

procedure DeinitializeSetup;
begin
  if InstallerMutex <> 0 then CloseHandle(InstallerMutex);
end;

procedure DeinitializeUninstall;
begin
  if InstallerMutex <> 0 then CloseHandle(InstallerMutex);
end;
