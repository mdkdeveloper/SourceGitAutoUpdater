#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef RuntimeIdentifier
  #error RuntimeIdentifier is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{38547CC2-FB09-4F2D-AB4C-CF51E7CE0237}
AppName=SourceGitAutoUpdater
AppVersion={#AppVersion}
AppPublisher=mdkdeveloper
AppPublisherURL=https://github.com/mdkdeveloper/SourceGitAutoUpdater
AppSupportURL=https://github.com/mdkdeveloper/SourceGitAutoUpdater/issues
DefaultDirName={localappdata}\Programs\SourceGitAutoUpdater
DefaultGroupName=SourceGitAutoUpdater
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
#if RuntimeIdentifier == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=SourceGitAutoUpdater_{#AppVersion}_{#RuntimeIdentifier}_setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\SourceGitAutoUpdater\App.ico
UninstallDisplayIcon={app}\SourceGitAutoUpdater.exe
CloseApplications=yes
RestartApplications=no
LicenseFile={#PackageDir}\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Excludes: "SourceGit\*,SourceGit.exe,SourceGit.dll,SourceGit.deps.json,SourceGit.runtimeconfig.json,sourcegit*.zip,sourcegit-managed-files.txt"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SourceGitAutoUpdater"; Filename: "{app}\SourceGitAutoUpdater.exe"; WorkingDir: "{app}"; AppUserModelID: "SourceGitAutoUpdater.SourceGit"; Check: not WizardNoIcons

[Run]
Filename: "{app}\SourceGitAutoUpdater.exe"; Description: "Launch SourceGitAutoUpdater"; Flags: nowait postinstall skipifsilent

[Code]
function GetFileAttributesW(const FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsSafeManagedFile(const RelativeName: String): Boolean;
var
  Relative, Root, Current, Part: String;
  Separator, Attributes: Cardinal;
begin
  Result := False;
  Relative := RelativeName;
  StringChangeEx(Relative, '/', '\', True);
  if (Relative = '') or (Copy(Relative, 1, 1) = '\') or (Pos(':', Relative) > 0) then Exit;
  if (Pos('\..\', '\' + Relative + '\') > 0) or (Pos('\.\', '\' + Relative + '\') > 0) then Exit;
  if (CompareText(Relative, 'data') = 0) or (CompareText(Copy(Relative, 1, 5), 'data\') = 0) then Exit;
  Root := ExpandConstant('{app}\SourceGit');
  Attributes := GetFileAttributesW(Root);
  if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then Exit;
  Current := Root;
  while Relative <> '' do
  begin
    Separator := Pos('\', Relative);
    if Separator = 0 then
    begin
      Part := Relative;
      Relative := '';
    end
    else
    begin
      Part := Copy(Relative, 1, Separator - 1);
      Delete(Relative, 1, Separator);
    end;
    if (Part = '') or (Copy(Part, Length(Part), 1) = '.') or (Copy(Part, Length(Part), 1) = ' ') then Exit;
    Current := Current + '\' + Part;
    Attributes := GetFileAttributesW(Current);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then Exit;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Files: TArrayOfString;
  Root, Manifest, RelativeName, FileName, ParentDir: String;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Root := ExpandConstant('{app}\SourceGit');
  Manifest := Root + '\sourcegit-managed-files.txt';
  if not IsSafeManagedFile('sourcegit-managed-files.txt') then Exit;
  if not LoadStringsFromFile(Manifest, Files) then Exit;
  for I := 0 to GetArrayLength(Files) - 1 do
  begin
    RelativeName := Trim(Files[I]);
    if IsSafeManagedFile(RelativeName) then
    begin
      StringChangeEx(RelativeName, '/', '\', True);
      FileName := Root + '\' + RelativeName;
      if FileExists(FileName) and not DeleteFile(FileName) then
        Log('Preserving busy managed file: ' + FileName);
      ParentDir := ExtractFileDir(FileName);
      while (CompareText(ParentDir, Root) <> 0) and (Length(ParentDir) > Length(Root)) do
      begin
        if not RemoveDir(ParentDir) then Break;
        ParentDir := ExtractFileDir(ParentDir);
      end;
    end
    else
      Log('Ignoring unsafe managed-file manifest entry.');
  end;
  DeleteFile(Manifest);
  RemoveDir(Root);
end;





