# Fonctionnalités de SoluPulse

SoluPulse surveille un PC Windows en continu, explique chaque problème en français simple et propose des corrections que l'utilisateur valide. Tout reste sur le PC : aucune donnée n'est envoyée.

## 1. Surveillance en continu

Un service Windows tourne en arrière-plan, même quand l'application est fermée.

| Domaine | Ce qui est mesuré | Fréquence |
|---|---|---|
| Processeur | Utilisation, température, fréquence, consommation | 30 s |
| Mémoire | Utilisation, mémoire disponible, barrettes et emplacements libres | 30 s / 6 h |
| Cartes graphiques | Température, charge, mémoire vidéo, consommation | 30 s |
| Carte mère | Températures, ventilateurs | 30 s |
| Disques | Espace libre par volume, température, usure des SSD, état selon Windows, attributs SMART (secteurs réalloués, en attente, illisibles), heures de fonctionnement, activité | 30 s (température, activité), 5 min (espace), 6 h (santé) |
| Batterie | Charge, santé, capacité d'origine et actuelle, cycles, branchement secteur | 1 h |
| Réseau | Latence et perte vers la box et vers Internet, résolution DNS, signal Wi-Fi, débit reçu et envoyé | 2 min |
| Système | Écrans bleus, arrêts brutaux, plantages d'applications, erreurs disque, périphériques en erreur, services en panne, programmes au démarrage, durée de démarrage, temps depuis le dernier redémarrage, mode d'alimentation | 10 min (événements), 1 h (inventaire) |
| Windows Update | Mises à jour en attente, redémarrage requis | 24 h |
| Sécurité | Antivirus actif et à jour, définitions et dernière analyse de Microsoft Defender, pare-feu par type de réseau, contrôle de compte d'utilisateur, démarrage sécurisé, chiffrement BitLocker, puce TPM | 30 min |

## 2. Détection des problèmes : 41 règles

Chaque règle produit une alerte expliquée (ce qui se passe, cause probable, explication technique) avec un niveau : information, attention ou critique.

- **Matériel (14)** : disque système ou secondaire presque plein, disque en mauvaise santé, panne de disque annoncée, secteurs défectueux ou illisibles, SSD très usé, disque trop chaud, processeur trop chaud, processeur chaud avec ventilateur lent, carte graphique trop chaude, batterie usée ou en fin de vie, mémoire presque saturée.
- **Système et logiciels (13)** : écran bleu récent, arrêts brutaux répétés, plantages fréquents d'applications, erreurs de lecture ou d'écriture disque, périphérique en erreur, service Windows en panne, mises à jour en attente depuis longtemps, redémarrage en attente, PC jamais redémarré, trop de programmes au démarrage, démarrage lent, processeur très sollicité, mode économie d'énergie sur secteur.
- **Réseau (6)** : box injoignable, Internet coupé alors que la box répond, noms de sites non résolus (DNS), connexion lente, box lente à répondre, signal Wi-Fi faible.
- **Sécurité (8)** : aucun antivirus actif, antivirus pas à jour, définitions Defender anciennes, pas d'analyse depuis deux semaines, pare-feu coupé, contrôle de compte désactivé, démarrage sécurisé désactivé, disque non chiffré.

Les seuils des règles sont modifiables dans les paramètres.

## 3. Corrections validées par l'utilisateur : 21 corrections et 8 guides

Aucune correction n'est lancée sans validation. La fiche de correction indique ce qui va être fait, le risque, la durée, et si l'action est annulable. Un point de restauration Windows est créé avant chaque correction système de risque moyen.

- **Nettoyage** : fichiers temporaires, cache de Windows Update, cache d'optimisation de la distribution, corbeille.
- **Système** : désactiver un programme au démarrage (annulable), repasser en mode d'alimentation normal (annulable), optimiser le disque système, réparer les fichiers de Windows (SFC), réparer l'image de Windows (DISM), vérifier le disque au prochain démarrage, redémarrer un service Windows (annulable), réinitialiser Windows Update (annulable).
- **Réseau** : vider le cache DNS, réinitialiser Winsock, réinitialiser TCP/IP, renouveler l'adresse IP, redémarrer la carte réseau, changer de serveur DNS (annulable).
- **Sécurité** : réactiver le pare-feu (annulable), mettre à jour l'antivirus, lancer une analyse antivirus rapide.
- **Guides pas à pas** pour ce que l'utilisateur fait lui-même : refroidissement, remplacement de disque, batterie, sauvegarde, antivirus, contrôle de compte, démarrage sécurisé, chiffrement.

Après une correction, SoluPulse vérifie que le problème a bien disparu.

## 4. Application de bureau

- **Tableau de bord** : score de santé sur 100, score par domaine, valeurs en direct rafraîchies toutes les 15 secondes, alertes actives.
- **Mon PC** : une fiche par composant (ordinateur et BIOS, sécurité, processeur, mémoire et chaque barrette, cartes graphiques, disques, espace disque, batterie, réseau, périphériques : écrans, son, caméras, Bluetooth, lecteurs biométriques, imprimantes) et le tableau de tous les capteurs.
- **Alertes** : liste des alertes actives et résolues, explication simple et technique, corrections proposées.
- **Historique** : graphique de chaque mesure sur 24 heures, 7 jours, 30 jours, 90 jours ou un an.
- **Journal des corrections** : qui a validé quoi et quand, bouton « Annuler » pour les corrections annulables, vérification de l'intégrité du journal.
- **Inventaire système** : programmes au démarrage, services en panne, programmes gourmands, mises à jour en attente, événements récents.
- **Paramètres** : notifications et niveau minimum, durée de conservation du détail des mesures, tests de connexion Internet, seuils des règles.
- **Notifications Windows** avec boutons « Voir » et « Corriger », icône dans la zone de notification colorée selon l'état du PC, diagnostic complet à la demande.
- Thème clair ou sombre selon Windows ; l'état n'est jamais donné par la couleur seule.

## 5. Sécurité et confidentialité

- Le service est la seule partie qui a des droits administrateur ; l'application n'en a aucun.
- Communication par canal nommé local protégé : aucun port réseau ouvert. Seule l'application SoluPulse installée, lancée par un utilisateur connecté au PC, est acceptée.
- Catalogue de corrections fermé : le service n'exécute que les actions prévues, jamais de commande libre.
- Journal d'audit chaîné : toute modification après coup est détectée.
- Aucune donnée ne quitte le PC ; ni numéros de série, ni adresses MAC, ni contenu des fichiers ne sont relevés.
- Historique conservé en détail 90 jours par défaut, puis en moyennes horaires jusqu'à deux ans.

## 6. Installation

- Installeur unique (service, application, pilote PawnIO pour les capteurs du processeur), mise à jour par-dessus une version existante.
- Logiciel libre sous licence GPL-3.0 ; signature du code prévue avec SignPath Foundation.
