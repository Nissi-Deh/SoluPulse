# Plan de test sur machines virtuelles

Critère d'acceptation V1 (§8.2) : *chaque action du catalogue testée sur machine virtuelle, annulation comprise.* Les tests automatisés vérifient la logique et la sécurité (validation, paramètres, sauvegardes) ; l'effet réel des actions sur Windows se vérifie ici.

## Laboratoire

- Hyper-V avec deux VM : Windows 10 22H2 x64 et Windows 11 x64, 4 Go de RAM, 2 cœurs (ENF-COMP-02).
- Protection du système activée sur C: (points de restauration).
- Un **point de contrôle Hyper-V** « propre » avant chaque série ; on y revient après chaque test destructif.
- Installer `SoluPulse-Setup-<version>.exe` produit par l'intégration continue (artefact `installeur`).

## Installation (UC-01)

| # | Vérification | Attendu |
| --- | --- | --- |
| I1 | Installation sur PC neuf, chronométrée jusqu'au premier score | Moins de 5 minutes |
| I2 | `sc qc SoluPulse` | Démarrage AUTO, compte LocalSystem |
| I3 | `sc qfailure SoluPulse` puis arrêt forcé du processus `SoluPulse.Agent.exe` | Redémarrage en moins de 1 minute (ENF-FIA-01) |
| I4 | ACL de `C:\ProgramData\SoluPulse` | SYSTEM et Administrateurs uniquement |
| I5 | Ouverture de session d'un utilisateur standard | Application dans la zone de notification, sans élévation |
| I6 | Client autre que `SoluPulse.Desktop.exe` installé (copie de l'exe ailleurs) | Appel refusé (PermissionDenied) |
| I7 | Analyse Microsoft Defender du MSI et du dossier installé | Aucune détection (ENF-SEC-07) |
| I8 | Désinstallation | Service supprimé, fichiers du programme supprimés, PC sans erreur |
| I9 | Mise à jour (installer 1.0.1 par-dessus 1.0.0) | Historique conservé, service redémarré |
| I10 | PawnIO absent avant installation | PawnIO installé, température CPU et ventilateurs visibles, aucun « capteur indisponible » pour eux |
| I11 | PawnIO déjà installé (par LibreHardwareMonitor ou FanControl) | PawnIO non réinstallé ; désinstaller SoluPulse laisse PawnIO en place |
| I12 | Échec forcé de PawnIO (fichier corrompu) | SoluPulse s'installe quand même, capteurs signalés absents |
| I13 | `C:\Program Files\SoluPulse\Licences` | Licence GPL-2.0 de PawnIO et avis des composants tiers présents |

## Actions du catalogue

Pour chaque action : provoquer la condition, appliquer depuis la fiche, vérifier l'effet, puis **Annuler** si l'action est annulable, et vérifier le retour à l'état initial.

| Action | Provoquer | Vérifier l'effet | Annulation |
| --- | --- | --- | --- |
| ACT-CLEAN-TEMP | Fichiers datés de 2 jours dans `%TEMP%` et `C:\Windows\Temp` | Fichiers anciens supprimés, fichiers récents et verrouillés conservés | Non annulable : la fiche l'indique |
| ACT-CLEAN-WU-CACHE | Mises à jour téléchargées | `SoftwareDistribution\Download` vide, service wuauserv relancé | — |
| ACT-CLEAN-DO-CACHE | Cache d'optimisation de la distribution | Cache vidé, DoSvc relancé | — |
| ACT-CLEAN-RECYCLE-BIN | Fichiers dans la corbeille de deux utilisateurs | Seule la corbeille de l'utilisateur qui valide est vidée | — |
| ACT-STARTUP-DISABLE | Programme ajouté à `Run` (machine et utilisateur) | Désactivé dans le Gestionnaire des tâches, absent au démarrage suivant | Réactivé |
| ACT-POWER-BALANCED | Plan « Économie d'énergie » actif sur secteur | Plan « Utilisation normale » actif | Plan d'origine restauré |
| ACT-DRIVE-OPTIMIZE | — | `defrag C: /O` terminé sans erreur (TRIM sur disque virtuel SSD) | — |
| ACT-SYS-SFC | Fichier système corrompu (copie altérée sur VM jetable) | Point de restauration créé, SFC termine, fichier réparé | — |
| ACT-SYS-DISM | — | DISM termine avec Internet ; message clair sans Internet | — |
| ACT-SYS-CHKDSK | — | Vérification au redémarrage suivant | — |
| ACT-SYS-SERVICE-START | Service automatique arrêté (ex. `Spooler`) | Service démarré | Service arrêté |
| ACT-SYS-WU-RESET | Windows Update bloqué | Dossiers renommés `*.solupulse-<date>`, recherche de mises à jour OK | Dossiers d'origine remis en place |
| ACT-NET-FLUSH-DNS | — | `ipconfig /displaydns` vide | — |
| ACT-NET-RENEW-IP | — | Nouvelle adresse DHCP, connexion rétablie | — |
| ACT-NET-RESET-WINSOCK | LSP tiers installé | Réinitialisé après redémarrage | — |
| ACT-NET-RESET-TCPIP | IP fixe configurée | Fiche avertit que l'IP fixe sera effacée ; réseau OK après redémarrage | — |
| ACT-NET-SET-DNS | DNS automatique, puis DNS fixe | Serveurs du fournisseur choisi appliqués | Réglage d'origine (automatique ou fixe) restauré |
| ACT-NET-RESTART-ADAPTER | — | Carte désactivée puis réactivée, réseau rétabli | — |
| Recommandations ACT-HW-GUIDE-* | — | Aucune modification du système, entrée au journal | — |

## Robustesse

| # | Scénario | Attendu |
| --- | --- | --- |
| R1 | Couper la VM (arrêt brutal) pendant ACT-SYS-WU-RESET | Au redémarrage, l'exécution passe à « Annulée après interruption » et les dossiers sont restaurés (ENF-FIA-02) |
| R2 | Protection du système désactivée, action de risque moyen | Correction refusée avec un message clair, rien n'est modifié |
| R3 | Modifier une ligne de la table `audit` avec un éditeur SQLite | « Vérifier l'intégrité du journal » signale l'entrée altérée |
| R4 | VM sans batterie, sans capteurs, sans Wi-Fi | Collecte continue, capteurs absents signalés (ENF-FIA-04) |
| R5 | Débrancher le réseau | Alertes réseau cohérentes, aucune alerte matérielle parasite |
| R6 | Règle YAML invalide ajoutée dans `rules` | Règle écartée, erreur visible, autres règles actives |

## Performances (ENF-PERF-01 à 04)

Sur 3 PC de gammes différentes (dont un à 4 Go de RAM), avec le build Release installé :

| Mesure | Outil | Cible |
| --- | --- | --- |
| CPU moyen du service sur 1 h | Moniteur de performances, compteur `% Processor Time` de SoluPulse.Agent | < 1 % |
| Mémoire du service / de l'application | Octets privés | < 80 Mo / < 200 Mo |
| Diagnostic complet | Chronomètre, bouton « Lancer un diagnostic complet » | < 2 min |
| Ouverture de l'interface | Clic sur le raccourci jusqu'à l'affichage du score | < 3 s |

Mesure de développement (build Debug, sans droits administrateur) : 46 Mo d'octets privés pour le service.
