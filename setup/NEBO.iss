; NEBO setup: one wizard for the controller app, the Stream Deck plugin and the pilot hook.
; Built by package.ps1 from its package folders:
;   ISCC.exe /DVersion=0.18.0 /DSrc=<release folder> NEBO.iss
; Per-user install, no admin rights.
; The DCS server hook is not part of it (its own setup, handed to server admins). A setup from
; 0.16-0.17 that installed the server hook leaves it in place on the server; this one only drops
; its own copy and stops tracking it.
; Silent install: NEBO-Setup-<version>.exe /SILENT /COMPONENTS=client,pilot
;   [/PILOTDIRS=...] [/SERVERS="host:port;host:port"]   (folders separated by ;)

#ifndef Version
  #error Version is required: /DVersion=...
#endif
#ifndef Src
  #error Src is required: /DSrc=<release folder>
#endif
#define ClientPkg Src + "\NEBO-Client-" + Version
#define PilotPkg  Src + "\NEBO-Pilot-" + Version
#define DeckPkg   Src + "\NEBO-StreamDeck-" + Version

[Setup]
AppId={{8C1B6E0A-6F57-4C55-9C51-4E45424F0016}
AppName=NEBO ATC/GCI
AppVersion={#Version}
AppVerName=NEBO ATC/GCI {#Version}
AppPublisher=Kestrel's Nest Community
AppPublisherURL=https://kestrelsnest.co.ua/
AppSupportURL=https://discord.gg/FZGRqwc2Eg
AppUpdatesURL=https://github.com/kestrelsnest/nebo/releases
VersionInfoVersion={#Version}
DefaultDirName={localappdata}\Programs\NEBO
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#Src}
OutputBaseFilename=NEBO-Setup-{#Version}
SetupIconFile=..\client\Nebo.Client\Assets\nebo.ico
UninstallDisplayIcon={app}\nebo.ico
UninstallDisplayName=NEBO ATC/GCI
WizardStyle=modern dynamic
WizardImageFile=wizard.png,wizard@2x.png
WizardSmallImageFile=wizard-small.png,wizard-small@2x.png
; The dynamic style takes dark-mode images from their own directives (else Inno's defaults).
WizardImageFileDynamicDark=wizard.png,wizard@2x.png
WizardSmallImageFileDynamicDark=wizard-small.png,wizard-small@2x.png
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes

[Types]
Name: "custom"; Description: "Choose the parts"; Flags: iscustom

[Components]
Name: "client"; Description: "Controller app - GCI and ATC (needs the .NET 10 Desktop Runtime)"; Types: custom
Name: "deck";   Description: "Stream Deck plugin - for controllers with a Stream Deck"
Name: "pilot";  Description: "Pilot hook - data cartridges from your controller, targeting pod sharing"

[InstallDelete]
; 0.16-0.17 setups kept the server hook's files here; the server part has its own setup now.
Type: filesandordirs; Name: "{app}\server"
Type: files; Name: "{app}\notes\INSTALL.txt"

[Files]
Source: "..\client\Nebo.Client\Assets\nebo.ico"; DestDir: "{app}"
Source: "..\LICENSE"; DestDir: "{app}\notes"
Source: "..\LICENSE-EXCEPTION.md"; DestDir: "{app}\notes"
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\notes"
Source: "{#ClientPkg}\Nebo.Client.exe"; DestDir: "{app}"; Components: client
Source: "{#ClientPkg}\CLIENT.txt"; DestDir: "{app}\notes"; Components: client
Source: "{#DeckPkg}\com.kestrelsnest.nebo.streamDeckPlugin"; DestDir: "{app}\streamdeck"; Components: deck
Source: "{#DeckPkg}\NEBO.streamDeckProfile"; DestDir: "{app}\streamdeck"; Components: deck
Source: "{#DeckPkg}\STREAMDECK.txt"; DestDir: "{app}\notes"; Components: deck
Source: "{#PilotPkg}\NEBO-DTC.lua"; DestDir: "{app}\pilot"; Components: pilot
Source: "{#PilotPkg}\NEBO-Export.lua"; DestDir: "{app}\pilot"; Components: pilot
Source: "{#PilotPkg}\PILOT.txt"; DestDir: "{app}\notes"; Components: pilot

[Icons]
Name: "{autoprograms}\NEBO"; Filename: "{app}\Nebo.Client.exe"; Components: client

[Run]
Filename: "https://dotnet.microsoft.com/download/dotnet/10.0"; Description: "Get the .NET 10 Desktop Runtime from Microsoft (the NEBO app needs it)"; Flags: postinstall shellexec nowait skipifsilent; Components: client; Check: not HasDesktopRuntime
Filename: "{app}\Nebo.Client.exe"; Description: "Start NEBO"; Flags: postinstall nowait skipifsilent; Components: client; Check: HasDesktopRuntime
Filename: "{app}\streamdeck\com.kestrelsnest.nebo.streamDeckPlugin"; Description: "Install the plugin in the Stream Deck app"; Flags: postinstall shellexec nowait skipifsilent; Components: deck
Filename: "{app}\streamdeck\NEBO.streamDeckProfile"; Description: "Import the NEBO page (Stream Deck XL)"; Flags: postinstall shellexec nowait skipifsilent unchecked; Components: deck

[Code]
const
  DefaultPort = '10309';
  ExportLine = 'pcall(function() dofile(lfs.writedir()..[[Scripts\NEBO-Export.lua]]) end)';

var
  PilotDirs: TInputOptionWizardPage;
  ServersPage: TWizardPage;
  ServersMemo: TNewMemo;
  ServersFilled: Boolean;
  Failed: String;

{ ---- small helpers ---------------------------------------------------------------------- }

function HasDesktopRuntime: Boolean;
var F: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), F);
  if Result then FindClose(F);
end;

{ The text between the first pair of double quotes after Key in S ('' if none). }
function QuotedAfter(S, Key: String): String;
var p: Integer;
begin
  Result := '';
  p := Pos(Key, S);
  if p = 0 then exit;
  S := Copy(S, p + Length(Key), MaxInt);
  p := Pos('"', S);
  if p = 0 then exit;
  S := Copy(S, p + 1, MaxInt);
  p := Pos('"', S);
  if p > 0 then Result := Copy(S, 1, p - 1);
end;

{ The digits after Key and '=' in S ('' if none). }
function NumberAfter(S, Key: String): String;
var p, i: Integer;
begin
  Result := '';
  p := Pos(Key, S);
  if p = 0 then exit;
  i := p + Length(Key);
  while (i <= Length(S)) and ((S[i] = ' ') or (S[i] = '=')) do i := i + 1;
  while (i <= Length(S)) and (S[i] >= '0') and (S[i] <= '9') do begin
    Result := Result + S[i];
    i := i + 1;
  end;
end;

{ Pascal Script keeps an array's old contents, and LoadStringsFromFile adds to them: every file
  is read through here, into an emptied array. }
function Load(FileName: String; var Lines: TArrayOfString): Boolean;
begin
  SetArrayLength(Lines, 0);
  Result := FileExists(FileName) and LoadStringsFromFile(FileName, Lines);
end;

{ "host:port" lines from a pilot hook's SERVERS table (or an old one-line SERVER). }
function ReadServers(FileName: String): String;
var Lines: TArrayOfString; i: Integer; Host, Port: String;
begin
  Result := '';
  if not Load(FileName, Lines) then exit;
  for i := 0 to GetArrayLength(Lines) - 1 do
    if Pos('--', Trim(Lines[i])) <> 1 then begin
      Host := '';
      if Pos('=', Trim(Copy(Lines[i], Pos('host', Lines[i]) + 4, MaxInt))) = 1 then Host := QuotedAfter(Lines[i], 'host');
      if Host <> '' then begin
        Port := NumberAfter(Lines[i], 'port');
        if Port = '' then Port := DefaultPort;
        Result := Result + Host + ':' + Port + #13#10;
      end;
    end;
end;

procedure SplitServer(S: String; var Host, Port: String);
var i: Integer;
begin
  S := Trim(S);
  StringChangeEx(S, '"', '', True);
  StringChangeEx(S, '\', '', True);
  Host := S;
  Port := DefaultPort;
  for i := Length(S) downto 1 do
    if S[i] = ':' then begin
      Host := Trim(Copy(S, 1, i - 1));
      if StrToIntDef(Trim(Copy(S, i + 1, MaxInt)), 0) > 0 then Port := Trim(Copy(S, i + 1, MaxInt));
      break;
    end;
end;

procedure AddLine(var Lines: TArrayOfString; S: String);
var n: Integer;
begin
  n := GetArrayLength(Lines);
  SetArrayLength(Lines, n + 1);
  Lines[n] := S;
end;

{ Drops every line containing one of the texts (separated by |). True when one was dropped. }
function DropLines(var Lines: TArrayOfString; Texts: String): Boolean;
var Keep: TArrayOfString; i, j: Integer; Parts: TArrayOfString; Hit: Boolean;
begin
  Result := False;
  SetArrayLength(Keep, 0);
  Parts := StringSplit(Texts, ['|'], stExcludeEmpty);
  for i := 0 to GetArrayLength(Lines) - 1 do begin
    Hit := False;
    for j := 0 to GetArrayLength(Parts) - 1 do
      if Pos(Parts[j], Lines[i]) > 0 then Hit := True;
    if Hit then Result := True else AddLine(Keep, Lines[i]);
  end;
  Lines := Keep;
end;

procedure Save(FileName: String; Lines: TArrayOfString);
begin
  if not SaveStringsToUTF8FileWithoutBOM(FileName, Lines, False) then
    Failed := Failed + #13#10 + FileName;
end;

procedure CopyOne(Src, Dst: String);
begin
  ForceDirectories(ExtractFileDir(Dst));
  if not FileCopy(Src, Dst, False) then Failed := Failed + #13#10 + Dst;
end;

function Checked(Page: TInputOptionWizardPage): TArrayOfString;
var i: Integer;
begin
  SetArrayLength(Result, 0);
  for i := 0 to Page.CheckListBox.Items.Count - 1 do
    if Page.Values[i] then AddLine(Result, Page.CheckListBox.Items[i]);
end;

{ ---- the pages ------------------------------------------------------------------------- }

{ Saved Games folders of DCS (a DCS* folder holding Config); all but server ones (name with
  "server") ticked. /PILOTDIRS= replaces the list. }
procedure FillDcsFolders(Page: TInputOptionWizardPage);
var F: TFindRec; Root, Given: String; Dirs: TArrayOfString; i, k: Integer;
begin
  Given := ExpandConstant('{param:PILOTDIRS|}');
  if Given <> '' then begin
    Dirs := StringSplit(Given, [';'], stExcludeEmpty);
    for i := 0 to GetArrayLength(Dirs) - 1 do Page.Values[Page.Add(Trim(Dirs[i]))] := True;
    exit;
  end;
  Root := ExpandConstant('{usersavedgames}');
  if FindFirst(Root + '\DCS*', F) then
  try
    repeat
      if (F.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and DirExists(Root + '\' + F.Name + '\Config') then begin
        k := Page.Add(Root + '\' + F.Name);
        Page.Values[k] := Pos('server', Lowercase(F.Name)) = 0;
      end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

procedure BrowsePilot(Sender: TObject);
var Dir: String;
begin
  Dir := ExpandConstant('{usersavedgames}');
  if BrowseForFolder('The Saved Games folder of a DCS install (it holds Config, Logs and Missions):', Dir, False) then
    PilotDirs.Values[PilotDirs.Add(Dir)] := True;
end;

procedure AddBrowseButton(Page: TInputOptionWizardPage; Handler: TNotifyEvent);
var B: TNewButton;
begin
  Page.CheckListBox.Height := Page.CheckListBox.Height - ScaleY(32);
  B := TNewButton.Create(Page);
  B.Parent := Page.Surface;
  B.Caption := 'Add a folder...';
  B.Left := 0;
  B.Top := Page.CheckListBox.Top + Page.CheckListBox.Height + ScaleY(8);
  B.Width := ScaleX(120);
  B.Height := ScaleY(24);
  B.OnClick := Handler;
end;

procedure InitializeWizard;
var L: TNewStaticText;
begin
  PilotDirs := CreateInputOptionPage(wpSelectComponents, 'Pilot hook: which DCS',
    'The DCS installs you fly with.', 'The hook goes into Scripts\Hooks of each ticked Saved Games folder, the targeting pod script into Scripts (with its line in Export.lua). Old Overseer and Eagle''s Nest hooks there are replaced.', False, False);
  FillDcsFolders(PilotDirs);
  AddBrowseButton(PilotDirs, @BrowsePilot);

  ServersPage := CreateCustomPage(PilotDirs.ID, 'Pilot hook: your NEBO servers',
    'The servers whose controllers may send you data cartridges.');
  L := TNewStaticText.Create(ServersPage);
  L.Parent := ServersPage.Surface;
  L.Caption := 'One server per line: its address and NEBO port, e.g.  kestrel.example.net:10309' + #13#10 +
    'The hook finds the one you fly on by itself. Ask your server admin for the line.';
  L.AutoSize := True;
  L.WordWrap := True;
  L.Width := ServersPage.SurfaceWidth;
  ServersMemo := TNewMemo.Create(ServersPage);
  ServersMemo.Parent := ServersPage.Surface;
  ServersMemo.Top := L.Top + ScaleY(40);
  ServersMemo.Width := ServersPage.SurfaceWidth;
  ServersMemo.Height := ServersPage.SurfaceHeight - ServersMemo.Top;
  ServersMemo.ScrollBars := ssVertical;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = PilotDirs.ID) or (PageID = ServersPage.ID)) and not WizardIsComponentSelected('pilot');
end;

{ The server list starts from the hook already installed (NEBO, Overseer or Eagle's Nest) in the
  first ticked folder, or /SERVERS=. }
procedure FillServers;
var Dirs, Given: TArrayOfString; i: Integer; Hooks: String;
begin
  if ServersFilled then exit;
  ServersFilled := True;
  if ExpandConstant('{param:SERVERS|}') <> '' then begin
    Given := StringSplit(ExpandConstant('{param:SERVERS|}'), [';'], stExcludeEmpty);
    for i := 0 to GetArrayLength(Given) - 1 do ServersMemo.Lines.Add(Trim(Given[i]));
    exit;
  end;
  Dirs := Checked(PilotDirs);
  for i := 0 to GetArrayLength(Dirs) - 1 do begin
    Hooks := Dirs[i] + '\Scripts\Hooks\';
    ServersMemo.Text := ReadServers(Hooks + 'NEBO-DTC.lua');
    if Trim(ServersMemo.Text) = '' then ServersMemo.Text := ReadServers(Hooks + 'Overseer-DTC.lua');
    if Trim(ServersMemo.Text) = '' then ServersMemo.Text := ReadServers(Hooks + 'EaglesNest-DTC.lua');
    if Trim(ServersMemo.Text) <> '' then exit;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = ServersPage.ID then FillServers;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = PilotDirs.ID) and (GetArrayLength(Checked(PilotDirs)) = 0) then begin
    MsgBox('Tick at least one folder, or go back and untick this part.', mbError, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var Dirs: TArrayOfString; i: Integer;
begin
  Result := MemoComponentsInfo;
  if WizardIsComponentSelected('pilot') then begin
    Result := Result + NewLine + NewLine + 'Pilot hook into:';
    Dirs := Checked(PilotDirs);
    for i := 0 to GetArrayLength(Dirs) - 1 do Result := Result + NewLine + Space + Dirs[i];
    Result := Result + NewLine + 'Servers:';
    for i := 0 to ServersMemo.Lines.Count - 1 do
      if Trim(ServersMemo.Lines[i]) <> '' then Result := Result + NewLine + Space + Trim(ServersMemo.Lines[i]);
  end;
end;

{ ---- installing ------------------------------------------------------------------------ }

procedure InstallPilot(Dir: String);
var Lines, Hook: TArrayOfString; i, Start, Stop: Integer; Host, Port: String;
begin
  { Old names: the servers were read on the servers page; their files go. }
  DeleteFile(Dir + '\Scripts\Hooks\Overseer-DTC.lua');
  DeleteFile(Dir + '\Scripts\Hooks\EaglesNest-DTC.lua');
  DeleteFile(Dir + '\Scripts\Overseer-Export.lua');
  DeleteFile(Dir + '\Scripts\EaglesNest-Export.lua');

  { The hook with this setup's server list in its SERVERS table. }
  if not Load(ExpandConstant('{app}\pilot\NEBO-DTC.lua'), Lines) then begin
    Failed := Failed + #13#10 + 'NEBO-DTC.lua';
    exit;
  end;
  SetArrayLength(Hook, 0);
  Start := -1; Stop := -1;
  for i := 0 to GetArrayLength(Lines) - 1 do
    if (Start < 0) and (Pos('local SERVERS = {', Lines[i]) = 1) then Start := i
    else if (Start >= 0) and (Stop < 0) and (Trim(Lines[i]) = '}') then Stop := i;
  for i := 0 to Start do AddLine(Hook, Lines[i]);
  for i := 0 to ServersMemo.Lines.Count - 1 do
    if Trim(ServersMemo.Lines[i]) <> '' then begin
      SplitServer(ServersMemo.Lines[i], Host, Port);
      if Host <> '' then AddLine(Hook, '    { host = "' + Host + '", port = ' + Port + ' },');
    end;
  if GetArrayLength(Hook) = Start + 1 then AddLine(Hook, '    { host = "", port = ' + DefaultPort + ' },');
  for i := Stop to GetArrayLength(Lines) - 1 do AddLine(Hook, Lines[i]);
  ForceDirectories(Dir + '\Scripts\Hooks');
  Save(Dir + '\Scripts\Hooks\NEBO-DTC.lua', Hook);

  { Targeting pod sharing: the export script and its one line in Export.lua. }
  CopyOne(ExpandConstant('{app}\pilot\NEBO-Export.lua'), Dir + '\Scripts\NEBO-Export.lua');
  Load(Dir + '\Scripts\Export.lua', Lines);
  DropLines(Lines, 'Overseer-Export.lua|EaglesNest-Export.lua|NEBO-Export.lua');
  AddLine(Lines, ExportLine);
  Save(Dir + '\Scripts\Export.lua', Lines);
end;

{ Where the pilot hooks went, so the uninstaller removes them: "pilot|<dir>" lines. Server lines
  from 0.16-0.17 setups are dropped: the server hook stays and its own setup looks after it. }
procedure Remember(Dirs: TArrayOfString);
var Lines: TArrayOfString; i, j: Integer; Known: Boolean; FileName: String;
begin
  FileName := ExpandConstant('{app}\targets.txt');
  Load(FileName, Lines);
  DropLines(Lines, 'server|');
  for i := 0 to GetArrayLength(Dirs) - 1 do begin
    Known := False;
    for j := 0 to GetArrayLength(Lines) - 1 do
      if CompareText(Lines[j], 'pilot|' + Dirs[i]) = 0 then Known := True;
    if not Known then AddLine(Lines, 'pilot|' + Dirs[i]);
  end;
  Save(FileName, Lines);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Dirs: TArrayOfString; i: Integer;
begin
  if CurStep <> ssPostInstall then exit;
  SetArrayLength(Dirs, 0);
  if WizardIsComponentSelected('pilot') then begin
    if WizardSilent then FillServers;
    Dirs := Checked(PilotDirs);
    for i := 0 to GetArrayLength(Dirs) - 1 do InstallPilot(Dirs[i]);
  end;
  Remember(Dirs);
  if Failed <> '' then
    SuppressibleMsgBox('These could not be written (a running DCS keeps its files locked: close it and run the setup again):' + Failed,
      mbError, MB_OK, IDOK);
end;

{ ---- uninstalling: only what the setup put there; never cartridges or settings ---- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Targets, Lines: TArrayOfString; i: Integer; Dir: String;
begin
  if (CurUninstallStep <> usUninstall) or not Load(ExpandConstant('{app}\targets.txt'), Targets) then exit;
  for i := 0 to GetArrayLength(Targets) - 1 do
    if Pos('pilot|', Targets[i]) = 1 then begin
      Dir := Copy(Targets[i], Pos('|', Targets[i]) + 1, MaxInt);
      DeleteFile(Dir + '\Scripts\Hooks\NEBO-DTC.lua');
      DeleteFile(Dir + '\Scripts\NEBO-Export.lua');
      if Load(Dir + '\Scripts\Export.lua', Lines) and DropLines(Lines, 'NEBO-Export.lua') then
        SaveStringsToUTF8FileWithoutBOM(Dir + '\Scripts\Export.lua', Lines, False);
    end;
  DeleteFile(ExpandConstant('{app}\targets.txt'));
end;
