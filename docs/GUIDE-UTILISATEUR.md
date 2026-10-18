# ZogmaDeck — guide utilisateur

ZogmaDeck est un panneau de boutons qui s'ouvre d'un raccourci clavier, par-dessus vos applications.
Un clic sur un bouton lance une action : ouvrir un programme, un dossier ou un site, taper un texte,
envoyer un raccourci, jouer un son, exécuter un script, lancer un jeu, piloter OBS, TeamSpeak ou Discord… ou toute
une séquence de ces actions.

> Anciennement PocketDeck : vos boutons, profils, images et mot de passe OBS sont repris automatiquement au premier lancement de ZogmaDeck.

## Démarrer

1. Installez **Zogma Deck** depuis le **Microsoft Store** (recommandé : aucune alerte de sécurité, mises à jour
   automatiques), ou lancez **ZogmaDeck-Setup.exe** (installation pour votre compte, sans droit administrateur) ou la
   version portable **ZogmaDeck.exe**. Si Windows affiche « Windows a protégé votre ordinateur » (installateur non
   signé) : **Informations complémentaires** › **Exécuter quand même**.
2. L'icône ZogmaDeck apparaît dans la zone de notification, près de l'horloge.
3. Appuyez sur **Ctrl + Alt + Espace** : le panneau s'ouvre au centre de l'écran où vous travaillez.

Au premier lancement, trois profils d'exemple sont créés : **Travail**, **Studio** (OBS) et **Multimédia**.
Modifiez-les ou supprimez-les librement.

**Fermer ≠ quitter** : Échap masque seulement le panneau. Pour arrêter vraiment ZogmaDeck :
clic droit sur l'icône de notification › **Quitter ZogmaDeck**.

## Utiliser le panneau

| Action | Comment |
|---|---|
| Exécuter un bouton | Clic, ou touche **1** à **9**, ou flèches puis **Entrée** |
| Annuler une séquence en cours | Recliquer sur le bouton (son infobulle indique l'étape en cours) |
| Changer de profil | Cliquer sur un des traits en haut du panneau (le nom du profil s'affiche au survol), **Ctrl + Tab**, ou **Ctrl + 1…9** |
| Modifier un bouton | Clic droit › **Modifier ce bouton…** |
| Ouvrir l'éditeur | Icône de notification › **Éditeur…**, **Ctrl + E**, ou clic droit sur le fond du panneau |
| Masquer | **Échap**, ou cliquer ailleurs |
| Déplacer le panneau | Glisser le fond (entre les boutons ou à côté des profils) : la position est mémorisée |
| Redimensionner le panneau | Bord droit, bord bas ou coin inférieur droit : les boutons gardent leur taille et se répartissent (rectangle vertical → une colonne, horizontal → une rangée) |
| Agrandir / réduire les boutons | **Ctrl + molette**, ou clic droit sur le fond |
| Taille automatique, colonnes, recentrer, apparence | Clic droit sur le fond du panneau |

Après une action, un liseré vert discret confirme la réussite ; en cas d'erreur, le bouton s'entoure de rouge et
le message apparaît en bas du panneau, seulement en cas d'erreur (ou dans une notification Windows si le panneau était masqué).

**Frappes clavier** : ZogmaDeck mémorise la fenêtre dans laquelle vous étiez avant d'ouvrir le panneau et lui
rend le focus avant de taper du texte ou d'envoyer un raccourci. Ouvrez donc le panneau depuis l'application
qui doit recevoir les touches.

## Créer un bouton

1. Dans le panneau, clic droit sur le fond › **Ajouter un bouton…** (la tuile **+ Ajouter** n'apparaît que si le profil est vide), ou dans l'éditeur : **Ajouter** / **Ctrl + N**.
2. Saisissez le **libellé**.
3. Onglet **Actions** › **Ajouter une action** › choisissez le type, puis remplissez les champs.
4. Cliquez sur **Tester** pour l'essayer immédiatement.

Tout est **enregistré automatiquement**. Un point d'exclamation rouge signale un bouton incomplet.

### Types d'actions

| Action | À savoir |
|---|---|
| **Lancer une application** | Chemin complet ou nom connu de Windows (`notepad.exe`). Arguments et dossier de travail facultatifs ; par défaut, le dossier du programme. |
| **Ouvrir un fichier, dossier ou URL** | `https://…`, `C:\Dossier`, un document, ou un lien système comme `ms-settings:apps-volume`. Les variables `%USERPROFILE%`, `%WINDIR%`… fonctionnent. |
| **Jouer un son** | Votre fichier (mp3, wav, wma, m4a, flac) ou un des 22 sons de la **bibliothèque intégrée** (klaxon, roulement de tambour, applaudissements, ba dum tss, grillons, trombone triste, succès, échec, compte à rebours…). Le **volume** (échelle progressive : 50 % sonne environ deux fois moins fort) se règle aussi pendant l'écoute. **Extrait** : un curseur à deux poignées sur la durée du fichier choisit le début et la fin (glisser, cliquer sur la piste, ou flèches au clavier) ; **Écouter l'extrait**, **Fichier entier**, et option « Écouter après chaque réglage ». **Si on appuie à nouveau** pendant la lecture : rejouer depuis le début, arrêter le son, ou le jouer une fois de plus par-dessus. Liens vers des banques de sons gratuites (Pixabay, Freesound). |
| **Arrêter les sons** | Coupe tous les sons lancés par ZogmaDeck (aussi dans le menu de l'icône de notification). |
| **Raccourci clavier** | Cliquez dans la zone puis appuyez sur la combinaison : **n'importe quel combo**, y compris plusieurs touches (ex. **Droite + Pavé 1**, **Ctrl + Maj + A + B**) ou un modificateur seul et de son côté (ex. **Ctrl droit**). Les touches sont appuyées dans l'ordre et relâchées dans l'ordre inverse. Les touches multimédias (lecture/pause, volume, piste suivante…) et F13–F24 sont dans « Touche spéciale ». |
| **Saisir du texte** | Accents, emoji et retours à la ligne inclus. Augmentez le délai entre caractères si l'application cible en perd. |
| **Exécuter un script** | cmd, Windows PowerShell, PowerShell 7, Python, Node.js, Bash (WSL), ou un fichier de script existant. Fenêtre masquée ou visible ; « Attendre la fin » avec durée maximale ; erreur si le script se termine avec un code différent de 0 (la dernière ligne d'erreur s'affiche). |
| **Lancer un jeu** | Steam, Epic Games, GOG Galaxy, Ubisoft Connect (liste des jeux installés détectés) ou Battle.net (code du jeu, ex. `WoW`). |
| **Steam** | Bibliothèque, amis, statut (en ligne, absent, invisible, hors ligne), Big Picture, téléchargements, captures, magasin, paramètres, quitter Steam. |
| **TeamSpeak**, **Discord** | Voir plus bas. |
| **OBS Studio** | Voir plus bas. |
| **Attendre** | En millisecondes (1000 = 1 seconde). |
| **Changer de profil** | Profil précis, suivant ou précédent. |

### Séquences

Un bouton peut enchaîner plusieurs actions, exécutées dans l'ordre. Exemple du profil Studio :
lancer OBS → attendre 6 s → choisir la scène → jouer un jingle.

- Réorganisez les étapes avec les flèches ou par **glisser-déposer**.
- **En cas d'erreur** : « Arrêter la séquence » (par défaut) ou « Continuer la séquence ».
- Décochez **Étape active** pour ignorer une étape sans la supprimer.

## Personnaliser

### Le panneau (éditeur › Apparence)

- **Fond** : verre dépoli par défaut (voile blanc à 20 % sur l'arrière-plan flouté). Réglez le **flou**, l'**opacité** et la **couleur** du voile, ou ajoutez une **image de fond**. Opacité 0 + flou désactivé = panneau entièrement transparent.
- **Style des boutons** : *Verre* (tuile translucide, icône sur une pastille à la couleur du bouton) ou *Couleur pleine*.
- **Boutons** : taille des icônes, taille des libellés, espacement, arrondi, numéros de raccourci.
- **Taille** : automatique (selon le nombre de colonnes du profil) ou libre, dès que vous redimensionnez le panneau à la souris.
- **Position** : centrée sur l'écran actif, ou mémorisée dès que vous déplacez le panneau (bouton « Recentrer »).
- **Afficher le panneau** montre le panneau à côté de l'éditeur : chaque réglage s'applique en direct.

### Les icônes (onglet Apparence d'un bouton)

| Type | Contenu |
|---|---|
| **Symbole** | Tous les symboles Segoe Fluent Icons de Windows (plus de 1 500), avec recherche (« micro », « play », « dossier »…). |
| **Emoji** | Tous les emoji de Windows, classés par catégorie, recherche en français (« fusée », « chat », « cœur »…). Affichés en silhouette, dans la couleur du texte. |
| **Logo d'appli** | Logos de 105 applications et services (OBS, Discord, Spotify, Twitch, YouTube, Steam…). Choisir un logo applique sa couleur officielle. |
| **Image** | Votre image, ou **Icône d'un programme…** (icône officielle d'une application installée, par exemple Teams ou Photoshop), ou **Icône du programme lancé** par le bouton. |
| **Texte** | Un texte court à la place d'une icône : **S1**, **S2** pour des scènes, **REC**, **LIVE**… (1 à 4 caractères conseillés, la taille s'adapte). |

- **Boutons** : glisser-déposer pour réordonner (ou **Alt + flèches**), **Ctrl + D** pour dupliquer, **Suppr** pour supprimer.
- **Apparence** (onglet du bouton) : icône, couleur (palette, code `#RRGGBB` ou **Personnaliser…** pour n'importe quelle couleur), libellé affiché ou non. Le même sélecteur de couleur existe pour le fond du panneau.
- **Après exécution** : masquer le panneau, le garder ouvert, ou suivre le réglage général.
- **Profils** (colonne de gauche) : créer, dupliquer, renommer, supprimer, réordonner ; **nombre de colonnes** et **taille des boutons** propres à chaque profil.
- **Réglages** : raccourci global, toujours au premier plan, masquage automatique, accélération graphique (tous décochés par défaut), démarrage avec Windows, mises à jour.
- **Apparence › Couleurs de l'éditeur** : thème clair/sombre/selon Windows, **couleur de fond** de l'éditeur (palette ou n'importe quelle couleur ; le texte passe en clair ou foncé selon le fond, la barre de titre suit) et **couleur principale** (cases, curseurs, boutons d'action : couleur de Windows ou personnalisée).

## OBS Studio

1. Dans OBS : **Outils › Paramètres du serveur WebSocket** › cochez **Activer le serveur WebSocket**. Notez le port (4455) et le mot de passe.
2. Dans ZogmaDeck : éditeur › **OBS** › adresse `localhost`, port, mot de passe › **Enregistrer** › **Tester la connexion**.
3. Ajoutez à un bouton l'action **OBS Studio** et choisissez la commande : changer de scène, démarrer/arrêter/basculer l'enregistrement ou le stream, pause d'enregistrement, couper/rétablir/basculer le son d'une source, sauvegarder le replay.
4. Le bouton **Liste OBS** récupère les noms exacts des scènes et sources.
5. **Requête personnalisée** (avancé) : n'importe quelle requête obs-websocket 5 avec ses données JSON.

La connexion s'établit au premier bouton OBS, sans rien faire tourner en arrière-plan. Son état est visible dans
l'éditeur › onglet OBS ; une erreur de connexion s'affiche en bas du panneau. Le mot de passe est chiffré pour votre compte Windows et n'est
jamais exporté.

## TeamSpeak

**TeamSpeak 3** (contrôle complet) : dans TeamSpeak, **Outils › Options › Modules** › activez **ClientQuery**, puis
**Paramètres** et copiez la **clé API**. Collez-la dans ZogmaDeck › éditeur › **Intégrations** › **Tester la connexion**.
L'action **TeamSpeak** permet alors : basculer/couper/réactiver le micro ou le son, basculer « absent » (avec message),
se connecter à un serveur (TeamSpeak se lance s'il est fermé), se déconnecter, aller dans un salon, changer de pseudo.

**TeamSpeak 5 / 6** : TeamSpeak n'accepte que des « boutons » d'applications autorisées.
1. Éditeur › **Intégrations** › **Autoriser dans TeamSpeak**, puis acceptez la demande dans TeamSpeak.
2. Créez dans ZogmaDeck les boutons voulus (action **TeamSpeak**, version 5/6, commande « couper le micro »…).
3. Dans TeamSpeak › **Paramètres › Raccourcis**, ajoutez un raccourci pour l'action voulue et, au moment de choisir la
   touche, appuyez sur le bouton correspondant de ZogmaDeck. Une seule fois par bouton.

La connexion à un serveur fonctionne sans association (lien `ts3server://`).

## Discord

Discord ne laisse piloter son application que par des applications enregistrées chez lui. Configuration unique :
1. Ouvrez le [portail développeur de Discord](https://discord.com/developers/applications) › **New Application** (nom libre).
2. Onglet **OAuth2** : copiez le **Client ID** et le **Client Secret** dans ZogmaDeck › éditeur › **Intégrations**, et
   ajoutez la redirection `http://localhost`. Si Discord le demande, ajoutez votre compte dans **App Testers**.
3. Lancez l'application Discord, cliquez sur **Autoriser dans Discord** et acceptez.

L'action **Discord** permet : basculer/couper/réactiver le micro, la sourdine, rejoindre un salon vocal (identifiant
du salon : mode développeur de Discord › clic droit › Copier l'identifiant), le quitter, régler le volume du micro ou
du son. ZogmaDeck reste connecté à Discord tant qu'il tourne : Discord annule les réglages d'une application qui se
déconnecte.

## Mises à jour

- **Version Microsoft Store** : le Store met ZogmaDeck à jour automatiquement ; ZogmaDeck le signale aussi.
- **Installateur ou version portable** : ZogmaDeck vérifie au plus deux fois par jour s'il existe une version plus
  récente sur GitHub (désactivable dans **Réglages › Mises à jour**).

Quand une version est disponible, une notification s'affiche et le menu de l'icône de notification commence par
**Mettre à jour…**.

## Import, export et sauvegardes

- **Exporter** : tous les profils ou seulement celui affiché, avec ou sans réglages. Le fichier `.zogmadeck` contient les images et sons utilisés.
- **Importer** : ZogmaDeck affiche le contenu et la liste des **ressources introuvables** sur ce poste avant toute modification, et signale les **scripts** contenus (n'importez que des fichiers de confiance). Choisissez **Ajouter** ou **Remplacer**. **Un import ne déclenche jamais aucune action.**
- **Sauvegardes automatiques** : une par jour et avant chaque import, suppression de profil ou restauration ; bouton **Restaurer** dans l'onglet Import / export.

## Mode portable

Copiez `ZogmaDeck.exe` sur une clé USB et créez à côté un fichier vide nommé `ZogmaDeck.portable` : la
configuration sera stockée dans un dossier `data` à côté de l'exécutable. (Le mot de passe OBS, chiffré pour un
compte Windows, devra être ressaisi sur un autre PC.)

## Dépannage

| Problème | Solution |
|---|---|
| « Raccourci indisponible » au démarrage | Une autre application utilise déjà la combinaison : choisissez-en une autre dans **Réglages** (elle est vérifiée immédiatement). En attendant, cliquez sur l'icône de notification. |
| Les touches ne sont pas reçues | L'application cible tourne peut-être en administrateur (ZogmaDeck l'indique) ; certains jeux ignorent les touches simulées. |
| « Impossible de joindre OBS » | Lancez OBS et vérifiez que le serveur WebSocket est activé, l'adresse et le port. |
| « Mot de passe OBS incorrect » | Ressaisissez-le dans l'onglet OBS. |
| « Fichier introuvable » | Le fichier a été déplacé ou n'existe pas sur ce poste : corrigez le chemin dans l'éditeur. |
| Fenêtres vides | Laissez **Utiliser l'accélération graphique** décoché (Réglages). |
| « TeamSpeak 3 ne répond pas » | Lancez TeamSpeak 3 et activez le module ClientQuery (Outils › Options › Modules). |
| « Clé API TeamSpeak 3 refusée » | Recopiez la clé depuis les paramètres du module ClientQuery. |
| Un bouton TeamSpeak 5/6 ne fait rien | Associez-le dans TeamSpeak › Paramètres › Raccourcis (voir plus haut). |
| « Discord ne répond pas » | Lancez l'application Discord (pas la version navigateur). |
| « Interpréteur introuvable » (script) | Installez Python, Node.js, PowerShell 7 ou WSL selon le langage choisi. |
| Un jeu n'apparaît pas dans la liste | Saisissez son identifiant (Steam : numéro visible dans l'adresse de sa page du magasin). |
| Comprendre une erreur | Éditeur › **Diagnostic** › **Ouvrir les journaux**. |
