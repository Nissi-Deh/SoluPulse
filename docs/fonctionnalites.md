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
| Disques | Espace libre par volume, température, usure des SSD, état selon Windows, attributs SMART (secteurs réalloués, en attente, illisibles, erreurs non corrigées, erreurs de câble), page de santé NVMe (réserve, avertissement critique, erreurs de données, arrêts non sécurisés), firmwares connus comme dangereux, Windows installé sur un disque dur classique, volume à vérifier, heures de fonctionnement, activité | 30 s (température, activité), 5 min (espace), 6 h (santé) |
| Occupation du disque système | Taille de chaque dossier personnel (Documents, Téléchargements…), disque virtuel de Docker, caches des outils de développement, installeurs de mises à jour, veille prolongée, mémoire virtuelle, Windows.old, fichiers temporaires, corbeille, cache de Windows Update | 24 h, et après chaque correction d'espace disque |
| Batterie et onduleur | Charge, santé, capacité d'origine et actuelle, cycles, branchement secteur ; sur un PC fixe, onduleur relié en USB (charge, autonomie, santé de sa batterie) | 1 h |
| Alimentation | Tensions 12 V, 5 V et 3,3 V des PC fixes dont la carte mère les mesure | 30 s |
| Réseau | Latence et perte vers la box et vers Internet, résolution DNS, signal Wi-Fi, débit reçu et envoyé | 2 min |
| Système | Écrans bleus et leur code, arrêts brutaux et vraies coupures de courant (sans écran bleu ni appui long sur le bouton), plantages d'applications, erreurs et resets de disque, vérifications du disque au démarrage, durée du bridage du processeur, erreurs matérielles WHEA (hors erreurs PCI Express corrigées), plantages du pilote graphique, mémoire virtuelle faible, passages secteur/batterie, horloge déréglée, coupures Wi-Fi, périphériques en erreur, services en panne, programmes au démarrage, durée de démarrage, temps depuis le dernier redémarrage, mode d'alimentation | 10 min (événements), 1 h (inventaire) |
| Programmes | Programme inconnu qui fait travailler le processeur pendant que le PC est verrouillé, programme dont la mémoire grossit sans arrêt | 5 min |
| Protection | Protection du système et nombre de points de restauration, environnement de récupération, clé de récupération BitLocker, sauvegardes (Historique des fichiers, OneDrive, logiciels de sauvegarde), état du magasin des composants (DISM), fin de support de la version de Windows, certificats Secure Boot 2023, âge du BIOS, microcode des processeurs Intel de 13e et 14e génération | 6 h |
| Tendances | Hausse des compteurs d'erreurs des disques sur 30 jours, réchauffement du processeur au repos d'un mois sur l'autre, jours avant que le disque C: soit plein, temps passé batterie pleine, points de restauration effacés d'un coup | 3 h |
| Windows Update | Mises à jour en attente, redémarrage requis, date de la dernière mise à jour réussie, mises à jour en échec | 24 h |
| Sécurité | Antivirus actif et à jour, définitions et dernière analyse de Microsoft Defender, pare-feu par type de réseau, contrôle de compte d'utilisateur, démarrage sécurisé, chiffrement BitLocker, puce TPM | 30 min |
| Menaces | Exclusions anormales de Defender, Bureau à distance et échecs de connexion, fichier hosts, proxy et DNS détournés, Wi-Fi ouverts enregistrés, programmes au démarrage et tâches planifiées suspects, logiciels à faille connue (WinRAR, 7-Zip, VLC), notifications et extensions des navigateurs | 1 h |
| Clés USB | Virus « raccourci » à la racine des clés et cartes mémoire branchées | 1 min |
| Rançongiciels | Fichiers renommés en masse avec une nouvelle extension, notes de rançon, dans les dossiers personnels (seuls les noms sont observés) | en continu |

## 2. Détection des problèmes : 103 règles

Chaque règle produit une alerte expliquée (ce qui se passe, cause probable, explication technique) avec un niveau : information, attention ou critique.

- **Matériel (40)** : disque système ou secondaire presque plein, disque en mauvaise santé, panne de disque annoncée, secteurs défectueux ou illisibles et secteurs qui augmentent, erreurs non corrigées, erreurs de câble, SSD très usé, SSD en état critique, réserve du SSD entamée, erreurs de données du SSD, firmware dangereux, disque qui ne répond plus par moments, disque C: qui se remplit vite, Windows sur un disque dur classique, disque trop chaud, processeur trop chaud, qui chauffe plus qu'avant, qui chauffe sans rien faire ou qui est bridé, processeur Intel exposé à la dégradation, processeur chaud avec ventilateur lent, carte graphique trop chaude, pilote graphique qui plante, erreurs matérielles, écrans bleus liés à la mémoire, tension de l'alimentation hors tolérance, batterie de l'onduleur usée, coupures de courant répétées sans onduleur, chargeur abîmé, pile de la carte mère usée, batterie usée, en fin de vie ou gardée pleine en permanence, mémoire presque saturée, BIOS jamais mis à jour.
- **Système et logiciels (27)** : écran bleu récent, même écran bleu qui revient, écran bleu lié au disque, arrêts brutaux répétés, système de fichiers qui s'abîme, plantages fréquents d'applications, erreurs de lecture ou d'écriture disque, fichiers de Windows abîmés, périphérique en erreur, service Windows en panne, mises à jour en attente depuis longtemps, aucune mise à jour depuis deux mois, mises à jour en échec, Windows en fin de support ou bientôt, redémarrage en attente, PC jamais redémarré, trop de programmes au démarrage, démarrage lent, processeur très sollicité, programme qui consomme de plus en plus de mémoire, mémoire virtuelle épuisée, mode économie d'énergie sur secteur, points de restauration désactivés, outil de secours de Windows désactivé, aucune sauvegarde, sauvegarde arrêtée.
- **Réseau (11)** : box injoignable, Internet coupé alors que la box répond, noms de sites non résolus (DNS), connexion lente, box lente à répondre, signal Wi-Fi faible, Wi-Fi qui se coupe souvent, fichier hosts modifié, proxy configuré, serveurs DNS inconnus, connexion automatique à des Wi-Fi ouverts.
- **Sécurité (22)** : aucun antivirus actif, antivirus pas à jour, définitions Defender anciennes, pas d'analyse depuis deux semaines, exclusions anormales de Defender, pare-feu coupé, contrôle de compte désactivé, démarrage sécurisé désactivé, certificats Secure Boot 2023 absents, disque non chiffré, disque chiffré sans clé de récupération, rançongiciel au travail, points de restauration effacés d'un coup, mineur de cryptomonnaie caché, clé USB infectée, programme ou tâche planifiée suspect, logiciel à faille connue, notifications et extensions de navigateur douteuses, Bureau à distance activé ou attaqué.

Les règles de tendance surveillent la vitesse à laquelle un compteur augmente, pas seulement sa valeur : c'est ce qui prédit le mieux une panne de disque.

Les seuils des règles sont modifiables dans les paramètres.

## 3. Corrections : 34 corrections et 35 guides

La fiche de correction indique ce qui va être fait, le gain estimé sur ce PC, le risque, la durée, et si l'action est annulable. Une alerte ne propose que les actions utiles sur ce PC (par exemple, le guide Docker seulement si Docker est installé). Un point de restauration Windows est tenté avant chaque correction qui modifie Windows ; à partir du risque moyen, la correction n'est pas appliquée sans lui.

**Corrections automatiques sans risque** (désactivables dans les paramètres) : quand une alerte le justifie, SoluPulse supprime seul les fichiers temporaires, les installeurs de mises à jour déjà téléchargés et le cache d'optimisation de la distribution, au plus une fois par jour. Chaque exécution est inscrite au journal. Toutes les autres corrections attendent la validation de l'utilisateur.

- **Espace disque** : déplacer un dossier personnel (Documents, Téléchargements, Bureau, Images, Vidéos, Musique) vers un autre disque avec la méthode de Windows (annulable) ; supprimer de gros fichiers cochés dans une liste ; vider les caches des outils de développement (pip, npm, Yarn, NuGet, Gradle…) ; supprimer les installeurs de mises à jour d'applications ; supprimer les anciennes versions laissées par Windows Update ; désactiver la veille prolongée (annulable) ; guide pour réduire ou déplacer le disque de Docker.
- **Nettoyage** : fichiers temporaires, cache de Windows Update, cache d'optimisation de la distribution, corbeille.
- **Système** : désactiver un programme au démarrage (annulable), repasser en mode d'alimentation normal (annulable), désactiver le démarrage rapide (annulable), optimiser le disque système, réparer les fichiers de Windows (SFC), réparer l'image de Windows (DISM), vérifier le disque au prochain démarrage, redémarrer un service Windows (annulable), réinitialiser Windows Update (annulable), activer la protection du système, réactiver l'environnement de récupération (annulable), relancer la détection du matériel, installer le pilote que Windows Update propose pour un périphérique en erreur (point de restauration obligatoire).
- **Réseau** : vider le cache DNS, réinitialiser Winsock, réinitialiser TCP/IP, renouveler l'adresse IP, redémarrer la carte réseau, changer de serveur DNS (annulable).
- **Sécurité** : réactiver le pare-feu (annulable), mettre à jour l'antivirus, lancer une analyse antivirus rapide, couper le Bureau à distance (annulable), nettoyer une clé USB infectée par le virus « raccourci » (fichiers piégés en quarantaine, dossiers rendus visibles, annulable).
- **Guides pas à pas** pour ce que l'utilisateur fait lui-même : refroidissement, remplacement de disque, batterie, sauvegarde, antivirus, contrôle de compte, démarrage sécurisé, chiffrement, disque de Docker ; onduleur et batterie d'onduleur, alimentation, chargeur, pile de la carte mère, limite de charge, test de la mémoire, passage à un SSD, câble du disque, firmware du disque, mise à jour du BIOS, pilotes graphique et Wi-Fi ; exclusions de Defender, rançongiciel, programme suspect, logiciels à mettre à jour, nettoyage du navigateur, clé de récupération BitLocker, certificats Secure Boot 2023, proxy, DNS et hosts, Wi-Fi ouverts ; fin de support de Windows, clé USB de récupération, programme qui consomme la mémoire, trouver le bon pilote.

Après une correction, SoluPulse vérifie que le problème a bien disparu.

## 4. Application de bureau

- **Tableau de bord** : score de santé sur 100, score par domaine, valeurs en direct rafraîchies toutes les 15 secondes, alertes actives.
- **Mon PC** : une fiche par composant (ordinateur et BIOS, sécurité, processeur, mémoire et chaque barrette, cartes graphiques, disques, espace disque avec ce qui occupe le disque système, batterie, réseau, périphériques : écrans, son, caméras, Bluetooth, lecteurs biométriques, imprimantes) et le tableau de tous les capteurs.
- **Alertes** : liste des alertes actives et résolues, explication simple et technique, corrections proposées.
- **Historique** : graphique de chaque mesure sur 24 heures, 7 jours, 30 jours, 90 jours ou un an.
- **Journal des corrections** : qui a validé quoi et quand (ou « lancée automatiquement »), bouton « Annuler » pour les corrections annulables, vérification de l'intégrité du journal.
- **Inventaire système** : programmes au démarrage, services en panne, programmes gourmands, mises à jour en attente et en échec, événements récents, codes des écrans bleus, onduleur, protection et fins de support, points de vigilance de sécurité, programmes suspects, clés USB branchées.
- **Captures du système** : liste des points de restauration Windows (créés par vous, chaque semaine par SoluPulse, avant une correction ou par Windows), bouton « Capturer maintenant », capture hebdomadaire désactivable, et « Revenir à cet état » qui ouvre la restauration de Windows en indiquant la ligne à choisir. Si la protection du système est coupée, la page propose de l'activer. L'encadré « Si Windows ne démarre plus » explique comment revenir à une capture depuis l'outil de secours de Windows et ouvre l'assistant de création d'une clé USB de récupération.
- **Réseau** : test de débit Internet (réception, envoi, temps de réponse) avec l'historique des derniers tests ; réseaux Wi-Fi alentour par canal et canal le moins encombré ; appareils connectés au réseau local, avec ceux jamais vus mis en avant (reconnus par une empreinte, jamais par leur adresse matérielle).
- **Applications** : applications qui n'ont pas servi depuis six mois (date de dernier lancement connue de Windows), désinstallées par leur propre désinstalleur ; mises à jour des logiciels proposées par winget.
- **Tests du matériel** : écran (pixels morts), clavier, haut-parleurs gauche et droite, niveau du micro, caméra, vitesse du disque comparée à ce qu'on attend de son type.
- **Paramètres** : mode simplifié (PC d'un proche : corrections à risque et réglages avancés protégés par un code de technicien), langue (français ou anglais), mises à jour de SoluPulse, corrections automatiques sans risque, notifications et niveau minimum, durée de conservation du détail des mesures, tests de connexion Internet, seuils des règles.
- **Notifications Windows** avec boutons « Voir » et « Corriger », résumé « votre PC cette semaine » une fois par semaine, annonce d'une nouvelle version, icône dans la zone de notification colorée selon l'état du PC, diagnostic complet à la demande.
- Thème clair ou sombre selon Windows ; l'état n'est jamais donné par la couleur seule.

## 5. Sécurité et confidentialité

- Le service est la seule partie qui a des droits administrateur ; l'application n'en a aucun.
- Communication par canal nommé local protégé : aucun port réseau ouvert. Seule l'application SoluPulse installée, lancée par un utilisateur connecté au PC, est acceptée.
- Catalogue de corrections fermé : le service n'exécute que les actions prévues, jamais de commande libre.
- Journal d'audit chaîné : toute modification après coup est détectée.
- Aucune donnée ne quitte le PC ; ni numéros de série, ni adresses MAC, ni contenu des fichiers ne sont relevés. La surveillance des rançongiciels n'observe que les noms des fichiers renommés, sans les enregistrer.
- Historique conservé en détail 90 jours par défaut, puis en moyennes horaires jusqu'à deux ans.
- Une fois par jour, SoluPulse lit sur GitHub la liste des versions publiées et les listes de détection signées : rien n'est envoyé. Les listes ne sont acceptées que signées par Solutech, et une nouvelle version n'est installée seule que si son installeur est signé (voir [listes-detection.md](listes-detection.md)).
- Le test de débit, lancé par l'utilisateur, utilise les serveurs de Cloudflare.

## 6. Installation

- Installeur unique (service, application, pilote PawnIO pour les capteurs du processeur), mise à jour par-dessus une version existante.
- Logiciel libre sous licence GPL-3.0 ; signature du code prévue avec SignPath Foundation.
