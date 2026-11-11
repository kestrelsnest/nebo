; NEBO setup: one wizard for every part - the controller app, the Stream Deck plugin, the pilot
; hook and the DCS server hook. Built by server\package.ps1 from its package folders:
;   ISCC.exe /DVersion=0.16.0 /DSrc=<release folder> NEBO.iss
; Per-user install. Admin rights are asked for only to open the firewall port (server hook).
; Silent install (admins): NEBO-Setup-<version>.exe /SILENT /COMPONENTS=server
;   [/SERVERDIRS="C:\Users\me\Saved Games\DCS.server"] [/PORT=10309] [/FIREWALL=1]
;   [/ENEMY_GROUND=auto|true|false] [/IDENTIFICATION=auto|manual] [/RADAR_SCAN=true|false]
;   [/ALLOW_FLYING_CONTROLLERS=false|true] [/DTC=true|false]   (settings left out keep NEBO.lua's)
;   [/PILOTDIRS=...] [/SERVERS="host:port;host:port"]   (folders separated by ;)

#ifndef Version
  #error Version is required: /DVersion=...
#endif
#ifndef Src
  #error Src is required: /DSrc=<release folder>
#endif
#define ServerPkg Src + "\NEBO-Server-" + Version
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
SetupIconFile=..\..\client\Nebo.Client\Assets\nebo.ico
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
Name: "server"; Description: "DCS server hook - for server admins"

[Files]
Source: "..\..\client\Nebo.Client\Assets\nebo.ico"; DestDir: "{app}"
Source: "..\..\LICENSE"; DestDir: "{app}\notes"
Source: "..\..\LICENSE-EXCEPTION.md"; DestDir: "{app}\notes"
Source: "..\..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\notes"
Source: "{#ClientPkg}\Nebo.Client.exe"; DestDir: "{app}"; Components: client
Source: "{#ClientPkg}\CLIENT.txt"; DestDir: "{app}\notes"; Components: client
Source: "{#DeckPkg}\com.kestrelsnest.nebo.streamDeckPlugin"; DestDir: "{app}\streamdeck"; Components: deck
Source: "{#DeckPkg}\NEBO.streamDeckProfile"; DestDir: "{app}\streamdeck"; Components: deck
Source: "{#DeckPkg}\STREAMDECK.txt"; DestDir: "{app}\notes"; Components: deck
Source: "{#PilotPkg}\NEBO-DTC.lua"; DestDir: "{app}\pilot"; Components: pilot
Source: "{#PilotPkg}\NEBO-Export.lua"; DestDir: "{app}\pilot"; Components: pilot
Source: "{#PilotPkg}\PILOT.txt"; DestDir: "{app}\notes"; Components: pilot
Source: "{#ServerPkg}\copy-into-saved-games\*"; DestDir: "{app}\server"; Flags: recursesubdirs; Components: server
Source: "{#ServerPkg}\INSTALL.txt"; DestDir: "{app}\notes"; Components: server
Source: "options\*.png"; Flags: dontcopy

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
  OptCount = 5;  { server settings pages; key number OptCount is the port }

var
  PilotDirs, ServerDirs: TInputOptionWizardPage;
  ServersPage: TWizardPage;
  ServersMemo: TNewMemo;
  ServersFilled, SettingsFilled: Boolean;
  OptKeys: array[0..4] of String;
  OptPages: array[0..4] of TWizardPage;
  Radios: array of TNewRadioButton;
  RadioOpt: array of Integer;
  RadioVal: array of String;
  ServerOpts: TInputQueryWizardPage;
  FirewallBox: TNewCheckBox;
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

procedure CopyTree(Src, Dst: String);
var F: TFindRec;
begin
  if FindFirst(Src + '\*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') then
        if F.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          CopyTree(Src + '\' + F.Name, Dst + '\' + F.Name)
        else
          CopyOne(Src + '\' + F.Name, Dst + '\' + F.Name);
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

{ Deletes from Dst every file Src has, then the folders left empty (never anything else). }
procedure DeleteTree(Src, Dst: String; Top: Boolean);
var F: TFindRec;
begin
  if FindFirst(Src + '\*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') then
        if F.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          DeleteTree(Src + '\' + F.Name, Dst + '\' + F.Name, False)
        else
          DeleteFile(Dst + '\' + F.Name);
    until not FindNext(F);
  finally
    FindClose(F);
  end;
  if not Top then RemoveDir(Dst);
end;

function Checked(Page: TInputOptionWizardPage): TArrayOfString;
var i: Integer;
begin
  SetArrayLength(Result, 0);
  for i := 0 to Page.CheckListBox.Items.Count - 1 do
    if Page.Values[i] then AddLine(Result, Page.CheckListBox.Items[i]);
end;

{ ---- the pages ------------------------------------------------------------------------- }

{ Saved Games folders of DCS (a DCS* folder holding Config); server ones (name with "server")
  ticked for the server hook, the others for the pilot hook. /PILOTDIRS= or /SERVERDIRS= replace
  the list. }
procedure FillDcsFolders(Page: TInputOptionWizardPage; ForServer: Boolean; Param: String);
var F: TFindRec; Root, Given: String; Dirs: TArrayOfString; i, k: Integer;
begin
  Given := ExpandConstant('{param:' + Param + '|}');
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
        Page.Values[k] := (Pos('server', Lowercase(F.Name)) > 0) = ForServer;
      end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

procedure Browse(Page: TInputOptionWizardPage);
var Dir: String;
begin
  Dir := ExpandConstant('{usersavedgames}');
  if BrowseForFolder('The Saved Games folder of a DCS install (it holds Config, Logs and Missions):', Dir, False) then
    Page.Values[Page.Add(Dir)] := True;
end;

procedure BrowsePilot(Sender: TObject); begin Browse(PilotDirs); end;
procedure BrowseServer(Sender: TObject); begin Browse(ServerDirs); end;

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

{ ---- server settings pages: two pictures side by side, the choices under each ---------------- }

function AddRadio(Page: TWizardPage; Opt: Integer; Value, Caption: String; X, Y, W: Integer): Integer;
begin
  Result := GetArrayLength(Radios);
  SetArrayLength(Radios, Result + 1);
  SetArrayLength(RadioOpt, Result + 1);
  SetArrayLength(RadioVal, Result + 1);
  Radios[Result] := TNewRadioButton.Create(Page);
  Radios[Result].Parent := Page.Surface;
  Radios[Result].Left := X;
  Radios[Result].Top := Y;
  Radios[Result].Width := W;
  Radios[Result].Height := ScaleY(18);
  Radios[Result].Caption := Caption;
  RadioOpt[Result] := Opt;
  RadioVal[Result] := Value;
end;

{ A click on a picture picks its (first) choice. }
procedure PictureClick(Sender: TObject);
begin
  Radios[TBitmapImage(Sender).Tag].Checked := True;
end;

{ Choices: 'value=Caption|value=Caption' (values as written in NEBO.lua); the first left one is
  the default. Pictures: options\<name>.png, drawn by branding\make-options.js. }
procedure MakeOptPage(Opt, After: Integer; Key, Title, Sub, Note, LeftPic, RightPic, LeftChoices, RightChoices: String);
var Page: TWizardPage; Img: TBitmapImage; L: TNewStaticText; Choices: TArrayOfString;
    W, Gap, PW, PH, Side, k, Eq, Idx: Integer; Pic: String;
begin
  Page := CreateCustomPage(After, Title, Sub);
  OptPages[Opt] := Page;
  OptKeys[Opt] := Key;
  W := Page.SurfaceWidth;
  Gap := ScaleX(12);
  PW := (W - Gap) div 2;
  PH := PW * 13 div 20;
  for Side := 0 to 1 do begin
    if Side = 0 then Pic := LeftPic else Pic := RightPic;
    ExtractTemporaryFile(Pic + '.png');
    Img := TBitmapImage.Create(Page);
    Img.Parent := Page.Surface;
    Img.Left := Side * (PW + Gap);
    Img.Top := 0;
    Img.Width := PW;
    Img.Height := PH;
    Img.Stretch := True;
    Img.PngImage.LoadFromFile(ExpandConstant('{tmp}\') + Pic + '.png');
    Img.Cursor := crHand;
    Img.OnClick := @PictureClick;
    if Side = 0 then Choices := StringSplit(LeftChoices, ['|'], stExcludeEmpty)
    else Choices := StringSplit(RightChoices, ['|'], stExcludeEmpty);
    for k := 0 to GetArrayLength(Choices) - 1 do begin
      Eq := Pos('=', Choices[k]);
      Idx := AddRadio(Page, Opt, Copy(Choices[k], 1, Eq - 1), Copy(Choices[k], Eq + 1, MaxInt),
        Img.Left, PH + ScaleY(6) + k * ScaleY(20), PW);
      if k = 0 then Img.Tag := Idx;
      if (Side = 0) and (k = 0) then Radios[Idx].Checked := True;
    end;
  end;
  L := TNewStaticText.Create(Page);
  L.Parent := Page.Surface;
  L.Top := PH + ScaleY(50);
  L.Width := W;
  L.AutoSize := False;
  L.WordWrap := True;
  L.Height := Page.SurfaceHeight - L.Top;
  L.Font.Color := clGrayText;
  L.Caption := Note;
end;

function OptValue(Opt: Integer): String;
var i: Integer;
begin
  Result := '';
  for i := 0 to GetArrayLength(Radios) - 1 do
    if (RadioOpt[i] = Opt) and Radios[i].Checked then Result := RadioVal[i];
end;

{ Picks the choice with this value (auto and "auto" alike); an unknown value changes nothing. }
procedure SetOpt(Opt: Integer; Value: String);
var i: Integer; Known: Boolean;
begin
  StringChangeEx(Value, ' ', '', True);
  if (Value <> 'true') and (Value <> 'false') and (Pos('"', Value) = 0) then Value := '"' + Value + '"';
  Known := False;
  for i := 0 to GetArrayLength(Radios) - 1 do
    if (RadioOpt[i] = Opt) and (RadioVal[i] = Value) then Known := True;
  if Known then
    for i := 0 to GetArrayLength(Radios) - 1 do
      if RadioOpt[i] = Opt then Radios[i].Checked := RadioVal[i] = Value;
end;

function KeyName(k: Integer): String;
begin
  if k = OptCount then Result := 'port' else Result := OptKeys[k];
end;

function KeyValue(k: Integer): String;
begin
  if k = OptCount then Result := Trim(ServerOpts.Values[0]) else Result := OptValue(k);
end;

{ The value of a "key = value  -- comment" line ('' when the line sets another key). }
function ValueOf(Line, Key: String): String;
var p: Integer;
begin
  Result := '';
  Line := Trim(Line);
  if Pos(Key, Line) <> 1 then exit;
  Line := Trim(Copy(Line, Length(Key) + 1, MaxInt));
  if Pos('=', Line) <> 1 then exit;
  Line := Copy(Line, 2, MaxInt);
  p := Pos('--', Line);
  if p > 0 then Line := Copy(Line, 1, p - 1);
  Result := Trim(Line);
end;

{ The line with its value replaced, spacing and comment kept. }
function WithValue(Line, Value: String): String;
var i, j: Integer;
begin
  i := Pos('=', Line) + 1;
  while (i <= Length(Line)) and (Line[i] = ' ') do i := i + 1;
  j := i;
  while (j <= Length(Line)) and (Line[j] <> ' ') and (Line[j] <> #9) do j := j + 1;
  Result := Copy(Line, 1, i - 1) + Value + Copy(Line, j, MaxInt);
end;

procedure InitializeWizard;
var L: TNewStaticText;
begin
  PilotDirs := CreateInputOptionPage(wpSelectComponents, 'Pilot hook: which DCS',
    'The DCS installs you fly with.', 'The hook goes into Scripts\Hooks of each ticked Saved Games folder, the targeting pod script into Scripts (with its line in Export.lua). Old Overseer and Eagle''s Nest hooks there are replaced.', False, False);
  FillDcsFolders(PilotDirs, False, 'PILOTDIRS');
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

  ServerDirs := CreateInputOptionPage(ServersPage.ID, 'Server hook: which DCS server',
    'The DCS server installs controllers connect to.', 'NEBO goes into each ticked Saved Games folder (Mods\Services\NEBO, Scripts\Hooks). Stop those DCS servers first. Old DCB / Overseer files there are removed; the settings move over on the first start.', False, False);
  FillDcsFolders(ServerDirs, True, 'SERVERDIRS');
  AddBrowseButton(ServerDirs, @BrowseServer);

  ServerOpts := CreateInputQueryPage(ServerDirs.ID, 'Server hook: port',
    'The TCP port controllers and pilot hooks connect to.',
    'Written to each server''s Config\NEBO.lua (a new server makes it on the first start), like the settings on the next pages. Passwords, Discord login and the other settings in NEBO.lua stay as they are (see INSTALL.txt).');
  ServerOpts.Add('Port:', False);
  ServerOpts.Values[0] := ExpandConstant('{param:PORT|' + DefaultPort + '}');
  FirewallBox := TNewCheckBox.Create(ServerOpts);
  FirewallBox.Parent := ServerOpts.Surface;
  FirewallBox.Top := ServerOpts.Edits[0].Top + ServerOpts.Edits[0].Height + ScaleY(16);
  FirewallBox.Width := ServerOpts.SurfaceWidth;
  FirewallBox.Height := ScaleY(20);
  FirewallBox.Caption := 'Open this port in the Windows firewall (asks for administrator rights)';
  FirewallBox.Checked := ExpandConstant('{param:FIREWALL|0}') = '1';

  MakeOptPage(0, ServerOpts.ID, 'enemy_ground', 'Server settings: enemy ground units',
    'Do controllers see the enemy SAMs and vehicles their own radars detect?',
    'NEBO.lua: enemy_ground. "Auto" shows them in missions dated enemy_ground_year (2012) or later, the modern AWACS/JSTARS era.',
    'ground-shown', 'ground-hidden', '"auto"=From 2012 missions (auto)|true=Always', 'false=Never: found by RWR triangulation');
  MakeOptPage(1, OptPages[0].ID, 'identification', 'Server settings: identification',
    'Does a radar contact show its aircraft type?',
    'NEBO.lua: identification. Manual: controllers identify contacts by NCTR, pilots'' calls and right-click, shared with their side''s other controllers.',
    'ident-auto', 'ident-manual', '"auto"=Automatic, once DCS identifies it', '"manual"=Manual, by the controllers');
  MakeOptPage(2, OptPages[1].ID, 'radar_scan', 'Server settings: radar scan',
    'How often does a contact move on the scope?',
    'NEBO.lua: radar_scan. An E-3 refreshes every 10 s, a ship''s radar every 5-6 s, a SAM search radar every 1-5 s, a fighter''s every 4 s.',
    'scan-on', 'scan-off', 'true=At each radar''s scan rate', 'false=Every second');
  MakeOptPage(3, OptPages[2].ID, 'allow_flying_controllers', 'Server settings: flying controllers',
    'May someone control from the address (IP) of a player sitting in a cockpit?',
    'NEBO.lua: allow_flying_controllers. Players behind one router share an address: allow it if your controllers and pilots do.',
    'flying-no', 'flying-yes', 'false=Refuse: controllers don''t fly', 'true=Allow (shared router)');
  MakeOptPage(4, OptPages[3].ID, 'dtc', 'Server settings: data cartridges',
    'May controllers push data cartridges (F/A-18C, F-16C, F-14B(U)) to pilots?',
    'NEBO.lua: dtc. Pilots need the NEBO pilot hook with this server in its list. A pilot gets only their own side''s cartridges.',
    'dtc-on', 'dtc-off', 'true=On', 'false=Off');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
var i: Integer;
begin
  Result := ((PageID = PilotDirs.ID) or (PageID = ServersPage.ID)) and not WizardIsComponentSelected('pilot')
         or ((PageID = ServerDirs.ID) or (PageID = ServerOpts.ID)) and not WizardIsComponentSelected('server');
  for i := 0 to OptCount - 1 do
    if (PageID = OptPages[i].ID) and not WizardIsComponentSelected('server') then Result := True;
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

{ The port and settings start from the first ticked server's NEBO.lua (or the old DCB.lua it
  takes over, or NEBO.example.lua); /PORT= and /<KEY>= override them. }
procedure FillSettings;
var Dirs, Lines: TArrayOfString; i, j, k: Integer; Cfg, V: String; Found: Boolean;
begin
  if SettingsFilled then exit;
  SettingsFilled := True;
  Dirs := Checked(ServerDirs);
  Found := False;
  for i := 0 to GetArrayLength(Dirs) - 1 do
    if not Found then begin
      Cfg := Dirs[i] + '\Config\';
      if Load(Cfg + 'NEBO.lua', Lines) then Found := True
      else if Load(Cfg + 'DCB.lua', Lines) then Found := True
      else if Load(Cfg + 'NEBO.example.lua', Lines) then Found := True;
    end;
  if Found then
    for j := 0 to GetArrayLength(Lines) - 1 do begin
      V := ValueOf(Lines[j], 'port');
      if StrToIntDef(V, 0) > 0 then ServerOpts.Values[0] := V;
      for k := 0 to OptCount - 1 do begin
        V := ValueOf(Lines[j], OptKeys[k]);
        if V <> '' then SetOpt(k, V);
      end;
    end;
  if ExpandConstant('{param:PORT|}') <> '' then ServerOpts.Values[0] := ExpandConstant('{param:PORT|}');
  for k := 0 to OptCount - 1 do begin
    V := ExpandConstant('{param:' + Uppercase(OptKeys[k]) + '|}');
    if V <> '' then SetOpt(k, V);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = ServersPage.ID then FillServers;
  if CurPageID = ServerOpts.ID then FillSettings;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if ((CurPageID = PilotDirs.ID) and (GetArrayLength(Checked(PilotDirs)) = 0))
     or ((CurPageID = ServerDirs.ID) and (GetArrayLength(Checked(ServerDirs)) = 0)) then begin
    MsgBox('Tick at least one folder, or go back and untick this part.', mbError, MB_OK);
    Result := False;
  end;
  if (CurPageID = ServerOpts.ID) and ((StrToIntDef(Trim(ServerOpts.Values[0]), 0) < 1) or (StrToIntDef(Trim(ServerOpts.Values[0]), 0) > 65535)) then begin
    MsgBox('The port is a number from 1 to 65535 (NEBO''s default is 10309).', mbError, MB_OK);
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
  if WizardIsComponentSelected('server') then begin
    Result := Result + NewLine + NewLine + 'Server hook into:';
    Dirs := Checked(ServerDirs);
    for i := 0 to GetArrayLength(Dirs) - 1 do Result := Result + NewLine + Space + Dirs[i];
    Result := Result + NewLine + 'Port ' + Trim(ServerOpts.Values[0]);
    if FirewallBox.Checked then Result := Result + ', opened in the Windows firewall';
    Result := Result + NewLine + 'NEBO.lua settings:';
    for i := 0 to OptCount - 1 do Result := Result + NewLine + Space + OptKeys[i] + ' = ' + OptValue(i);
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

{ Sets the port and the settings pages' keys in a NEBO.lua (changed lines only; a key the file
  lacks, from an older version, is added at the end). }
procedure WriteSettings(FileName: String);
var Lines: TArrayOfString; Found: array[0..5] of Boolean; j, k: Integer; Changed: Boolean; New: String;
begin
  if not Load(FileName, Lines) then exit;
  Changed := False;
  for k := 0 to OptCount do Found[k] := False;
  for j := 0 to GetArrayLength(Lines) - 1 do
    for k := 0 to OptCount do
      if ValueOf(Lines[j], KeyName(k)) <> '' then begin
        Found[k] := True;
        New := WithValue(Lines[j], KeyValue(k));
        if New <> Lines[j] then begin
          Lines[j] := New;
          Changed := True;
        end;
      end;
  for k := 0 to OptCount do
    if not Found[k] then begin
      AddLine(Lines, KeyName(k) + ' = ' + KeyValue(k));
      Changed := True;
    end;
  if Changed then Save(FileName, Lines);
end;

procedure InstallServer(Dir: String);
var Cfg: String;
begin
  { 0.15.x (code name DCB): its hook would open the same port. NEBO takes over its settings. }
  DeleteFile(Dir + '\Scripts\Hooks\DCB-hook.lua');
  DelTree(Dir + '\Mods\Services\DCB', True, True, True);
  CopyTree(ExpandConstant('{app}\server'), Dir);
  { The settings go into NEBO.lua, and into the example a new server makes it from on the first
    start. The old DCB.lua becomes NEBO.lua here (as the hook would) so the settings land in it. }
  Cfg := Dir + '\Config\';
  if not FileExists(Cfg + 'NEBO.lua') and FileExists(Cfg + 'DCB.lua') then CopyOne(Cfg + 'DCB.lua', Cfg + 'NEBO.lua');
  WriteSettings(Cfg + 'NEBO.example.lua');
  WriteSettings(Cfg + 'NEBO.lua');
end;

procedure OpenFirewall(Port: String);
var Code: Integer;
begin
  if not ShellExec('runas', ExpandConstant('{cmd}'),
      '/c netsh advfirewall firewall delete rule name="NEBO ' + Port + '" >nul & netsh advfirewall firewall add rule name="NEBO ' + Port + '" dir=in action=allow protocol=TCP localport=' + Port,
      '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Failed := Failed + #13#10 + 'Windows firewall rule for port ' + Port + ' (open it as INSTALL.txt shows)';
end;

{ Where the pilot and server hooks went, so the uninstaller removes them: "pilot|<dir>" lines. }
procedure Remember(Kind: String; Dirs: TArrayOfString);
var Lines: TArrayOfString; i, j: Integer; Known: Boolean; FileName: String;
begin
  FileName := ExpandConstant('{app}\targets.txt');
  Load(FileName, Lines);
  for i := 0 to GetArrayLength(Dirs) - 1 do begin
    Known := False;
    for j := 0 to GetArrayLength(Lines) - 1 do
      if CompareText(Lines[j], Kind + '|' + Dirs[i]) = 0 then Known := True;
    if not Known then AddLine(Lines, Kind + '|' + Dirs[i]);
  end;
  Save(FileName, Lines);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Dirs: TArrayOfString; i: Integer; Port: String;
begin
  if CurStep <> ssPostInstall then exit;
  if WizardIsComponentSelected('pilot') then begin
    if WizardSilent then FillServers;
    Dirs := Checked(PilotDirs);
    for i := 0 to GetArrayLength(Dirs) - 1 do InstallPilot(Dirs[i]);
    Remember('pilot', Dirs);
  end;
  if WizardIsComponentSelected('server') then begin
    if WizardSilent then FillSettings;
    Port := Trim(ServerOpts.Values[0]);
    Dirs := Checked(ServerDirs);
    for i := 0 to GetArrayLength(Dirs) - 1 do InstallServer(Dirs[i]);
    Remember('server', Dirs);
    if FirewallBox.Checked then OpenFirewall(Port);
  end;
  if Failed <> '' then
    SuppressibleMsgBox('These could not be written (a running DCS keeps its files locked: close it and run the setup again):' + Failed,
      mbError, MB_OK, IDOK);
end;

{ ---- uninstalling: only what the setup put there; never NEBO.lua, cartridges or settings ---- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Targets, Lines: TArrayOfString; i: Integer; Dir: String;
begin
  if (CurUninstallStep <> usUninstall) or not Load(ExpandConstant('{app}\targets.txt'), Targets) then exit;
  for i := 0 to GetArrayLength(Targets) - 1 do begin
    Dir := Copy(Targets[i], Pos('|', Targets[i]) + 1, MaxInt);
    if Pos('pilot|', Targets[i]) = 1 then begin
      DeleteFile(Dir + '\Scripts\Hooks\NEBO-DTC.lua');
      DeleteFile(Dir + '\Scripts\NEBO-Export.lua');
      if Load(Dir + '\Scripts\Export.lua', Lines) and DropLines(Lines, 'NEBO-Export.lua') then
        SaveStringsToUTF8FileWithoutBOM(Dir + '\Scripts\Export.lua', Lines, False);
    end else if Pos('server|', Targets[i]) = 1 then begin
      DelTree(Dir + '\Mods\Services\NEBO', True, True, True);
      DeleteTree(ExpandConstant('{app}\server'), Dir, True);
    end;
  end;
  DeleteFile(ExpandConstant('{app}\targets.txt'));
end;
