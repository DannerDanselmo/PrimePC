; Instalador do PrimePC (Inno Setup 6.7+).
; Gera um único PrimePC-Setup-<versão>.exe que instala o programa, cria atalhos, configura a execução como
; administrador e, se o usuário mantiver a opção marcada, baixa o driver PawnIO do site oficial (com verificação
; SHA-256) e o instala. O PawnIO não é embutido: a licença dele é freeware proprietária, por isso é baixado na hora.
; Compile com: gerar-instalador.ps1 (na raiz do projeto).

#define AppName "PrimePC"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "PrimePC.exe"
#ifndef SourceDir
  #define SourceDir "..\publicado"
#endif
#define PawnIOVersion "2.2.0"
#define PawnIOUrl "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"
#define PawnIOSha256 "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"

[Setup]
AppId={{6E2B5C1A-3F7D-4B8E-9A21-5D0C7E4F9B13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=PrimePC
AppComments=Monitoramento e diagnóstico de PC: sensores, gargalos, testes de RAM, VRAM, CPU, GPU e armazenamento.
DefaultDirName={autopf}\{#AppName}
; O AppId é o mesmo do PulsoPC (nome anterior): a instalação atualiza a antiga em vez de criar outra. Sem reaproveitar
; a pasta antiga, o programa vai para "PrimePC"; a pasta e os atalhos do PulsoPC são removidos em [InstallDelete].
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Mostra a tela de boas-vindas com a imagem do PrimePC (no Inno Setup 6 ela vem desligada por padrão).
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\instalador
OutputBaseFilename=PrimePC-Setup-{#AppVersion}
SetupIconFile=assets\PrimePC.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern dark
WizardBackColor=#0A0A0C
WizardImageFile=assets\wizard-100.bmp,assets\wizard-150.bmp,assets\wizard-200.bmp,assets\wizard-250.bmp
WizardSmallImageFile=assets\wizard-small-100.bmp,assets\wizard-small-150.bmp,assets\wizard-small-200.bmp,assets\wizard-small-250.bmp
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador do {#AppName}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
WelcomeLabel2=Este assistente vai instalar o [name/ver] no seu computador.%n%nO PrimePC mostra todos os sensores do PC em tempo real (CPU, GPU, RAM, placa-mãe, discos), mostra FPS, temperaturas e ping por cima dos jogos, analisa gargalos, confere e atualiza os drivers e faz testes de RAM, VRAM, CPU, GPU e armazenamento.%n%nRecomenda-se fechar outros programas antes de continuar.

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"
Name: "runasadmin"; Description: "Abrir sempre como administrador (necessário para temperatura da CPU, placa-mãe, RAM e discos)"; GroupDescription: "Sensores:"
Name: "pawnio"; Description: "Baixar e instalar o driver PawnIO {#PawnIOVersion} (oficial, assinado e de código aberto) — libera temperatura, clock e potência da CPU, ventoinhas e tensões da placa-mãe"; GroupDescription: "Sensores:"; Check: not IsPawnIOInstalled

[InstallDelete]
; Restos do nome anterior (PulsoPC, até a versão 1.5): programa, atalhos e desinstalador antigos.
Type: filesandordirs; Name: "{autopf}\PulsoPC"
Type: files; Name: "{autoprograms}\PulsoPC.lnk"
Type: files; Name: "{autoprograms}\Desinstalar o PulsoPC.lnk"
Type: files; Name: "{autodesktop}\PulsoPC.lnk"

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "LEIA-ME.md"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Monitoramento e diagnóstico do PC"
Name: "{autoprograms}\Desinstalar o {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Monitoramento e diagnóstico do PC"; Tasks: desktopicon

[Registry]
; "Executar como administrador" pela camada de compatibilidade do Windows; removido ao desinstalar.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: string; ValueName: "{app}\{#AppExe}"; ValueData: "~ RUNASADMIN"; Flags: uninsdeletevalue; Tasks: runasadmin
; Remove o "executar como administrador" do executável antigo (PulsoPC).
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: none; ValueName: "{autopf}\PulsoPC\PulsoPC.exe"; Flags: deletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o PrimePC agora"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; Remove a tarefa "iniciar com o Windows", se o usuário a ativou no PrimePC.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""PrimePC"" /F"; Flags: runhidden; RunOnceId: "RemoverTarefaInicializacao"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""PulsoPC"" /F"; Flags: runhidden; RunOnceId: "RemoverTarefaInicializacaoAntiga"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  PawnIODownloaded: Boolean;
  PawnIONeedsRestart: Boolean;

function IsPawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Baixando o driver PawnIO',
    'Download do instalador oficial (github.com/namazso/PawnIO.Setup), com verificação SHA-256.', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('pawnio') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#PawnIOUrl}', 'PawnIO_setup.exe', '{#PawnIOSha256}');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        PawnIODownloaded := True;
      except
        PawnIODownloaded := False;
        if DownloadPage.AbortedByUser then
          Log('Download do PawnIO cancelado pelo usuário.')
        else
          SuppressibleMsgBox('Não foi possível baixar o driver PawnIO (' + GetExceptionMessage + ').' + #13#10#13#10 +
            'O PrimePC será instalado normalmente. Sem o driver, a temperatura da CPU e os sensores da placa-mãe aparecem como "Não disponível". ' +
            'Você pode instalar o PawnIO depois pelo site oficial: https://pawnio.eu/', mbError, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and PawnIODownloaded then
  begin
    WizardForm.StatusLabel.Caption := 'Instalando o driver PawnIO...';
    if Exec(ExpandConstant('{tmp}\PawnIO_setup.exe'), '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Log('Instalador do PawnIO terminou com o código ' + IntToStr(ResultCode));
      if ResultCode = 3010 then
        PawnIONeedsRestart := True
      else if ResultCode <> 0 then
        SuppressibleMsgBox('O instalador do driver PawnIO terminou com o código ' + IntToStr(ResultCode) + '.' + #13#10#13#10 +
          'O PrimePC funciona sem ele, mas sem os sensores de baixo nível. Você pode instalá-lo depois em https://pawnio.eu/',
          mbError, MB_OK, IDOK);
    end
    else
      SuppressibleMsgBox('Não foi possível executar o instalador do PawnIO: ' + SysErrorMessage(ResultCode), mbError, MB_OK, IDOK);
  end;
end;

// Fecha o PulsoPC (nome anterior) se estiver aberto, para a pasta antiga poder ser removida.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{autopf}\PulsoPC\PulsoPC.exe')) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM PulsoPC.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM PulsoPC.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := PawnIONeedsRestart;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\PrimePC');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Deseja apagar também o histórico, os resultados de testes e as configurações do PrimePC?' + #13#10#13#10 + DataDir,
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
