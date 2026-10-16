# Publication sur le Microsoft Store

Fiche de soumission Partner Center pour ZogmaDeck (produit « Application MSIX ou PWA »).

| Élément | Valeur |
|---|---|
| ID Store | 9PJJ1JLZDF4Z |
| Package/Identity/Name | TOMAZKI.ZogmaDeck |
| Package/Identity/Publisher | CN=683599A3-0189-4C97-92C8-4F14962DC73A |
| PublisherDisplayName | TOMAZKI |
| Package Family Name | TOMAZKI.ZogmaDeck_cj56jtk60z7yt |
| Politique de confidentialité | https://github.com/tpodgro/ZOGMADECK/blob/main/docs/PRIVACY.md |

## 1. Construire le paquet

```powershell
.\build.ps1 -Task msix
```

Produit `artifacts\ZogmaDeck-<version>-x64.msix`, **non signé** : le Store signe lui-même les paquets soumis.
Manifeste : [`packaging/msix/AppxManifest.xml`](../packaging/msix/AppxManifest.xml) ; visuels :
`packaging/msix/Assets` (générés par `python tools/generate-icons.py`). Chaque nouvelle soumission doit porter un numéro de
version supérieur : modifiez `<Version>` dans `Directory.Build.props` (le 4e nombre du paquet reste 0).

Pour tester localement avant soumission, un paquet signé par un certificat de test est nécessaire :

```powershell
.\build.ps1 -Task msix -MsixTestCertificate          # crée le certificat de test au besoin et signe le paquet
# Une fois, dans PowerShell lancé en administrateur : approuver le certificat de test sur cet ordinateur
Import-Certificate -FilePath artifacts\ZogmaDeck-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage artifacts\ZogmaDeck-<version>-x64.msix
```

Désinstallez ce paquet de test (Paramètres › Applications) avant d'installer la version du Store, et retirez le
certificat de test de « Personnes autorisées » quand vous n'en avez plus besoin.

## 2. Soumission (Partner Center › ZogmaDeck › Soumission)

### Tarification et disponibilité
Gratuit ; marchés au choix ; visibilité publique.

### Propriétés
- Catégorie : **Productivité** (sous-catégorie libre) ; ou Utilitaires et outils.
- Confidentialité : « Oui, cette application accède à des informations personnelles » → **Non** (aucune donnée collectée) ;
  URL de la politique de confidentialité ci-dessus.
- Site web / contact du support : https://github.com/tpodgro/ZOGMADECK/issues
- Configuration système : clavier et souris recommandés.

### Classification par âge
Questionnaire IARC : aucune violence, aucun contenu sexuel, aucun achat, **aucun échange entre utilisateurs**, aucun
partage de position, accès à Internet non restreint uniquement via le navigateur ouvert par l'utilisateur
(« ouvrir un lien »). Résultat attendu : **3+ / PEGI 3**.

### Paquets
Téléverser `ZogmaDeck-<version>-x64.msix`. Familles d'appareils : **Windows 10/11 Desktop** uniquement.

### Fiche Store (français)

**Description**

> ZogmaDeck affiche à l'écran un panneau de boutons personnalisables, toujours à portée de raccourci clavier
> (Ctrl+Alt+Espace par défaut), qui lance vos actions en un clic : l'équivalent logiciel d'un pupitre de streaming.
>
> • Lancer des applications, ouvrir des fichiers, dossiers et sites web
> • Envoyer des raccourcis clavier et les touches multimédias (lecture/pause, piste suivante, volume)
> • Saisir du texte, jouer des sons
> • Piloter OBS Studio : changer de scène, lancer ou arrêter l'enregistrement et le stream, couper une source
> • Enchaîner plusieurs actions dans un seul bouton, avec délais et gestion des erreurs
> • Profils multiples, icônes (symboles Windows, emoji, logos d'applications, vos images), couleurs, effet verre
> • Panneau déplaçable et redimensionnable, thème clair ou sombre, démarrage avec Windows
> • Import/export de vos configurations
>
> Aucune inscription, aucune publicité, aucune collecte de données : tout reste sur votre PC.

**Fonctionnalités** (une par ligne)

> Panneau de boutons affiché par raccourci clavier global
> Lancement d'applications, fichiers, dossiers et liens
> Raccourcis clavier et touches multimédias
> Contrôle d'OBS Studio (scènes, enregistrement, stream, sources)
> Séquences d'actions avec délais
> Profils, icônes, couleurs et apparence verre personnalisables
> Import et export des configurations
> Aucune collecte de données

**Mots-clés** : macro, raccourcis, lanceur, boutons, OBS, streaming, productivité. Évitez « Stream Deck » (marque
déposée d'Elgato/Corsair) dans le nom, la description et les mots-clés : risque de refus ou de réclamation.

**Nom** : le nom affiché dans le paquet (`DisplayName` du manifeste, « ZogmaDeck ») doit être l'un des noms réservés
dans Partner Center › Gérer les noms d'application, sinon le paquet est refusé au téléversement.

**Captures d'écran** : au moins une, PNG, 1366 × 768 px ou plus (jusqu'à 10). Les éléments importants doivent être dans
les deux tiers supérieurs (le Store peut superposer du texte sur le tiers inférieur). Suggestions : le panneau, l'éditeur
(onglet Boutons), l'onglet Apparence, un bouton OBS.

**Icône de vignette 1:1 (300 × 300 px)** : recommandée ; générée par `tools/generate-icons.py` (fonction `logo(300)`).

### Notes pour la certification — justification de `runFullTrust`

À coller dans « Remarques pour la certification » (en anglais, lu par l'équipe de certification) :

> ZogmaDeck is a WPF desktop application (.NET 10, Win32) packaged with MSIX. It requires the runFullTrust restricted
> capability because it is a classic desktop app that relies on Win32 APIs not available to app-container apps:
> RegisterHotKey (global shortcut that shows its button panel), SendInput (sends the keyboard shortcuts, media keys and
> text configured by the user), SetWinEventHook/SetForegroundWindow (returns focus to the user's previous window before
> sending keys), Shell_NotifyIcon (notification-area icon), and process launching of user-chosen programs, files and
> URLs. It collects no data and makes network connections only to the OBS Studio WebSocket address configured by the
> user. A startup task (disabled by default) can be enabled from the app's settings to start it in the notification area
> at sign-in.
>
> How to test: launch ZogmaDeck, press Ctrl+Alt+Space to show the panel; sample profiles are created on first run.
> Right-click the notification-area icon › "Éditeur…" to edit buttons. No account or credentials are needed.

## 3. Après publication

- Les mises à jour se publient en créant une nouvelle soumission avec un paquet de version supérieure.
- Les utilisateurs de l'installateur gardent leurs données : la version Store utilise et met à jour les fichiers déjà
  présents dans `%APPDATA%\ZogmaDeck` (vérifié : profils, boutons et mot de passe OBS repris). Les fichiers qu'elle crée
  elle-même sont conservés par Windows dans son espace privé et supprimés à sa désinstallation.

## Vérifications effectuées sur le paquet (Windows 11 25H2, certificat de test)

| Vérification | Résultat |
|---|---|
| Installation, nom de famille du paquet | `TOMAZKI.ZogmaDeck_cj56jtk60z7yt`, identique à Partner Center |
| Lancement depuis le menu Démarrer | Panneau affiché, identité de paquet détectée, prêt en ~0,4–0,9 s, ~38 Mo |
| Réglages › démarrage avec Windows | État Windows de la tâche : 0 → 2 (activée) → 0 (désactivée) |
| Démarrage désactivé par l'utilisateur dans Paramètres | Case décochée et message explicatif ; le choix de l'utilisateur est respecté |
| Programme lancé depuis ZogmaDeck | S'exécute hors du paquet (sans virtualisation) |
| Données existantes de `%APPDATA%\ZogmaDeck` | Reprises et intactes |
| Lancement réel à l'ouverture de session (mode discret) | À vérifier : nécessite de fermer puis rouvrir la session |
