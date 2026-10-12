; =====================================================================
; TKG PDA Server - Inno Setup Update Package Script
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
AppName=TKG PDA Server Update
AppVersion={#AppVersion}
AppPublisher=TKG Otomotiv
AppPublisherURL=https://www.tkg.com.tr
DefaultDirName={autopf}\TKG PDA
DisableDirPage=no
DisableProgramGroupPage=yes
OutputBaseFilename=TKG_PDA_Update_{#AppVersion}
OutputDir={#OutputDir}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern

[Files]
Source: "{#StagingDir}\Server\*"; DestDir: "{app}\_update_staging\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\Web\*"; DestDir: "{app}\_update_staging\Web"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\Tools\*"; DestDir: "{app}\_update_staging\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\db\*"; DestDir: "{app}\_update_staging\db"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\rds\*"; DestDir: "{app}\_update_staging\rds"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StagingDir}\manifest.json"; DestDir: "{app}\_update_staging"; Flags: ignoreversion

[Code]
function InitializeSetup(): Boolean;
var
  InstallPath: String;
begin
  InstallPath := ExpandConstant('{autopf}\TKG PDA\Server\TkgPda.Api.exe');
  if not FileExists(InstallPath) then
  begin
    MsgBox('Mevcut bir TKG PDA Server kurulumu bulunamadı (' + InstallPath + '). Lutfen once ana kurulum paketini (TKG_PDA_Setup) calistirin.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  Result := True;
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

    Params := Format('-NoProfile -ExecutionPolicy Bypass -File "%s\Tools\Update-TkgServer.ps1" ' +
      '-SourceDir "%s\_update_staging" ' +
      '-InstallDir "%s" ' +
      '-TargetVersion "%s"', [
        ExpandConstant('{app}'),
        ExpandConstant('{app}'),
        ExpandConstant('{app}'),
        '{#AppVersion}'
      ]);

    Exec(PwshExe, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    
    // Clean up update staging folder
    DelTree(ExpandConstant('{app}\_update_staging'), True, True, True);

    if ResultCode <> 0 then
    begin
      MsgBox('Guncelleme sirasinda bir hata olustu. Sistem otomatik olarak onceki surume geri donduruldu (rollback). Ayrıntılar için C:\ProgramData\TKG PDA\Logs altındaki log dosyasını inceleyin.', mbError, MB_OK);
    end
    else
    begin
      MsgBox('TKG PDA Server {#AppVersion} guncellemesi basariyla tamamlandi. Veritabani ve yapilandirma korundu.', mbInformation, MB_OK);
    end;
  end;
end;
