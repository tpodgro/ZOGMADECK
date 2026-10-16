; Installateur ZogmaDeck (Inno Setup 6) — compilé par build.ps1 -Task installer
; Installation par utilisateur, sans droits administrateur. L'application est autonome (runtime .NET inclus) :
; aucune autre dépendance à installer.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\ZogmaDeck"
#endif

[Setup]
AppId={{6B1F2C4E-9D3A-4E57-8C21-5A7E0D9B3F12}
AppName=ZogmaDeck
AppVersion={#AppVersion}
AppVerName=ZogmaDeck {#AppVersion}
AppPublisher=ZogmaDeck
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Installateur de ZogmaDeck
; Par utilisateur : %LOCALAPPDATA%\Programs\ZogmaDeck, aucune élévation (pas d'UAC)
PrivilegesRequired=lowest
DefaultDirName={autopf}\ZogmaDeck
DisableProgramGroupPage=yes
DefaultGroupName=ZogmaDeck
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\ZogmaDeck.App\Assets\ZogmaDeck.ico
UninstallDisplayIcon={app}\ZogmaDeck.exe
UninstallDisplayName=ZogmaDeck
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
OutputDir=..\artifacts
OutputBaseFilename=ZogmaDeck-Setup-{#AppVersion}
; Mise à jour : ferme ZogmaDeck s'il tourne, puis le relance
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked
Name: "startup"; Description: "Lancer ZogmaDeck à l'ouverture de session (dans la zone de notification)"; GroupDescription: "Démarrage :"

[Files]
Source: "{#SourceDir}\ZogmaDeck.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ZogmaDeck"; Filename: "{app}\ZogmaDeck.exe"
Name: "{autodesktop}\ZogmaDeck"; Filename: "{app}\ZogmaDeck.exe"; Tasks: desktopicon

[Registry]
; Même entrée que celle gérée par ZogmaDeck (Réglages › Démarrage) : l'application la reconnaît et la garde à jour.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ZogmaDeck"; \
    ValueData: """{app}\ZogmaDeck.exe"" --tray"; Tasks: startup; Flags: uninsdeletevalue
; La désinstallation retire l'entrée même si elle a été activée depuis l'application.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "ZogmaDeck"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\ZogmaDeck.exe"; Description: "Lancer ZogmaDeck"; Flags: nowait postinstall skipifsilent

[Messages]
fr.FinishedLabel=ZogmaDeck est installé. Appuyez sur Ctrl+Alt+Espace pour afficher le deck ; l'éditeur est accessible depuis l'icône de la zone de notification.
