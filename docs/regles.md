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
| `cpu.` | `temperature_c`, `utilisation_pct`, `puissance_w`, `frequence_mhz` |
| `gpu.<nom>.` | `temperature_c`, `utilisation_pct`, `memoire_utilisee_pct`, `memoire_totale_mo`, `puissance_w` |
| `carte_mere.<capteur>.` | `temperature_c` |
| `ventilateur.<nom>.` | `tours_min` |
| `memoire.` | `utilisation_pct`, `disponible_mo`, `totale_mo`, `barrettes`, `emplacements_libres` |
| `ecrans.` | `connectes` |
| `disque.systeme.` / `volume.<lettre>.` | `espace_libre_pct`, `espace_libre_go`, `taille_go` |
| `disque_physique.<modèle>.` | `etat_sante`, `usure_pct`, `temperature_c`, `heures_fonctionnement`, `secteurs_realloues`, `secteurs_en_attente`, `secteurs_irrecuperables`, `prediction_panne`, `erreurs_lecture_non_corrigees` |
| `disque_activite.` | `utilisation_pct` |
| `batterie.` | `sante_pct`, `cycles`, `charge_pct`, `capacite_origine_mwh`, `capacite_actuelle_mwh` |
| `alimentation.` | `secteur` |
| `evenements.` | `ecrans_bleus_7j`, `arrets_brutaux_7j`, `plantages_applications_24h`, `erreurs_disque_24h` |
| `systeme.` | `programmes_demarrage`, `services_auto_arretes`, `pilotes_en_erreur`, `mises_a_jour_en_attente`, `redemarrage_en_attente`, `jours_depuis_demarrage`, `duree_demarrage_s`, `plan_economie_energie` |
| `reseau.` | `connecte`, `passerelle.perte_pct`, `passerelle.latence_ms`, `internet.perte_pct`, `internet.latence_ms`, `dns.echec`, `dns.resolution_ms`, `wifi.signal_pct`, `debit_recu_kbps`, `debit_envoye_kbps` |
| `securite.` | `antivirus_actif`, `antivirus_a_jour`, `defender.protection_temps_reel`, `defender.signatures_age_j`, `defender.dernier_scan_j`, `pare_feu.profils_desactives`, `demarrage_securise`, `uac_actif`, `chiffrement_disque_systeme`, `tpm_pret` |

La page **Historique** de l'application liste les métriques réellement mesurées sur un PC.

## Corrections disponibles

`ACT-CLEAN-TEMP`, `ACT-CLEAN-WU-CACHE`, `ACT-CLEAN-DO-CACHE`, `ACT-CLEAN-RECYCLE-BIN`, `ACT-STARTUP-DISABLE`, `ACT-POWER-BALANCED`, `ACT-DRIVE-OPTIMIZE`, `ACT-SYS-SFC`, `ACT-SYS-DISM`, `ACT-SYS-CHKDSK`, `ACT-SYS-SERVICE-START`, `ACT-SYS-WU-RESET`, `ACT-NET-FLUSH-DNS`, `ACT-NET-RENEW-IP`, `ACT-NET-RESET-WINSOCK`, `ACT-NET-RESET-TCPIP`, `ACT-NET-SET-DNS`, `ACT-NET-RESTART-ADAPTER`, `ACT-SEC-FIREWALL-ON`, `ACT-SEC-DEFENDER-UPDATE`, `ACT-SEC-DEFENDER-SCAN`, et les recommandations `ACT-HW-GUIDE-COOLING`, `ACT-HW-GUIDE-DISK`, `ACT-HW-GUIDE-BATTERY`, `ACT-HW-GUIDE-BACKUP`, `ACT-SEC-GUIDE-ANTIVIRUS`, `ACT-SEC-GUIDE-UAC`, `ACT-SEC-GUIDE-SECUREBOOT`, `ACT-SEC-GUIDE-ENCRYPTION`.

Le test `ShippedRuleFilesAreValid` vérifie à chaque build que toutes les règles livrées se chargent.
