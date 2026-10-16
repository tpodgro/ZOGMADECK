# ZogmaDeck

Panneau de boutons logiciel pour Windows 11, qui remplace un Stream Deck physique. Un raccourci global
(par défaut **Ctrl + Alt + Espace**) affiche un petit panneau ; chaque bouton déclenche une action ou une
séquence d'actions : lancer une application, ouvrir un fichier ou une URL, jouer un son, envoyer un
raccourci clavier ou du texte, piloter OBS Studio, attendre, changer de profil.

- Guide utilisateur : [docs/GUIDE-UTILISATEUR.md](docs/GUIDE-UTILISATEUR.md)
- Panneau en verre dépoli par défaut (voile blanc à 20 % + flou de l'arrière-plan, boutons translucides à pastille colorée), déplaçable, redimensionnable librement (les boutons se répartissent dans la place disponible), apparence personnalisable.
- Plus de 3 000 icônes : symboles Segoe Fluent Icons, emoji Windows (recherche en français), logos d'applications, ou icône extraite d'un programme installé.
- Aucun compte, aucune télémétrie, aucun service en ligne. Aucun droit administrateur.
- Anciennement **PocketDeck** : au premier lancement, ZogmaDeck reprend automatiquement les données de `%APPDATA%\PocketDeck` (configuration, sauvegardes, images, mot de passe OBS chiffré ; pas les journaux), remplace l'ancienne entrée de démarrage avec Windows, et sait importer les anciens fichiers `.pocketdeck`.

## Choix techniques

| Choix | Pourquoi |
|---|---|
| **C# / .NET 10 (LTS, support jusqu'en nov. 2028)** | .NET 8 sort du support le 10/11/2026 ; .NET 11 n'est qu'en préversion (vérifié sur `releases-index.json` officiel). |
| **WPF + thème Fluent natif** (`ThemeMode`) | Interface native Windows 11 (clair/sombre) sans navigateur embarqué. `ThemeMode` est encore marqué expérimental (WPF0001) dans .NET 10. |
| **Aucun paquet NuGet dans l'application** | Win32 par P/Invoke (`RegisterHotKey`, `SendInput`, `Shell_NotifyIcon`, DPAPI), OBS en `ClientWebSocket` + `System.Text.Json`, audio par `MediaPlayer` (Media Foundation). |
| **Rendu logiciel par défaut** | Sur le poste de développement, le rendu Direct3D de WPF affichait des fenêtres vides, y compris pour une application WPF minimale générée par `dotnet new wpf`. Le rendu logiciel suffit pour cette interface. L'option « Utiliser l'accélération graphique » existe dans les Réglages. |
| **Tests xUnit v3 sur Microsoft.Testing.Platform** | Mode de test natif du SDK .NET 10 (`global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }`). |

## Construire et lancer

Prérequis : Windows 10/11 x64 et le **SDK .NET 10** (installation sans droits administrateur :
`.\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"`, ou `winget install Microsoft.DotNet.SDK.10`).

```powershell
.\build.ps1                      # compilation Release + tests + publication portable
.\build.ps1 -Task test           # tests seulement
.\build.ps1 -Task publish        # artifacts\ZogmaDeck\ZogmaDeck.exe (autonome, aucun runtime requis)
.\build.ps1 -Task publish -FrameworkDependent   # artifacts\ZogmaDeck-fd\ (0,6 Mo, runtime .NET 10 Desktop requis)
.\build.ps1 -Task run            # lance la version Debug
```

**Installateur** : `.\build.ps1` génère aussi `artifacts\ZogmaDeck-Setup-<version>.exe` (Inno Setup 6, ~41 Mo) :
installation par utilisateur dans `%LOCALAPPDATA%\Programs\ZogmaDeck` sans droits administrateur, menu Démarrer,
raccourci Bureau et démarrage avec Windows en option, désinstallation depuis « Applications installées » (les réglages de
`%APPDATA%\ZogmaDeck` sont conservés), fermeture/relance automatique lors d'une mise à jour. Aucune dépendance à installer :
le runtime .NET est inclus dans l'exécutable. Prérequis de build : `winget install --id JRSoftware.InnoSetup -e --scope user`.

### Signature

Sans signature, Windows SmartScreen affiche « Windows a protégé votre ordinateur » au premier lancement de l'installateur
téléchargé (« Informations complémentaires » › « Exécuter quand même »). Options (documentation Microsoft,
[Code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)) :

| Option | Coût | Alerte SmartScreen |
|---|---|---|
| Microsoft Store, paquet MSIX re-signé par Microsoft | Gratuit (compte développeur gratuit) | Aucune |
| Certificat OV (DigiCert, Sectigo…) sur clé matérielle | 150–300 $/an | Oui au début, disparaît avec la réputation |
| Azure Artifact Signing | ~10 $/mois | Comme OV ; particuliers : États-Unis et Canada uniquement |
| Certificat auto-signé | — | Blocage : inutilisable en diffusion |

Une fois un certificat obtenu : `.\build.ps1 -CertThumbprint <empreinte>` signe l'exécutable puis l'installateur
(horodatage DigiCert par défaut), ou `-SignCommand '<commande signtool avec {file}>'` pour Azure Artifact Signing.

### Microsoft Store (MSIX)

`.\build.ps1 -Task msix` produit `artifacts\ZogmaDeck-<version>-x64.msix` (~59 Mo) avec l'identité réservée dans
Partner Center ([`packaging/msix/AppxManifest.xml`](packaging/msix/AppxManifest.xml)). Les outils `makeappx`/`makepri`
viennent du SDK Windows s'il est installé, sinon du paquet NuGet officiel `Microsoft.Windows.SDK.BuildTools`, téléchargé
dans `artifacts\tools`. Différences de la version empaquetée :

- les fichiers sont publiés séparément, sans exécutable unique : rien n'est extrait au lancement et les mises à jour sont
  différentielles ;
- le démarrage avec Windows utilise la tâche de démarrage du manifeste (`Windows.ApplicationModel.StartupTask`, appelée
  par COM sans projection WinRT), car les écritures dans `HKCU\…\Run` d'une application empaquetée sont virtualisées ;
  l'utilisateur peut aussi la gérer dans Paramètres › Applications › Démarrage ;
- les nouveaux fichiers de `%APPDATA%\ZogmaDeck` sont conservés par Windows dans l'espace privé de l'application.

Soumission, textes de la fiche et justification de `runFullTrust` : [docs/STORE.md](docs/STORE.md). Politique de
confidentialité : [docs/PRIVACY.md](docs/PRIVACY.md).

Commandes équivalentes : `dotnet build ZogmaDeck.slnx`, `dotnet test --project tests/ZogmaDeck.Tests`,
`dotnet publish src/ZogmaDeck.App -c Release -r win-x64 -o artifacts\ZogmaDeck`.

**Application portable** : copiez `ZogmaDeck.exe` où vous voulez et lancez-le. Pour garder aussi les données
à côté de l'exécutable (clé USB…), créez un fichier vide `ZogmaDeck.portable` dans le même dossier.

### Options de ligne de commande

| Option | Effet |
|---|---|
| `--tray` | Démarre dans la zone de notification sans afficher le panneau (utilisé par le démarrage avec Windows). |
| `--data <dossier>` | Utilise un autre dossier de données (tests, configurations séparées). |
| `--editor [0-5]` | Ouvre l'éditeur au démarrage, éventuellement sur un onglet (0 Boutons, 1 Réglages, 2 Apparence, 3 OBS, 4 Import/export, 5 Diagnostic). |

Un second lancement ne crée pas de deuxième instance : il affiche le panneau de l'instance existante
(une instance par dossier de données : `--data` permet une instance de test séparée).

## Organisation du code

```
src/ZogmaDeck.Core/        Bibliothèque sans interface (net10.0), entièrement testable
  Actions/                  DeckAction (base), métadonnées [ActionType]/[Field], registre, actions intégrées
  Execution/                SequenceRunner : ordre, erreurs (arrêter/continuer), annulation, progression
  Input/                    KeyChord et table des touches virtuelles (y compris multimédia)
  Model/                    DeckConfig, Profile, DeckButton…, configuration d'exemple
  Obs/                      Protocole obs-websocket 5 et client (connexion à la demande)
  Storage/                  Sérialisation, migration, validation, écriture atomique, import/export .zogmadeck
src/ZogmaDeck.App/         Application WPF (net10.0-windows)
  Services/                 Raccourci global, focus, SendInput, audio, shell, zone de notification, thème, secrets DPAPI, runtime
  ViewModels/  Views/       Panneau rapide et éditeur (MVVM sans framework)
  Controls/                 Capture de raccourci, glisser-déposer
  Helpers/                  Catalogues d'icônes (IconLibrary), icône d'un programme (ShellIcons), couleurs, images
  Assets/Icons/             fluent-icons.tsv, emoji.tsv, brands.tsv (voir « Données tierces »)
tests/ZogmaDeck.Tests/     Configuration, séquences, erreurs, protocole OBS (dont un vrai serveur WebSocket local), import/export, DPAPI, raccourcis
```

### Ajouter un type d'action

Aucune modification du panneau, de l'éditeur ni du format de configuration n'est nécessaire :

```csharp
[ActionType("notify", "Afficher une notification", Category = "Système", Glyph = "\uE7E7",
    Description = "Affiche un message dans la zone de notification.")]
public sealed class NotifyAction : DeckAction
{
    [Field("Message", Kind = FieldKind.Text, Order = 1)]
    public string Message { get; set; } = "";

    public override string Describe() => $"Notifier {Quote(Message)}";

    public override IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Message)) yield return "Le message est vide.";
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct) => /* … */ Task.CompletedTask;
}
```

Le registre découvre la classe par réflexion ; le JSON s'écrit `{ "type": "notify", "message": "…" }` ; l'éditeur
génère les champs depuis `[Field]` (texte, fichier, dossier, nombre, curseur, case, liste, raccourci, profil,
scène/source OBS, JSON) avec affichage conditionnel (`VisibleWhen`). Un type inconnu (version plus récente)
est conservé tel quel à l'enregistrement et signalé, jamais supprimé. Pour un assembly externe :
`ActionRegistry.RegisterAssembly(assembly)`. Si l'action a besoin d'un nouveau service Windows, ajoutez-le à
`ActionContext`.

## Données

Dossier : `%APPDATA%\ZogmaDeck` (ou `data\` en mode portable).

| Fichier | Rôle |
|---|---|
| `config.json` | Configuration versionnée (`schemaVersion`, actuellement 2 ; les fichiers v1 sont migrés automatiquement), écrite de façon atomique (fichier temporaire + `File.Replace`), sauvegarde automatique 0,7 s après la dernière modification. |
| `config.json.bak` | Version précédente, utilisée automatiquement si `config.json` est illisible (le fichier corrompu est mis de côté). |
| `backups\` | Copie quotidienne et copie avant chaque import, suppression de profil ou restauration (10 dernières). |
| `assets\` | Images des boutons (copiées à la sélection) et médias importés. |
| `secrets.dat` | Mot de passe OBS chiffré par DPAPI (compte Windows). Jamais exporté ni journalisé. |
| `logs\` | Journal local, 7 jours. |

## Tests

```
dotnet test --project tests/ZogmaDeck.Tests
```

Couverture : sérialisation et aller-retour de la configuration, types inconnus préservés, validation et
réparation, migration de schéma, écriture atomique, récupération depuis `.bak`, rotation des sauvegardes ;
séquences (ordre, arrêt/continuation sur erreur, exceptions inattendues, étapes désactivées, annulation, délai
réel annulable, focus avant les frappes, touches multimédias sans focus) ; protocole OBS (vecteur
d'authentification officiel, formes des messages, codes d'erreur), client OBS (mot de passe correct/faux/absent,
serveur injoignable, délai dépassé, déconnexion puis reconnexion, ordre des scènes) et **un vrai serveur
WebSocket local** ; import/export (médias inclus, ressources manquantes, absence de secrets, fusion avec
renommage et ré-association des liens de profil, archive malveillante) ; DPAPI ; conflit réel de raccourci
global ; présence de toutes les icônes dans la police installée.

## Données tierces

| Fichier | Source | Licence |
|---|---|---|
| `Assets/Icons/brands.tsv` | [Simple Icons](https://simpleicons.org) 16.35.0 (105 logos) | CC0-1.0. Les marques appartiennent à leurs propriétaires et ne servent qu'à identifier l'application lancée. Les marques retirées de Simple Icons à la demande de leurs propriétaires (Microsoft, Adobe, Slack…) ne sont pas incluses : utilisez « Icône d'un programme… ». |
| `Assets/Icons/emoji.tsv` | Unicode `emoji-test.txt` + noms et mots-clés français CLDR (`cldr-json`) | Licence Unicode v3 |
| `Assets/Icons/fluent-icons.tsv` | Noms des symboles, documentation Microsoft « Segoe Fluent Icons » | CC BY 4.0 (documentation Microsoft). La police elle-même est fournie par Windows, non redistribuée. |

## Limites connues

- **OBS** : testé contre un serveur obs-websocket simulé (protocole 5, y compris par un vrai WebSocket local), pas contre OBS Studio réel (non installé sur le poste de développement).
- **Frappes simulées** : Windows bloque l'envoi de touches vers une application lancée en administrateur (ZogmaDeck le détecte et l'indique) ; Ctrl+Alt+Suppr ne peut pas être simulé ; certains jeux en plein écran exclusif ignorent les entrées simulées.
- **Détection de conflits** : `RegisterHotKey` détecte les raccourcis réservés par Windows ou d'autres applications, mais pas ceux interceptés par un hook clavier bas niveau ni les raccourcis internes d'une application.
- **Formats audio** : ceux de Media Foundation (mp3, wav, wma, m4a/aac, flac). OGG dépend des extensions installées.
- **Un seul panneau à la fois**, pas de pages ni de dossiers dans un profil (utiliser l'action « Changer de profil »).
- **Emoji en monochrome** : WPF ne sait pas afficher les polices couleur ; les emoji sont dessinés en silhouette, dans la couleur du texte du bouton (cohérent avec les symboles). Pour une icône en couleur, utiliser une image ou l'icône d'un programme. Les emoji absents de la police de votre Windows sont masqués.
- **Verre dépoli** : fenêtre « layered » (WPF `AllowsTransparency`) + flou par `SetWindowCompositionAttribute` (API non documentée de Windows, stable depuis Windows 10 1803 ; sans effet si elle disparaît) + coins arrondis et lissés par DWM (`DWMWA_WINDOW_CORNER_PREFERENCE`), qui découpent aussi le flou. Leur rayon est imposé par Windows 11 (8 px) ; sans flou, le panneau utilise un arrondi de 16 px dessiné par WPF. Sous Windows 10, pas d'arrondi avec le flou. Pas d'ombre portée hors du panneau. Le fond garde une opacité minimale (1/255) pour rester cliquable et déplaçable même à 0 %.
- **Mise à l'échelle** : manifeste Per-Monitor V2 ; vérifié à 100 % sur trois écrans 1920×1080 ; les autres facteurs (125 %, 150 %…) n'ont pas pu être testés sur ce poste.
- **CPU de l'éditeur** : 0 ms sur 30 s dans la zone de notification ou panneau affiché ; éditeur ouvert non utilisé : 0 à 16 ms / 8 s sur 4 essais, mais une mesure non contrôlée a relevé ~50 ms/s lorsque l'éditeur était au premier plan (cause non isolée). Fermer l'éditeur le masque et ramène la consommation à zéro.
