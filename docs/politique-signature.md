# Politique de signature de code

Les versions publiées de SoluPulse pour Windows sont signées gratuitement par [SignPath.io](https://signpath.io), avec un certificat fourni par [SignPath Foundation](https://signpath.org). Sur un PC, l'éditeur affiché est donc **SignPath Foundation**.

## Ce qui est signé

Seuls les fichiers construits par l'intégration continue GitHub Actions de ce dépôt, à partir du code source public, sont signés :

- l'installeur complet `SoluPulse-Setup-<version>.exe` ;
- l'installeur `SoluPulse-<version>-x64.msi`.

Aucun fichier construit sur un poste de développeur n'est signé avec ce certificat. Le pilote PawnIO, inclus dans l'installeur complet, est signé par son auteur (namazso) et n'est pas re-signé.

## Rôles

| Rôle | Personnes |
| --- | --- |
| Auteurs (commit) | [Nissi-Deh](https://github.com/Nissi-Deh) |
| Relecteurs | [Nissi-Deh](https://github.com/Nissi-Deh) |
| Approbateurs des signatures | [Nissi-Deh](https://github.com/Nissi-Deh) |

Toute contribution extérieure passe par une pull request relue avant fusion. Chaque demande de signature d'une version est approuvée manuellement. Tous les membres utilisent l'authentification à deux facteurs sur GitHub et sur SignPath.

## Confidentialité

SoluPulse ne transmet aucune information à un autre système en réseau, sauf à la demande explicite de l'utilisateur. Les seules communications sortantes sont des tests de connexion (ping vers la box et vers deux serveurs publics, une résolution DNS) et la recherche de mises à jour par l'agent Windows Update, comme Windows le fait lui-même. Les tests de connexion se désactivent dans les paramètres. Aucune donnée personnelle ni aucune mesure ne quitte le PC : voir [données collectées](donnees-collectees.md).

## Signaler un problème

Voir [SECURITY.md](../SECURITY.md).
