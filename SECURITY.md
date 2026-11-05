# Signaler une vulnérabilité

SoluPulse installe un service qui tourne avec les droits SYSTEM : une faille peut permettre de prendre le contrôle d'un PC. Merci de nous la signaler en privé avant toute publication.

## Comment signaler

- Utilisez le bouton **« Report a vulnerability »** de l'onglet *Security* du dépôt GitHub (signalement privé).
- À défaut, écrivez à l'équipe Solutech en indiquant « Sécurité SoluPulse » dans l'objet.

Merci d'indiquer :

1. la version de SoluPulse et de Windows ;
2. le composant concerné (service, application, canal nommé, installeur, règles) ;
3. les étapes pour reproduire le problème et son impact (élévation de privilèges, contournement de la validation, fuite de données…).

## Nos engagements

| Étape | Délai visé |
| --- | --- |
| Accusé de réception | 3 jours ouvrés |
| Première évaluation | 10 jours ouvrés |
| Correctif pour une faille critique | 30 jours |

Nous vous tenons informé de l'avancement et nous vous citons dans les notes de version si vous le souhaitez. Nous demandons de ne pas publier les détails avant la sortie du correctif.

## Périmètre

Sont notamment concernés : tout moyen d'exécuter une action sans la validation de l'utilisateur présent, d'exécuter autre chose qu'une action du catalogue, d'accéder au canal nommé depuis un autre compte ou depuis le réseau, de modifier le journal d'audit sans détection, ou d'obtenir des données personnelles que SoluPulse ne devrait pas collecter.
