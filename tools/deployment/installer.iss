; =====================================================================
; TKG PDA Server - Inno Setup Installer Script
; =====================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef StagingDir
  #define StagingDir "..\..\build_staging"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts\release\" + AppVersion
#endif

[Setup]
AppId={{9F27C21E-4B07-4C3D-B91F-77D012B5E2C4}
AppName=TKG PDA Server
AppVersion={#AppVersion}
AppPublisher=TKG Otomotiv
AppPublisherURL=https://www.tkg.com.tr
DefaultDirName={autopf}\TKG PDA
DefaultGroupName=TKG PDA
DisableProgramGroupPage=yes
OutputBaseFilename=TKG_PDA_Setup_{#AppVersion}
OutputDir={#OutputDir}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
UninstallDisplayName=TKG PDA Server {#AppVersion}
UninstallFilesDir={app}\Uninstall

[Dirs]
Name: "{commonappdata}\TKG PDA\Config"
Name: "{commonappdata}\TKG PDA\Logs"
Name: "{commonappdata}\TKG PDA\Backups"
Name: "{commonappdata}\TKG PDA\Data"
Name: "{commonappdata}\TKG PDA\RDS"
Name: "{commonappdata}\TKG PDA\PostgreSQL\data"
Name: "{app}\Server"
Name: "{app}\Web"
Name: "{app}\Tools"
Name: "{app}\PostgreSQL"

[Files]
Source: "{#StagingDir}\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\Web\*"; DestDir: "{app}\Web"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\Tools\*"; DestDir: "{app}\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\PostgreSQL\*"; DestDir: "{app}\PostgreSQL"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: DirExists(ExpandConstant('{#StagingDir}\PostgreSQL'))
Source: "{#StagingDir}\db\*"; DestDir: "{app}\Tools\db"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\rds\*"; DestDir: "{app}\Tools\rds"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\manifest.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Yonetim Paneli (Admin)"; Filename: "http://127.0.0.1:5000/admin/"
Name: "{group}\PDA - Operator Terminali"; Filename: "http://127.0.0.1:5000/pda/"
Name: "{group}\Sunucu Durumu"; Filename: "http://127.0.0.1:5000/"
Name: "{group}\Kaldir (Uninstall)"; Filename: "{uninstallexe}"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Server"
Type: filesandordirs; Name: "{app}\Web"
Type: filesandordirs; Name: "{app}\Tools"
Type: filesandordirs; Name: "{app}\PostgreSQL"

[Code]
function SetEnvironmentVariable(lpName, lpValue: string): BOOL;
external 'SetEnvironmentVariableW@kernel32.dll stdcall';

var
  ServerPage: TInputQueryWizardPage;
  DbPage: TInputQueryWizardPage;
  AdminAuthPage: TInputQueryWizardPage;
  RdsPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  // 1. Server Settings Page
  ServerPage := CreateInputQueryPage(wpSelectDir,
    'Sunucu Ayarlari',
    'TKG PDA Kestrel web sunucusu baglanti ayarlari',
    'Sunucunun istemcilere hizmet verecegi ag portunu ve baglanti IP adresini belirleyin.');
  ServerPage.Add('Sunucu Portu (Varsayilan 5000):', False);
  ServerPage.Add('Baglanti IP / Host (Varsayilan 0.0.0.0):', False);
  ServerPage.Values[0] := '5000';
  ServerPage.Values[1] := '0.0.0.0';

  // 2. Database Settings Page
  DbPage := CreateInputQueryPage(ServerPage.ID,
    'Veritabani Ayarlari (PostgreSQL)',
    'PostgreSQL baglanti parametreleri',
    'Mevcut veya kurulacak PostgreSQL veritabani baglanti bilgilerini girin.');
  DbPage.Add('Sunucu Adresi (Host):', False);
  DbPage.Add('Port:', False);
  DbPage.Add('Veritabani Adi:', False);
  DbPage.Add('Yonetici Kullanici (Superuser):', False);
  DbPage.Add('Yonetici Parola:', True);
  DbPage.Add('Uygulama Kullanicisi (App User):', False);
  DbPage.Values[0] := '127.0.0.1';
  DbPage.Values[1] := '5432';
  DbPage.Values[2] := 'tkg_pda';
  DbPage.Values[3] := 'postgres';
  DbPage.Values[4] := '';
  DbPage.Values[5] := 'tkg_app';

  // 3. First Admin Bootstrap Page
  AdminAuthPage := CreateInputQueryPage(DbPage.ID,
    'Ilk Sistem Yoneticisi (Admin) Hesabi',
    'Yonetim Paneli ilk giris bilgileri',
    'TKG PDA Yonetim paneline giris yapacak ilk admin hesabi parolasini belirleyin (en az 8 karakter).');
  AdminAuthPage.Add('Admin Parolasi:', True);
  AdminAuthPage.Add('Admin Parolasi (Tekrar):', True);
  AdminAuthPage.Values[0] := '';
  AdminAuthPage.Values[1] := '';

  // 4. RDS Settings Page
  RdsPage := CreateInputQueryPage(AdminAuthPage.ID,
    'RDS Entegrasyon Ayarlari',
    'SEER RDS robot kontrol entegrasyonu',
    'RDS servis adresi ve geri arama (callback) adresini girin.');
  RdsPage.Add('RDS Script / API Base Adresi:', False);
  RdsPage.Add('RDS Core Base Adresi (Orn: http://127.0.0.1:8088):', False);
  RdsPage.Add('Callback URL (RDS tarafindan erisilebilir adres):', False);
  RdsPage.Add('Integration Key (bos birakilirsa otomatik uretilir):', False);
  RdsPage.Add('Callback Key (bos birakilirsa otomatik uretilir):', False);
  RdsPage.Add('Dispatch Etkin (1=Evet, 0=Hayir):', False);
  RdsPage.Values[0] := 'http://127.0.0.1:8080';
  RdsPage.Values[1] := 'http://127.0.0.1:8088';
  RdsPage.Values[2] := 'http://127.0.0.1:5000/api/integrations/rds/events';
  RdsPage.Values[3] := '';
  RdsPage.Values[4] := '';
  RdsPage.Values[5] := '1';
end;

function IsLoopback(UrlStr: String): Boolean;
var
  Lower: String;
begin
  Lower := Lowercase(UrlStr);
  Result := (Pos('127.0.0.1', Lower) > 0) or (Pos('localhost', Lower) > 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  AdminPw, AdminPwConfirm: String;
  RdsUrl, RdsCoreUrl, CallbackUrl: String;
  RemoteRds, LoopbackCallback: Boolean;
begin
  Result := True;

  // Validate Admin Password
  if CurPageID = AdminAuthPage.ID then
  begin
    AdminPw := AdminAuthPage.Values[0];
    AdminPwConfirm := AdminAuthPage.Values[1];

    if Length(AdminPw) < 8 then
    begin
      MsgBox('Admin parolasi en az 8 karakter uzunlugunda olmalidir.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if AdminPw <> AdminPwConfirm then
    begin
      MsgBox('Girilen admin parolalari birbiriyle eslesmiyor.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  // Validate RDS Network Topology
  if CurPageID = RdsPage.ID then
  begin
    RdsUrl := Trim(RdsPage.Values[0]);
    RdsCoreUrl := Trim(RdsPage.Values[1]);
    CallbackUrl := Trim(RdsPage.Values[2]);

    RemoteRds := False;
    if (RdsUrl <> '') and (not IsLoopback(RdsUrl)) then
      RemoteRds := True;
    if (RdsCoreUrl <> '') and (not IsLoopback(RdsCoreUrl)) then
      RemoteRds := True;

    LoopbackCallback := IsLoopback(CallbackUrl);

    if RemoteRds and LoopbackCallback then
    begin
      MsgBox('Gecersiz Ag Topolojisi: Uzak RDS sunucusu kullanilirken Callback URL 127.0.0.1 veya localhost olamaz.' + #13#10 +
        'RDS sunucusunun erisebilecegi bu sunucunun ag IP adresini Callback URL alanina giriniz.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
  PwshExe: String;
  DispatchFlag: String;
begin
  if CurStep = ssPostInstall then
  begin
    PwshExe := 'powershell.exe';
    
    DispatchFlag := '$true';
    if RdsPage.Values[5] = '0' then
      DispatchFlag := '$false';

    SetEnvironmentVariable('TKG_SETUP_DB_PASSWORD', DbPage.Values[4]);
    SetEnvironmentVariable('TKG_SETUP_ADMIN_PASSWORD', AdminAuthPage.Values[0]);

    Params := Format('-NoProfile -ExecutionPolicy Bypass -File "%s\Tools\Install-TkgServer.ps1" ' +
      '-SourceDir "%s" ' +
      '-InstallDir "%s" ' +
      '-ServerPort %s ' +
      '-HostBinding "%s" ' +
      '-DbHost "%s" ' +
      '-DbPort %s ' +
      '-DbName "%s" ' +
      '-DbUser "%s" ' +
      '-AppDbUser "%s" ' +
      '-RdsBaseUrl "%s" ' +
      '-RdsCoreBaseUrl "%s" ' +
      '-RdsCallbackUrl "%s" ' +
      '-RdsIntegrationKey "%s" ' +
      '-RdsCallbackKey "%s" ' +
      '-RdsDispatchEnabled %s ' +
      '-Version "%s"', [
        ExpandConstant('{app}'),
        ExpandConstant('{app}'),
        ExpandConstant('{app}'),
        ServerPage.Values[0],
        ServerPage.Values[1],
        DbPage.Values[0],
        DbPage.Values[1],
        DbPage.Values[2],
        DbPage.Values[3],
        DbPage.Values[5],
        RdsPage.Values[0],
        RdsPage.Values[1],
        RdsPage.Values[2],
        RdsPage.Values[3],
        RdsPage.Values[4],
        DispatchFlag,
        '{#AppVersion}'
      ]);

    try
      Exec(PwshExe, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    finally
      SetEnvironmentVariable('TKG_SETUP_DB_PASSWORD', '');
      SetEnvironmentVariable('TKG_SETUP_ADMIN_PASSWORD', '');
    end;

    if ResultCode <> 0 then
    begin
      MsgBox('Sunucu kurulumu basarisiz oldu. Ayrintilar icin C:\ProgramData\TKG PDA\Logs altindaki log dosyasini inceleyin.', mbCriticalError, MB_OK);
      RaiseException('TKG PDA Server kurulumu ' + IntToStr(ResultCode) + ' hata kodu ile basarisiz oldu.');
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "& sc.exe stop ''TKG PDA Server''; & sc.exe delete ''TKG PDA Server''; Remove-NetFirewallRule -DisplayName ''TKG PDA Server (HTTP)'' -ErrorAction SilentlyContinue"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
