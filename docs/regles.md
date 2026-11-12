# Format des règles

Les règles sont des fichiers YAML dans le dossier `rules` du programme (EF-DIA-01). Elles sont chargées au démarrage du service, sans recompilation. Un fichier peut contenir une règle seule ou une liste sous la clé `regles`.

Les conditions sont **structurées** : métrique, opérateur, seuil. Un fichier de règles ne peut jamais exécuter de code. Le chargeur est strict : une clé inconnue, une métrique vide, un opérateur ou une durée invalides, un identifiant en double ou une correction absente du catalogue écartent la règle. L'erreur est inscrite au journal technique et affichée dans l'état du service.

## Exemple

```yaml
regles:
  - id: HW-CPU-002                 # majuscules, chiffres, tirets
    version: 1                     # facultatif
    domaine: materiel              # materiel, systeme, securite, reseau
    titre: Processeur chaud et ventilateur lent
    gravite: critique              # information, attention, critique
    conditions:
      tous:                        # ou au_moins_un
        - { metrique: cpu.temperature_c, operateur: ">", seuil: 80, duree_min: 2m, hysteresis: 5 }
        - { metrique: ventilateur.*.tours_min, operateur: "<", seuil: 300 }
    cause_probable: Ventilateur bloqué, débranché ou en panne
    explication_simple: >
      Le processeur chauffe alors que le ventilateur tourne à peine.
    explication_technique: Température CPU > 80 °C avec un ventilateur sous 300 tr/min.
    corrections: [ACT-HW-GUIDE-COOLING]
```

Une règle à une seule condition utilise `condition:` au lieu de `conditions:`.

## Champs d'une condition

| Champ | Rôle |
| --- | --- |
| `metrique` | Nom de la métrique. `*` remplace un segment : `disque_physique.*.usure_pct` crée une alerte par disque. |
| `operateur` | `<`, `<=`, `>`, `>=`, `==`, `!=` |
| `seuil` | Valeur numérique. L'utilisateur peut la remplacer dans les paramètres (première condition seulement). |
| `duree_min` | Durée pendant laquelle la condition doit rester vraie avant l'alerte : `30s`, `10m`, `2h`, `7j`. La plus longue durée de la règle s'applique. |
| `hysteresis` | Marge de retour à la normale : avec `< 10` et `hysteresis: 2`, l'alerte se ferme quand la valeur atteint 12. |

## Comportement du moteur (EF-DIA-06)

- Une alerte n'est jamais ouverte deux fois pour la même règle et le même sujet.
- Une métrique absente (capteur illisible, disque débranché) ne déclenche ni ne ferme une alerte.
- Après une correction, la règle est réévaluée immédiatement (EF-COR-06).

## Métriques disponibles

| Préfixe | Exemples |
| --- | --- |
| `cpu.` | `temperature_c`, `utilisation_pct`, `puissance_w`, `frequence_mhz`, `temperature_repos_c`, `temperature_repos_hausse_c`, `microcode_instable` |
| `gpu.<nom>.` | `temperature_c`, `utilisation_pct`, `memoire_utilisee_pct`, `memoire_totale_mo`, `puissance_w` |
| `carte_mere.<capteur>.` | `temperature_c` |
| `ventilateur.<nom>.` | `tours_min` |
| `memoire.` | `utilisation_pct`, `disponible_mo`, `totale_mo`, `barrettes`, `emplacements_libres`, `fuite_processus_go_h` |
| `ecrans.` | `connectes` |
| `disque.systeme.` / `volume.<lettre>.` | `espace_libre_pct`, `espace_libre_go`, `taille_go` ; pour le disque système : `jours_avant_plein`, `disque_dur_classique`, `verification_en_attente` |
| `disque_physique.<modèle>.` | `etat_sante`, `usure_pct`, `temperature_c`, `heures_fonctionnement`, `secteurs_realloues`, `secteurs_en_attente`, `secteurs_irrecuperables`, `prediction_panne`, `erreurs_lecture_non_corrigees`, `erreurs_non_corrigees` (SMART 187), `erreurs_cable` (SMART 199), `reserve_pct`, `avertissement_critique`, `erreurs_media`, `arrets_non_securises` (NVMe), `firmware_a_risque`, et pour chaque compteur `<compteur>_hausse_30j` |
| `disque_activite.` | `utilisation_pct` |
| `batterie.` | `sante_pct`, `cycles`, `charge_pct`, `capacite_origine_mwh`, `capacite_actuelle_mwh`, `temps_a_100_pct` |
| `onduleur.` | `present`, `charge_pct`, `autonomie_min`, `sante_pct` (PC fixes) |
| `alimentation.` | `secteur` ; `<rail>.tension_v`, `<rail>.ecart_pct` pour `rail_12v`, `rail_5v`, `rail_3v3` |
| `evenements.` | `ecrans_bleus_7j`, `arrets_brutaux_7j`, `plantages_applications_24h`, `erreurs_disque_24h`, `resets_disque_24h`, `verifications_disque_demarrage_30j`, `processeur_bride_minutes_7j`, `coupures_courant_30j`, `erreurs_materiel_30j`, `pilote_graphique_7j`, `memoire_virtuelle_faible_7j`, `changements_alimentation_24h`, `corrections_horloge_30j`, `deconnexions_wifi_24h` |
| `ecrans_bleus.` | `memoire_30j`, `materiel_30j`, `graphique_30j`, `stockage_30j`, `meme_code_30j` |
| `systeme.` | `programmes_demarrage`, `services_auto_arretes`, `pilotes_en_erreur`, `mises_a_jour_en_attente`, `redemarrage_en_attente`, `jours_depuis_demarrage`, `duree_demarrage_s`, `plan_economie_energie`, `jours_depuis_derniere_maj`, `mises_a_jour_echouees_30j`, `jours_avant_fin_support`, `magasin_composants_etat`, `protection_systeme`, `points_restauration`, `points_restauration_effaces`, `environnement_recuperation_actif`, `horloge_dans_le_passe`, `jours_depuis_installation_windows` |
| `donnees.` | `sauvegarde_detectee`, `historique_fichiers_jours` |
| `materiel.` | `bios_age_jours` |
| `navigateur.` | `sites_notifications`, `extensions_suspectes` |
| `reseau.` | `connecte`, `passerelle.perte_pct`, `passerelle.latence_ms`, `internet.perte_pct`, `internet.latence_ms`, `dns.echec`, `dns.resolution_ms`, `wifi.signal_pct`, `debit_recu_kbps`, `debit_envoye_kbps`, `hosts_entrees_suspectes`, `proxy_configure`, `dns_inconnus`, `wifi_ouverts_enregistres` |
| `securite.` | `antivirus_actif`, `antivirus_a_jour`, `defender.protection_temps_reel`, `defender.signatures_age_j`, `defender.dernier_scan_j`, `pare_feu.profils_desactives`, `demarrage_securise`, `uac_actif`, `chiffrement_disque_systeme`, `tpm_pret`, `bitlocker.cle_recuperation`, `secure_boot_certificats_2023`, `defender.exclusions_dangereuses`, `bureau_a_distance_actif`, `echecs_connexion_24h`, `demarrage_suspects`, `taches_suspectes`, `logiciels_vulnerables`, `processeur_occupe_inactif_pct`, `usb.fichiers_suspects`, `rancongiciel_suspect` |

La page **Historique** de l'application liste les métriques réellement mesurées sur un PC.

## Corrections disponibles

`ACT-CLEAN-TEMP`, `ACT-CLEAN-WU-CACHE`, `ACT-CLEAN-DO-CACHE`, `ACT-CLEAN-RECYCLE-BIN`, `ACT-STARTUP-DISABLE`, `ACT-POWER-BALANCED`, `ACT-DRIVE-OPTIMIZE`, `ACT-POWER-FASTSTARTUP-OFF`, `ACT-DISK-MOVE-FOLDER`, `ACT-DISK-LARGE-FILES`, `ACT-CLEAN-DEV-CACHES`, `ACT-CLEAN-UPDATE-LEFTOVERS`, `ACT-CLEAN-COMPONENTS`, `ACT-POWER-HIBERNATE-OFF`, `ACT-SYS-SFC`, `ACT-SYS-DISM`, `ACT-SYS-CHKDSK`, `ACT-SYS-SERVICE-START`, `ACT-SYS-WU-RESET`, `ACT-SYS-RESTORE-ON`, `ACT-NET-FLUSH-DNS`, `ACT-NET-RENEW-IP`, `ACT-NET-RESET-WINSOCK`, `ACT-NET-RESET-TCPIP`, `ACT-NET-SET-DNS`, `ACT-NET-RESTART-ADAPTER`, `ACT-SEC-FIREWALL-ON`, `ACT-SEC-DEFENDER-UPDATE`, `ACT-SEC-DEFENDER-SCAN`, `ACT-SYS-WINRE-ON`, `ACT-SEC-RDP-OFF`, `ACT-SEC-USB-CLEAN`, et les recommandations `ACT-DISK-GUIDE-DOCKER`, `ACT-SEC-GUIDE-ANTIVIRUS`, `ACT-SEC-GUIDE-UAC`, `ACT-SEC-GUIDE-SECUREBOOT`, `ACT-SEC-GUIDE-ENCRYPTION`, `ACT-HW-GUIDE-COOLING`, `ACT-HW-GUIDE-DISK`, `ACT-HW-GUIDE-BATTERY`, `ACT-HW-GUIDE-BACKUP`, `ACT-HW-GUIDE-UPS`, `ACT-HW-GUIDE-UPS-BATTERY`, `ACT-HW-GUIDE-POWER-SUPPLY`, `ACT-HW-GUIDE-CHARGER`, `ACT-HW-GUIDE-CMOS`, `ACT-HW-GUIDE-CHARGE-LIMIT`, `ACT-HW-GUIDE-MEMTEST`, `ACT-HW-GUIDE-SSD`, `ACT-HW-GUIDE-SATA-CABLE`, `ACT-HW-GUIDE-FIRMWARE`, `ACT-HW-GUIDE-BIOS`, `ACT-HW-GUIDE-GPU-DRIVER`, `ACT-HW-GUIDE-WIFI-DRIVER`, `ACT-SEC-GUIDE-EXCLUSIONS`, `ACT-SEC-GUIDE-RANSOMWARE`, `ACT-SEC-GUIDE-SUSPICIOUS`, `ACT-SEC-GUIDE-UPDATE-SOFTWARE`, `ACT-SEC-GUIDE-BROWSER`, `ACT-SEC-GUIDE-BITLOCKER-KEY`, `ACT-SEC-GUIDE-SECUREBOOT-2023`, `ACT-SEC-GUIDE-HIJACK`, `ACT-SEC-GUIDE-OPEN-WIFI`, `ACT-SYS-GUIDE-END-OF-SUPPORT`, `ACT-SYS-GUIDE-RECOVERY-DRIVE`, `ACT-SYS-GUIDE-MEMORY-LEAK`.

Le test `ShippedRuleFilesAreValid` vérifie à chaque build que toutes les règles livrées se chargent.
