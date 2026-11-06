# Données collectées par SoluPulse

Liste publiée conformément à l'exigence ENF-CONF-02 (minimisation). **En V1, aucune de ces données ne quitte le PC.** Elles sont stockées dans `%ProgramData%\SoluPulse\data\solupulse.db`, lisible uniquement par le système et les administrateurs ; l'application y accède à travers le service.

## Ce que SoluPulse ne collecte jamais

- le contenu de vos fichiers, documents, photos ou courriels ;
- votre historique de navigation ;
- vos frappes clavier, captures d'écran ou mots de passe ;
- la ligne de commande des programmes (seul leur nom est relevé) ;
- les numéros de série des composants et les adresses MAC des cartes réseau.

## Mesures

| Domaine | Mesures | Fréquence |
| --- | --- | --- |
| Capteurs | Températures processeur, carte graphique, carte mère et disques ; vitesse des ventilateurs ; consommation et fréquence du processeur ; charge, mémoire vidéo et consommation des cartes graphiques | 30 s |
| Performances | Utilisation processeur, mémoire, activité disque ; plan d'alimentation ; durée depuis le démarrage | 30 s |
| Espace disque | Espace libre et taille de chaque volume fixe | 5 min |
| Réseau | Latence et perte vers la box et vers deux sondes Internet (1.1.1.1, 8.8.8.8), temps de résolution DNS, signal Wi-Fi, débit reçu et envoyé (volume total, jamais le contenu ni les destinations) | 2 min |
| Événements | Nombre d'écrans bleus et d'arrêts brutaux (7 jours), de plantages d'applications et d'erreurs disque (24 h), durée du dernier démarrage | 10 min |
| Batterie | Capacité d'origine et actuelle, cycles, charge, branchement secteur | 1 h |
| Inventaire | Nombre de programmes au démarrage, services automatiques arrêtés, périphériques en erreur, redémarrage en attente | 1 h |
| Santé des disques | État de santé Windows, usure, heures de fonctionnement, erreurs non corrigées, attributs SMART 5, 197, 198 | 6 h |
| Windows Update | Nombre et titres des mises à jour en attente | 24 h |
| Sécurité | Antivirus actif et à jour, âge des définitions et de la dernière analyse de Microsoft Defender, pare-feu par type de réseau, démarrage sécurisé, contrôle de compte d'utilisateur, chiffrement BitLocker du disque système, puce TPM | 30 min |
| Matériel | Nombre de barrettes de mémoire et d'emplacements libres, nombre d'écrans | 6 h |

Les sondes Internet n'envoient que des paquets de test (ping) et une requête DNS : aucune donnée du PC. Elles peuvent être désactivées dans **Paramètres > Données et confidentialité**.

## Inventaire

Pour expliquer les alertes, SoluPulse garde le dernier relevé de : nom et emplacement des programmes au démarrage, nom des services arrêtés, nom des périphériques en erreur, modèle et type des disques, volumes, édition et version de Windows, nom de l'ordinateur, nom des 5 programmes les plus gourmands, cartes réseau actives, titres des mises à jour en attente, date et source des derniers événements critiques.

Pour la page **Mon PC** : fabricant et modèle du PC, carte mère et version du BIOS, processeur (modèle, cœurs, fréquence, cache), barrettes de mémoire (emplacement, taille, type, fréquence, fabricant, référence), cartes graphiques (modèle, version du pilote, résolution), écrans (modèle, fabricant, année), cartes réseau (modèle, état, débit), périphériques audio, caméras, Bluetooth (adaptateur et noms des appareils associés), lecteurs biométriques, imprimantes (nom), état des protections de sécurité, et le tableau de tous les capteurs lus (nom et valeur).

## Journal des corrections

Pour chaque correction : action, paramètres choisis, compte Windows qui l'a validée, horodatages, résultat, et la sauvegarde nécessaire pour l'annuler (valeur de registre, réglage DNS, plan d'alimentation, noms de dossiers renommés).

## Durée de conservation

- Mesures détaillées : 90 jours par défaut (réglable de 7 à 365 jours), puis moyennes horaires pendant 2 ans.
- Inventaire : seul le dernier relevé est gardé.
- Alertes, corrections et journal d'audit : conservés tant que SoluPulse est installé.

À la désinstallation, le dossier `%ProgramData%\SoluPulse` est conservé pour garder l'historique en cas de réinstallation ; il peut être supprimé à la main.
