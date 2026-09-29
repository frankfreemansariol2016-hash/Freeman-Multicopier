; ---------------------------------------------------------------------------
;  Freeman MULTICOPIER - instalador.
;
;  Los tres valores de abajo los pasa build.ps1 con /D, para que el numero de
;  version y las carpetas no esten escritos a mano en dos sitios distintos.
; ---------------------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef StageDir
  #define StageDir "..\build\stage"
#endif
#ifndef OutDir
  #define OutDir "..\build\out"
#endif

#define AppName    "Freeman MULTICOPIER"
#define AppExe     "FreemanMultiCopier.exe"
#define AppPub     "Freeman"
#define AppVersionFull AppVersion

[Setup]
AppId={{7C4A2E19-8B03-4D6F-9E51-2A7C63B4D118}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPub}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir={#OutDir}
; El nombre del instalador es el que ve el cliente, asi que va aqui y no se
; renombra a mano despues: si hay que renombrarlo, el nombre se queda viejo.
OutputBaseFilename=Freeman MULTICOPIER - Instalador v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Un instalador que exige 64 bits y Windows 10 o mas. La aplicacion es .NET 10
; con WPF, que en Windows 7 ni siquiera arranca.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile={#StageDir}\app.ico
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Default.isl"

[Tasks]
; El acceso del Menu de Inicio se crea siempre (DisableProgramGroupPage=yes), asi que
; no hay tarea para el: solo la del Escritorio, que es opcional de verdad.
Name: "desktopicon"; Description: "Crear un icono en el &Escritorio"; GroupDescription: "Accesos directos:"

[Files]
; Solo el ejecutable. Al ser de un solo archivo y autocontenido, no hay mas nada
; que instalar: ni carpetitas de recursos ni librerias de .NET.
Source: "{#StageDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir {#AppName} ahora"; Flags: nowait postinstall skipifsilent

; Sin seccion [Code] a proposito. Se habia puesto un aviso al desinstalar diciendo que la
; carpeta %LOCALAPPDATA%\FreemanMultiCopier se conserva, y fue un error: ese MessageBox
; se cuelga en las desinstalaciones automaticas y deja el uninstaller colgado hasta que
; alguien pulse Enter. La carpeta pesa unos cuantos kilobytes de registro y es historial
; del cliente, no del programa; se explica en el manual, no con un cuadro de dialogo.
