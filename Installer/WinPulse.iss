[Setup]
AppId=WinPulsePro
AppName=WinPulse Pro
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName=WinPulse Pro
VersionInfoDescription=Instalador de WinPulse Pro
DefaultDirName={commonpf64}\WinPulse Pro
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
CloseApplications=no
RestartApplications=no
SetupIconFile=..\Assets\WinPulse.ico
OutputBaseFilename=WinPulseSetup
Compression=lzma2/fast
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
#ifdef OfflineRuntimeInstaller
Source: "{#OfflineRuntimeInstaller}"; DestName: "WindowsDesktopRuntime-x64.exe"; Flags: dontcopy
#endif
Source: "{#AppExecutable}"; DestName: "WinPulseOptimizer.exe"; Flags: dontcopy

[Run]
Filename: "{commonpf64}\WinPulse Pro\WinPulseOptimizer.exe"; WorkingDir: "{commonpf64}\WinPulse Pro"; Description: "Abrir WinPulse Pro"; Flags: postinstall nowait runasoriginaluser skipifsilent

[Code]
#ifndef OfflineRuntimeInstaller
var
  RuntimeDownloadPage: TDownloadWizardPage;
#endif

function HasRuntimeFiles(const FrameworkName, RequiredFile: String): Boolean;
var
  FrameworkRoot, Version: String;
  Entry: TFindRec;
  MinimumVersion, InstalledVersion: Int64;
begin
  Result := False;
  FrameworkRoot := ExpandConstant('{commonpf64}\dotnet\shared\') + FrameworkName + '\';
  if not StrToVersion('{#RuntimeVersion}.0', MinimumVersion) then
    exit;
  if not FindFirst(FrameworkRoot + '10.*', Entry) then
    exit;
  try
    repeat
      Version := Entry.Name;
      if ((Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
         StrToVersion(Version + '.0', InstalledVersion) and
         (ComparePackedVersion(InstalledVersion, MinimumVersion) >= 0) and
         FileExists(FrameworkRoot + Version + '\' + RequiredFile) and
         FileExists(FrameworkRoot + Version + '\' + FrameworkName + '.deps.json') then
      begin
        Result := True;
        exit;
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

function DesktopRuntimeAvailable: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\dotnet\dotnet.exe')) and
    HasRuntimeFiles('Microsoft.NETCore.App', 'coreclr.dll') and
    HasRuntimeFiles('Microsoft.WindowsDesktop.App', 'PresentationFramework.dll');
end;

procedure InitializeWizard;
begin
#ifdef OfflineRuntimeInstaller
  WizardForm.WelcomeLabel2.Caption :=
    'Este asistente instalará o actualizará WinPulse Pro. Si Windows necesita .NET 10 Desktop Runtime x64, el asistente instalará la copia oficial de Microsoft incluida en este paquete.';
#else
  WizardForm.WelcomeLabel2.Caption :=
    'Este asistente instalará o actualizará WinPulse Pro. Si Windows necesita .NET 10 Desktop Runtime x64, el asistente lo descargará desde Microsoft.';
  RuntimeDownloadPage := CreateDownloadPage('Preparando WinPulse Pro',
    'Descargando el runtime oficial de Microsoft…', nil);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
#endif
end;

procedure PrepareDesktopRuntimeFile;
begin
#ifdef OfflineRuntimeInstaller
  ExtractTemporaryFile('WindowsDesktopRuntime-x64.exe');
#else
  RuntimeDownloadPage.Clear;
  RuntimeDownloadPage.Add('{#RuntimeUrl}', 'WindowsDesktopRuntime-x64.exe', '{#RuntimeSha256}');
  RuntimeDownloadPage.Show;
  try
    RuntimeDownloadPage.Download;
  finally
    RuntimeDownloadPage.Hide;
  end;
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  Output: TExecOutput;
begin
  Result := '';
  try
    if not DesktopRuntimeAvailable then
    begin
      PrepareDesktopRuntimeFile;
      WizardForm.PreparingLabel.Caption := 'Instalando Microsoft .NET 10 Desktop Runtime x64…';
      if not Exec(ExpandConstant('{tmp}\WindowsDesktopRuntime-x64.exe'), '/install /quiet /norestart',
                  ExpandConstant('{sys}'), SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
      begin
        Result := 'No se pudo iniciar el instalador de Microsoft .NET. ' + SysErrorMessage(ExitCode);
        exit;
      end;
      if (ExitCode = 3010) or (ExitCode = 1641) then
      begin
        NeedsRestart := True;
        Result := 'Microsoft .NET necesita reiniciar Windows. Guarda tu trabajo y vuelve a abrir el instalador de WinPulse después.';
        exit;
      end;
      if (ExitCode <> 0) and not DesktopRuntimeAvailable then
      begin
        Result := 'Microsoft .NET no pudo instalarse. Código: ' + IntToStr(ExitCode) + '.';
        exit;
      end;
      if not DesktopRuntimeAvailable then
      begin
        Result := 'Windows todavía no detecta .NET 10 Desktop Runtime x64. Reinicia el equipo y vuelve a abrir el instalador.';
        exit;
      end;
    end;

    WizardForm.PreparingLabel.Caption := 'Instalando WinPulse Pro…';
    ExtractTemporaryFile('WinPulseOptimizer.exe');
    if not ExecAndCaptureOutput(ExpandConstant('{tmp}\WinPulseOptimizer.exe'), '--install-only',
                                ExpandConstant('{sys}'), SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode, Output) then
      Result := 'No se pudo iniciar la instalación de WinPulse. ' + SysErrorMessage(ExitCode)
    else if ExitCode = 2 then
      SuppressibleMsgBox('WinPulse quedó instalado con avisos.' + #13#10 + StringJoin(#13#10, Output.StdErr),
        mbInformation, MB_OK, IDOK)
    else if ExitCode <> 0 then
      Result := 'No se pudo instalar WinPulse. Código: ' + IntToStr(ExitCode) + '.' + #13#10 + StringJoin(#13#10, Output.StdErr);
  except
    Result := 'No se pudo completar la instalación. ' + GetExceptionMessage;
  end;
end;
