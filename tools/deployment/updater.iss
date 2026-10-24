; =====================================================================
; TKG PDA Server - Inno Setup Update Package Script
; Upgrades an existing TKG PDA Server or TKG PDA Demo Server installation
; without requiring uninstallation, preserving data and configurations.
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
AppName=TKG PDA Server Güncelleme Paketi
AppVersion={#AppVersion}
AppPublisher=TKG Otomotiv
AppPublisherURL=https://www.tkg.com.tr
DefaultDirName={code:GetDefaultInstallDir}
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
Source: "{#StagingDir}\update_manifest.json"; DestDir: "{app}\_update_staging"; DestName: "manifest.json"; Flags: ignoreversion

[Code]
function GetDefaultInstallDir(Param: String): String;
begin
  if FileExists(ExpandConstant('{autopf}\TKG PDA Demo\Server\TkgPda.Api.exe')) then
    Result := ExpandConstant('{autopf}\TKG PDA Demo')
  else if FileExists(ExpandConstant('{autopf}\TKG PDA\Server\TkgPda.Api.exe')) then
    Result := ExpandConstant('{autopf}\TKG PDA')
  else
    Result := ExpandConstant('{autopf}\TKG PDA Demo');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedServerExe: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    SelectedServerExe := ExpandConstant('{app}\Server\TkgPda.Api.exe');
    if not FileExists(SelectedServerExe) then
    begin
      MsgBox('Secilen dizinde gecerli bir TKG PDA sunucu kurulumu bulunamadi:' + #13#10 +
             SelectedServerExe + #13#10#13#10 +
             'Lutfen TKG PDA veya TKG PDA Demo sisteminin kurulu oldugu ana dizini secin.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
  PwshExe: String;
  UpdateScript: String;
begin
  if CurStep = ssPostInstall then
  begin
    PwshExe := 'powershell.exe';

    UpdateScript := ExpandConstant('{app}\_update_staging\Tools\Update-TkgServer.ps1');
    if not FileExists(UpdateScript) then
      UpdateScript := ExpandConstant('{app}\Tools\Update-TkgServer.ps1');

    Params := Format('-NoProfile -ExecutionPolicy Bypass -File "%s" ' +
      '-SourceDir "%s\_update_staging" ' +
      '-InstallDir "%s" ' +
      '-TargetVersion "%s"', [
        UpdateScript,
        ExpandConstant('{app}'),
        ExpandConstant('{app}'),
        '{#AppVersion}'
      ]);

    Exec(PwshExe, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    
    // Clean up update staging folder
    DelTree(ExpandConstant('{app}\_update_staging'), True, True, True);

    if ResultCode <> 0 then
    begin
      SuppressibleMsgBox('Guncelleme sirasinda bir hata olustu. Sistem otomatik olarak onceki surume geri donduruldu (rollback). Ayrintilar icin Logs altindaki update log dosyasini inceleyin.', mbCriticalError, MB_OK, IDOK);
      RaiseException('Guncelleme basarisiz oldu. Hata kodu: ' + IntToStr(ResultCode));
    end
    else
    begin
      SuppressibleMsgBox('TKG PDA Server {#AppVersion} guncellemesi basariyla tamamlandi.' + #13#10 +
             'Veritabani, kullanici yapilandirmalari ve calisan servisler basariyla korundu ve en son demo gelistirmeleriyle guncellendi.', mbInformation, MB_OK, IDOK);
    end;
  end;
end;
