# Politique de confidentialité de ZogmaDeck

*Dernière mise à jour : 17 octobre 2026*

ZogmaDeck est une application Windows qui affiche un panneau de boutons personnalisables. Cette politique s'applique à
toutes ses versions (Microsoft Store, installateur, version portable).

## Données collectées

**Aucune.** ZogmaDeck ne collecte, ne transmet et ne vend aucune donnée personnelle ni aucune donnée d'utilisation :

- aucun compte, aucune inscription ;
- aucune télémétrie, aucune statistique d'utilisation, aucun rapport de plantage envoyé ;
- aucune publicité ;
- aucun serveur ni service en ligne exploité par l'éditeur.

## Données conservées sur votre ordinateur

Toutes les données restent sur votre ordinateur, dans votre profil Windows :

- la configuration (profils, boutons, actions, apparence) ;
- les images et sons que vous ajoutez aux boutons ;
- des copies de sauvegarde de la configuration ;
- un journal technique local (conservé 7 jours), qui ne contient ni mot de passe ni contenu saisi.

Le mot de passe OBS, la clé TeamSpeak et les identifiants Discord éventuellement saisis sont chiffrés avec la
protection des données de Windows (DPAPI), lisibles uniquement par votre compte Windows. Ils ne sont jamais inclus dans
les exports ni dans le journal.

Vous pouvez supprimer ces données à tout moment : désinstaller la version Microsoft Store supprime les données qu'elle a
créées ; pour les autres versions, supprimez le dossier `%APPDATA%\ZogmaDeck`.

## Connexions réseau

ZogmaDeck n'établit des connexions réseau que pour les fonctions suivantes :

- **OBS Studio** : connexion WebSocket à l'adresse que vous indiquez (en général votre propre ordinateur ou votre réseau
  local), pour piloter OBS ;
- **TeamSpeak** : connexions locales à votre propre ordinateur (127.0.0.1) uniquement ;
- **Discord** : connexion locale à l'application Discord ; si vous configurez cette intégration, l'autorisation et son
  renouvellement contactent discord.com avec les identifiants de votre propre application Discord ;
- **mises à jour** (désactivable) : la version Microsoft Store interroge le Store ; les autres versions consultent la
  dernière version publiée sur GitHub (api.github.com), au plus deux fois par jour. Aucune information autre que la
  requête elle-même n'est envoyée ;
- **liens web** : un bouton « Ouvrir un lien » transmet l'adresse à votre navigateur par défaut ;
- **programmes lancés** : les applications que vous lancez depuis ZogmaDeck appliquent leur propre politique de
  confidentialité.

## Saisie clavier

ZogmaDeck enregistre un raccourci clavier global pour afficher son panneau. Il n'enregistre et ne lit **aucune autre
frappe** : il n'envoie des touches ou du texte que lorsque vous déclenchez un bouton configuré pour cela.

## Enfants

ZogmaDeck ne collecte aucune donnée et convient à tous les publics.

## Contact

Pour toute question : [ouvrir un ticket sur GitHub](https://github.com/tpodgro/ZOGMADECK/issues).

---

## Privacy policy (English summary)

ZogmaDeck collects **no data**: no account, no telemetry, no analytics, no ads, no online service. Configuration, images,
sounds, backups and a local technical log stay on your computer; the optional OBS password is encrypted with Windows DPAPI
and never exported or logged. Network connections are made only to the OBS address you configure, and web links are
opened in your default browser. Optional integrations connect locally to TeamSpeak and Discord; Discord authorization
contacts discord.com with the user's own Discord application credentials. Update checks (optional) query the Microsoft
Store, or GitHub for non-Store versions, at most twice a day. The app registers one global hotkey to show its panel
and does not record any other keystrokes. Uninstalling the Microsoft Store version removes the data it created. Contact:
[GitHub issues](https://github.com/tpodgro/ZOGMADECK/issues).
