; =====================================================================
; TKG PDA Demo Server - Inno Setup Installer Script
; Dedicated offline demonstration setup package
; Zero RDS/PLC/Robot connection required.
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
AppId={{C2828D2B-814E-43EA-8B18-202C5A96A77C}}
AppName=TKG PDA Demo Server
AppVersion={#AppVersion}
AppPublisher=TKG Otomotiv
AppPublisherURL=https://www.tkg.com.tr
DefaultDirName={autopf}\TKG PDA Demo
DefaultGroupName=TKG PDA Demo
DisableProgramGroupPage=yes
OutputBaseFilename=TKG_PDA_Demo_Setup_{#AppVersion}
OutputDir={#OutputDir}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
UninstallDisplayName=TKG PDA Demo Server {#AppVersion}
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
Source: "{#StagingDir}\PostgreSQL\*"; DestDir: "{app}\PostgreSQL"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\db\*"; DestDir: "{app}\Tools\db"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\rds\*"; DestDir: "{app}\Tools\rds"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\manifest.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\TKG PDA Demo - Ana Sayfa"; Filename: "http://127.0.0.1:5000/"
Name: "{autodesktop}\TKG PDA Demo - PDA Operator Terminali"; Filename: "http://127.0.0.1:5000/pda/"
Name: "{autodesktop}\TKG PDA Demo - Admin Yonetim Paneli"; Filename: "http://127.0.0.1:5000/admin/"
Name: "{group}\TKG PDA Demo - Ana Sayfa"; Filename: "http://127.0.0.1:5000/"
Name: "{group}\TKG PDA Demo - PDA Operator Terminali"; Filename: "http://127.0.0.1:5000/pda/"
Name: "{group}\TKG PDA Demo - Admin Yonetim Paneli"; Filename: "http://127.0.0.1:5000/admin/"
Name: "{group}\Kaldir (Uninstall)"; Filename: "{uninstallexe}"

[Run]
Filename: "http://127.0.0.1:5000/"; Description: "TKG PDA Demo Karşılama Sayfasını Aç (Tarayıcıda)"; Flags: postinstall shellexec nowait

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Server"
Type: filesandordirs; Name: "{app}\Web"
Type: filesandordirs; Name: "{app}\Tools"
Type: filesandordirs; Name: "{app}\PostgreSQL"

[Code]
var
  InfoPage: TOutputMsgWizardPage;

procedure InitializeWizard;
begin
  InfoPage := CreateOutputMsgPage(wpSelectDir,
    'TKG PDA Cevrimdisi Demo Surumu',
    'Musteri ve Saha Tanitimi Icin Hazirlanmis Izole Calisma Modu',
    'Bu kurulum paketi, TKG PDA sisteminin tum son kullanici arayuzlerini (Admin ve PDA Web)' + #13#10 +
    'tamamen cevrimdisi ve kontrollu simülasyon modunda calistirmak uzere yapilandirir.' + #13#10#13#10 +
    'Ozellikler:' + #13#10 +
    ' * Gercek RDS, PLC veya robot baglantisi bulunmaz (Saha dis ag baglantilari kilitlidir).' + #13#10 +
    ' * Dahili yerel PostgreSQL ve hazir TKG demo verileri otomatik kurulur.' + #13#10 +
    ' * Masaustu kisayollari ve yerel Windows Servisi otomatik olusturulur.' + #13#10#13#10 +
    'Kuruluma devam etmek icin Ileri butonuna basin.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
  PwshExe: String;
begin
  if CurStep = ssInstall then
  begin
    Exec('sc.exe', 'stop "TKG PDA Server"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'stop "tkg-pda-postgres"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;

  if CurStep = ssPostInstall then
  begin
    PwshExe := 'powershell.exe';
    
    Params := Format('-NoProfile -ExecutionPolicy Bypass -File "%s\Tools\Install-TkgServer.ps1" ' +
      '-SourceDir "%s" ' +
      '-InstallDir "%s" ' +
      '-ServerPort 5000 ' +
      '-HostBinding "0.0.0.0" ' +
      '-DbHost "127.0.0.1" ' +
      '-DbPort 5432 ' +
      '-DbName "tkg_pda" ' +
      '-DbUser "postgres" ' +
      '-AppDbUser "tkg_app" ' +
      '-AdminPassword "Admin123!" ' +
      '-DemoMode ' +
      '-Version "%s"', [
        ExpandConstant('{app}'),
        ExpandConstant('{app}'),
        ExpandConstant('{app}'),
        '{#AppVersion}'
      ]);

    Exec(PwshExe, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);

    if ResultCode <> 0 then
    begin
      MsgBox('Demo sunucu kurulumu basarisiz oldu. Ayrintilar icin C:\ProgramData\TKG PDA\Logs altindaki log dosyasini inceleyin.', mbCriticalError, MB_OK);
      RaiseException('TKG PDA Demo Server kurulumu ' + IntToStr(ResultCode) + ' hata kodu ile basarisiz oldu.');
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
