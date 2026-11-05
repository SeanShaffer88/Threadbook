#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef LicensePath
  #error LicensePath is required
#endif
[Setup]
AppId={{8AE96828-BF76-4AAD-A689-72CB0299D4D5}
AppName=Threadbook
AppVersion={#AppVersion}
AppVerName=Threadbook {#AppVersion}
VersionInfoVersion=0.2.0.2
DefaultDirName={localappdata}\Programs\Threadbook
DefaultGroupName=Threadbook
PrivilegesRequired=lowest
DisableDirPage=no
DisableWelcomePage=no
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
RestartApplications=no
Uninstallable=yes
OutputBaseFilename=Threadbook-v{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\desktop\Assets\Threadbook.ico
LicenseFile={#LicensePath}
[Messages]
WelcomeLabel2=This installs Threadbook for your current Windows user.%n%nRequired: Microsoft .NET 10 Desktop Runtime x64. Install it manually from https://dotnet.microsoft.com/en-us/download/dotnet/10.0 before opening Threadbook.%n%nThe runtime is not included or installed by this setup. Your notebooks are not removed on uninstall.
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
[Files]
Source: "{#PayloadDir}\Threadbook.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Threadbook.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Threadbook.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Threadbook.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Threadbook.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Threadbook.Storage.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#LicensePath}"; DestDir: "{app}"; DestName: "LICENSE"; Flags: ignoreversion
[Icons]
Name: "{userprograms}\Threadbook"; Filename: "{app}\Threadbook.exe"
Name: "{userdesktop}\Threadbook"; Filename: "{app}\Threadbook.exe"; Tasks: desktopicon
[Code]
function HasUnexpectedEntries: Boolean;
var
  Entry: TFindRec;
  Name: String;
begin
  Result := False;
  if FindFirst(ExpandConstant('{app}\*'), Entry) then begin
    try
      repeat
        Name := Lowercase(Entry.Name);
        if (Name <> '.') and (Name <> '..') and
           (Name <> 'threadbook.exe') and (Name <> 'threadbook.dll') and
           (Name <> 'threadbook.deps.json') and (Name <> 'threadbook.runtimeconfig.json') and
           (Name <> 'threadbook.core.dll') and (Name <> 'threadbook.storage.dll') and
           (Name <> 'license') and (Name <> 'unins000.exe') and
           (Name <> 'unins000.dat') and (Name <> 'unins000.msg') then begin
          Result := True;
          Exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;
function UnsafeTarget: Boolean;
begin
  Result := HasUnexpectedEntries or FileExists(ExpandConstant('{app}\portable.flag')) or
    DirExists(ExpandConstant('{app}\portable.flag')) or
    DirExists(ExpandConstant('{app}\Data')) or
    FileExists(ExpandConstant('{app}\threadbook.json')) or
    FileExists(ExpandConstant('{app}\vault-header.json')) or
    FileExists(ExpandConstant('{app}\.threadbook.lock'));
end;
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and UnsafeTarget then begin
    MsgBox('Choose an application folder without portable data or notebooks. No notebook will be moved or removed.', mbError, MB_OK);
    Result := False;
  end;
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if UnsafeTarget then Result := 'The selected application folder contains portable data or a notebook. Choose another folder.';
end;
