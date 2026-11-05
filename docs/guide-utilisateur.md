# Guide utilisateur SoluPulse

SoluPulse surveille votre PC en permanence, vous prévient quand quelque chose ne va pas, vous explique le problème simplement et vous propose une correction. **Rien n'est jamais modifié sur votre PC sans votre accord.**

## Installation

1. Lancez `SoluPulse-Setup-1.0.0.exe` et acceptez la demande d'autorisation de Windows. Si Windows affiche « Windows a protégé votre ordinateur » (version de test non signée), cliquez sur **Informations complémentaires** puis **Exécuter quand même**.
2. À la fin de l'installation, le service SoluPulse démarre aussitôt et fait ses premières mesures.
3. Ouvrez **SoluPulse** depuis le menu Démarrer. Aux ouvertures de session suivantes, il se lance tout seul dans la zone de notification (près de l'horloge).

L'installeur ajoute aussi le pilote **PawnIO**, qui permet de lire la température du processeur et la vitesse des ventilateurs. Il reste installé si vous désinstallez SoluPulse, car d'autres logiciels peuvent l'utiliser.

Configuration requise : Windows 10 22H2 ou Windows 11, 64 bits, 4 Go de mémoire.

## Le tableau de bord

- **Le score sur 100** résume l'état du PC. Vert à partir de 80, orange de 50 à 79, rouge en dessous.
- **Les quatre domaines** : matériel, système et logiciels, réseau, et sécurité (antivirus, pare-feu, démarrage sécurisé, chiffrement).
- **En direct** : utilisation du processeur et de la mémoire, températures, espace disque, batterie et Internet, mis à jour toutes les 15 secondes. **Voir tous les composants** ouvre la page *Mon PC*.
- **Les alertes actives** : cliquez sur **Voir** pour lire l'explication.
- **Lancer un diagnostic complet** refait toutes les mesures en moins de 2 minutes.
- **Capteurs indisponibles** : certains PC n'ont pas tous les capteurs. Ce n'est pas un problème : SoluPulse surveille tout ce qui est lisible.

L'icône dans la zone de notification change de couleur : verte si tout va bien, bleue, orange ou rouge selon l'alerte la plus grave.

## Les alertes

Chaque alerte indique :

- sa **gravité** : *information* (à savoir), *attention* (à corriger bientôt), *critique* (à traiter maintenant) ;
- une **explication simple** et la **cause probable** ;
- une **explication technique**, à déplier si vous voulez le détail ;
- les **corrections proposées**.

Les notifications Windows proposent deux boutons : **Voir** ouvre l'alerte, **Corriger** ouvre directement la fiche de correction.

## Corriger un problème

1. Cliquez sur **Corriger…** à côté de la correction choisie.
2. Lisez la fiche : ce qui va être fait, le niveau de risque, la durée, si un redémarrage est nécessaire et si l'opération peut être annulée. Pour les opérations plus sensibles, SoluPulse crée d'abord un point de restauration Windows.
3. Si la correction demande un choix (programme, carte réseau…), choisissez dans la liste.
4. Cliquez sur **Valider la correction**, ou sur **Ne rien faire**.

À la fin, SoluPulse vérifie si le problème a disparu et vous l'indique.

Certaines corrections sont des **recommandations** (nettoyer un ventilateur, sauvegarder ses fichiers, remplacer une batterie) : SoluPulse vous guide, et c'est vous qui agissez.

## Annuler une correction

Ouvrez **Journal des corrections**. Les corrections annulables ont un bouton **Annuler cette correction**, qui remet le réglage précédent. Le journal indique aussi qui a validé chaque correction ; le bouton **Vérifier l'intégrité du journal** contrôle qu'il n'a pas été modifié.

## Mon PC

Une fiche par composant détecté, avec ses caractéristiques et ses mesures en direct : ordinateur et BIOS, sécurité, processeur, mémoire (chaque barrette et les emplacements libres), cartes graphiques, disques (température, usure, état), espace disque, batterie, réseau et périphériques (écrans, son, caméra, Bluetooth, imprimantes). Une pastille de couleur accompagne chaque état, toujours doublée d'un texte. **Tous les capteurs** liste en bas de page chaque valeur lue sur le PC.

## Historique et inventaire

- **Historique** : choisissez une mesure (température, espace disque, mémoire…) et une période pour voir son évolution.
- **Inventaire système** : programmes au démarrage, services en panne, programmes gourmands, mises à jour en attente, événements récents.

## Paramètres

- **Notifications** : les activer ou non, et à partir de quelle gravité.
- **Données** : durée de conservation du détail des mesures ; tests de connexion Internet.
- **Seuils d'alerte** : remplacer un seuil recommandé par le vôtre (laissez vide pour revenir au seuil recommandé).

## Vos données

SoluPulse fonctionne sans Internet et rien ne quitte votre PC. La liste complète des données relevées est dans [donnees-collectees.md](donnees-collectees.md).

## En cas de problème

- **« Le service SoluPulse ne répond pas »** : redémarrez le PC. Si le message persiste, ouvrez *Services* dans Windows et vérifiez que le service **SoluPulse** est démarré.
- **Désinstaller** : *Paramètres Windows > Applications > SoluPulse > Désinstaller*.
