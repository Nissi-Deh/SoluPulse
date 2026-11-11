# Plan de test sur machines virtuelles

Critère d'acceptation V1 (§8.2) : *chaque action du catalogue testée sur machine virtuelle, annulation comprise.* Les tests automatisés vérifient la logique et la sécurité (validation, paramètres, sauvegardes) ; l'effet réel des actions sur Windows se vérifie ici.

## Pas à pas : première machine virtuelle

Pour qui n'a jamais créé de machine virtuelle. Compter une heure, dont 30 minutes d'installation de Windows. Il faut Windows 10 ou 11 **Pro** (Hyper-V n'existe pas dans l'édition Famille) et **60 Go libres** sur un disque : pas forcément C:, la machine virtuelle peut vivre sur un autre disque.

1. **Activer Hyper-V.** PowerShell en administrateur, puis redémarrer le PC :
   ```powershell
   Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All
   ```
2. **Télécharger Windows 11** : <https://www.microsoft.com/software-download/windows11>, section « Télécharger l'image disque (ISO) ». L'enregistrer dans un dossier du disque qui a de la place, par exemple `E:\VM`.
3. **Ranger les machines virtuelles sur ce disque.** Ouvrir le **Gestionnaire Hyper-V** ; à droite, **Paramètres Hyper-V** : mettre `E:\VM` dans « Disques durs virtuels » et dans « Ordinateurs virtuels ».
4. **Créer la machine.** À droite, **Nouveau > Ordinateur virtuel** :
   - nom `SoluPulse-Test` ;
   - **Génération 2** ;
   - mémoire **4096 Mo** ;
   - réseau **Default Switch** ;
   - disque de **64 Go** ;
   - « Installer un système d'exploitation à partir d'un fichier image » : choisir l'ISO.
5. **Préparer la machine pour Windows 11.** Clic droit sur la machine, **Paramètres** :
   - **Sécurité** : cocher « Activer le module de plateforme sécurisée » ;
   - **Processeur** : 2 processeurs virtuels.
6. **Installer Windows.** Double-cliquer sur la machine, **Démarrer**, appuyer sur une touche quand « Press any key » s'affiche. Choisir « Je n'ai pas de clé de produit », puis Windows 11 Pro. Sans activation, Windows fonctionne normalement pour ces tests.
7. **Activer les points de restauration dans la machine.** Menu Démarrer, « Créer un point de restauration », disque C:, **Configurer**, « Activer la protection du système ».
8. **Faire un point de contrôle « propre ».** Dans le Gestionnaire Hyper-V, clic droit sur la machine, **Point de contrôle**, le renommer `propre`. Après chaque test qui casse quelque chose, clic droit sur ce point de contrôle, **Appliquer**, et la machine revient à cet état en quelques secondes.
9. **Copier les fichiers dans la machine.** La fenêtre de la machine s'ouvre en « session améliorée » : copier-coller directement depuis l'explorateur du PC vers le bureau de la machine :
   - `SoluPulse-Setup-<version>.exe` (artefact `installeur` de l'intégration continue) ;
   - `tests\vm\Test-SoluPulseInstall.ps1` et `tools\mesurer-performances.ps1` du dépôt.
10. **Installer SoluPulse** dans la machine, puis lancer les vérifications automatiques ci-dessous dans un PowerShell administrateur, après `Set-ExecutionPolicy -Scope Process Bypass`.
11. **Tester les actions** une par une avec le tableau [Actions du catalogue](#actions-du-catalogue), en revenant au point de contrôle `propre` entre les tests destructifs.
12. **Noter les résultats** : copier la sortie des scripts et, pour chaque action, « OK » ou ce qui ne va pas, puis les reporter dans les tickets #3, #4 et #5.

## Laboratoire

- Hyper-V avec deux VM : Windows 10 22H2 x64 et Windows 11 x64, 4 Go de RAM, 2 cœurs (ENF-COMP-02).
- Protection du système activée sur C: (points de restauration).
- Un **point de contrôle Hyper-V** « propre » avant chaque série ; on y revient après chaque test destructif.
- Installer `SoluPulse-Setup-<version>.exe` produit par l'intégration continue (artefact `installeur`).

## Vérifications automatiques

Après l'installation, en administrateur sur la VM :

```powershell
./tests/vm/Test-SoluPulseInstall.ps1          # I2, I3, I4, I5, I10, I13, absence de WinRing0, journal sans erreur
./tools/mesurer-performances.ps1 -MesurerOuverture   # ENF-PERF-01, 02 et 04 sur 1 heure
```

Le premier script arrête de force le service pour vérifier sa reprise (I3) : ne pas le lancer sur un PC de production, ou ajouter `-SkipRestartTest`.

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
| ACT-SEC-FIREWALL-ON | `Set-NetFirewallProfile -Profile Private -Enabled False` | Pare-feu actif sur les trois profils (`Get-NetFirewallProfile`) | Seul le profil privé est de nouveau coupé |
| ACT-SEC-DEFENDER-UPDATE | — | `Get-MpComputerStatus` : `AntivirusSignatureLastUpdated` à la date du jour | — |
| ACT-SEC-DEFENDER-SCAN | — | `Get-MpComputerStatus` : `QuickScanEndTime` à l'heure du test | — |
| ACT-POWER-FASTSTARTUP-OFF | Démarrage rapide actif (`HiberbootEnabled` = 1) | Valeur à 0, l'arrêt suivant relance complètement Windows (événement Kernel-Boot 27 de type 0) | Valeur 1 remise |
| ACT-DISK-MOVE-FOLDER | Second disque NTFS ; 2 Go de fichiers dans Téléchargements, dont un ouvert dans un programme | Fichiers dans `E:\<profil>\Downloads`, droits de l'utilisateur, l'Explorateur ouvre le nouvel emplacement après fermeture de session ; le fichier ouvert reste à l'ancien emplacement, sans doublon | Fichiers et emplacement d'origine remis ; refus clair si C: n'a pas la place |
| ACT-DISK-MOVE-FOLDER (OneDrive) | Documents synchronisé par OneDrive | Dossier absent des choix, refus si forcé | — |
| ACT-DISK-LARGE-FILES | Trois fichiers de 200 Mo dans Téléchargements | Seuls les fichiers cochés sont supprimés ; un identifiant inventé est refusé | Non annulable : la fiche l'indique |
| ACT-CLEAN-DEV-CACHES | Caches pip et npm remplis (`pip download`, `npm cache add`) | Caches vidés, `.nuget\packages` intact | — |
| ACT-CLEAN-UPDATE-LEFTOVERS | Dossier `%LOCALAPPDATA%\test-updater\pending` avec un fichier | Dossier vidé ; lancé seul en moins de 20 min quand le disque système est presque plein | — |
| ACT-CLEAN-COMPONENTS | VM ayant reçu des mises à jour cumulatives | DISM termine, espace libéré affiché | — |
| ACT-POWER-HIBERNATE-OFF | Veille prolongée active | `hiberfil.sys` supprimé, gain affiché | `powercfg /a` : veille prolongée de nouveau disponible |
| Corrections automatiques | Disque système sous 10 % ; paramètre activé puis désactivé | Activé : temporaires, installeurs et cache de distribution supprimés une seule fois par 24 h, « Lancée automatiquement » au journal. Désactivé : rien ne se lance | — |
| ACT-SYS-RESTORE-ON | Protection du système coupée sur C: | Page « Captures » : protection active, bouton « Capturer maintenant » visible | — (non annulable : la couper effacerait les captures) |
| Captures du système | Protection active ; un point créé il y a moins de 24 h | « Capturer maintenant » ajoute bien un point (limite de 24 h levée puis remise) ; un second clic dans les 5 min est refusé ; « Revenir à cet état » ouvre `rstrui` ; capture hebdomadaire créée 10 min après le démarrage si le dernier point a plus de 7 jours ; entrées « Capture » dans l'audit | Restaurer via `rstrui` : le logiciel installé après la capture a disparu |
| Recommandations ACT-HW-GUIDE-*, ACT-SEC-GUIDE-* et ACT-DISK-GUIDE-DOCKER | — | Aucune modification du système, entrée au journal ; le guide Docker n'apparaît que si Docker Desktop est installé | — |

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
