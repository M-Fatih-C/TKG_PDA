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

[Files]
Source: "{#StagingDir}\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\Web\*"; DestDir: "{app}\Web"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\Tools\*"; DestDir: "{app}\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs
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

[Code]
var
  ServerPage: TInputQueryWizardPage;
  DbPage: TInputQueryWizardPage;
  RdsPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  // 1. Server Settings Page
  ServerPage := CreateInputQueryPage(wpSelectDir,
    'Sunucu Ayarlari',
    'TKG PDA Kestrel web sunucusu baglanti ayarlari',
    'Sunucunun istemcilere hizmet verecegi ag portunu ve baglanti adresini belirleyin.');
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
  DbPage.Add('Kullanici Adi:', False);
  DbPage.Add('Parola:', True);
  DbPage.Values[0] := '127.0.0.1';
  DbPage.Values[1] := '5432';
  DbPage.Values[2] := 'tkg_pda';
  DbPage.Values[3] := 'postgres';
  DbPage.Values[4] := '';

  // 3. RDS Settings Page
  RdsPage := CreateInputQueryPage(DbPage.ID,
    'RDS Entegrasyon Ayarlari',
    'SEER RDS robot kontrol entegrasyonu',
    'RDS servis adresi ve geri arama (callback) adresini girin.');
  RdsPage.Add('RDS Base Adresi:', False);
  RdsPage.Add('Callback URL:', False);
  RdsPage.Add('Integration Key (bos birakilirsa otomatik uretilir):', False);
  RdsPage.Add('Callback Key (bos birakilirsa otomatik uretilir):', False);
  RdsPage.Values[0] := 'http://127.0.0.1:8080';
  RdsPage.Values[1] := 'http://127.0.0.1:5000/api/integrations/rds/events';
  RdsPage.Values[2] := '';
  RdsPage.Values[3] := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
  PwshExe: String;
begin
  if CurStep = ssPostInstall then
  begin
    PwshExe := 'powershell.exe';

    Params := Format('-NoProfile -ExecutionPolicy Bypass -File "%s\Tools\Install-TkgServer.ps1" ' +
      '-SourceDir "%s" ' +
      '-InstallDir "%s" ' +
      '-ServerPort %s ' +
      '-HostBinding "%s" ' +
      '-DbHost "%s" ' +
      '-DbPort %s ' +
      '-DbName "%s" ' +
      '-DbUser "%s" ' +
      '-DbPassword "%s" ' +
      '-RdsBaseUrl "%s" ' +
      '-RdsCallbackUrl "%s" ' +
      '-RdsIntegrationKey "%s" ' +
      '-RdsCallbackKey "%s" ' +
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
        DbPage.Values[4],
        RdsPage.Values[0],
        RdsPage.Values[1],
        RdsPage.Values[2],
        RdsPage.Values[3],
        '{#AppVersion}'
      ]);

    Exec(PwshExe, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then
    begin
      MsgBox('Sunucu kurulum adiminda hata olustu. Ayrıntılar için C:\ProgramData\TKG PDA\Logs altındaki log dosyasını inceleyin.', mbError, MB_OK);
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
