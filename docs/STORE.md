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

> ZogmaDeck transforme votre écran en Stream Deck : un panneau de boutons personnalisables, toujours à portée de
> raccourci clavier (Ctrl+Alt+Espace par défaut), qui lance vos actions en un clic.
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

**Mots-clés** : stream deck, macro, raccourcis, lanceur, OBS, streaming, productivité

**Captures d'écran** : au moins une, 1366 × 768 px ou plus (PNG). Suggestions : le panneau sur un fond d'écran neutre,
l'éditeur (onglet Boutons), l'onglet Apparence, un bouton OBS.

**Logo de la fiche** : facultatif (le Store utilise les visuels du paquet). Un carré 300 × 300 px peut être produit à partir
de `tools/generate-icons.py` si demandé.

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
- Les utilisateurs de l'installateur ou de la version portable gardent leurs données dans `%APPDATA%\ZogmaDeck` ; la
  version Store les lit à cet emplacement au premier lancement (les nouveaux fichiers qu'elle crée sont conservés dans
  son espace privé et supprimés à sa désinstallation).
