# Listes de détection signées

SoluPulse contient trois listes qui doivent rester à jour sans attendre une nouvelle version :

- les **firmwares de disque dangereux** (règle HW-FW-001) ;
- les **logiciels à faille connue** et la version qui corrige (règle SEC-VULN-001) ;
- les **dates de fin de support de Windows 11** (règles SYS-EOS-001 et SYS-EOS-002).

La liste intégrée au programme sert par défaut. Une fois par jour, le service télécharge `data/listes.json` et `data/listes.json.sig` depuis la branche `main` du dépôt. Il ne met une liste en service que si elle remplit trois conditions :

- elle est **signée** par la clé de Solutech (ECDSA P-256, SHA-256) ;
- son numéro de `version` est **plus grand** que celui de la liste en service ;
- son contenu respecte les **limites** : 512 Ko au plus, des textes courts, des versions valides.

Une liste ne contient que des données, jamais de code. Une liste modifiée sans la clé privée est refusée.

## Clés

- **Clé publique** : elle est intégrée au code, dans `src/SoluPulse.Platform.Windows/ThreatLists.cs`.
- **Clé privée** : elle est dans `%USERPROFILE%\.solupulse\cle-signature-listes.pem` sur le PC de Solutech. **Elle ne doit jamais entrer dans le dépôt.**
  - Gardez-en une copie hors de ce PC, par exemple sur une clé USB rangée en lieu sûr ou dans un gestionnaire de mots de passe.
  - Si elle est perdue, il faut générer une nouvelle paire de clés et publier une nouvelle version de SoluPulse avec la nouvelle clé publique. Les PC restés sur l'ancienne version ne recevront plus de listes.

## Publier une nouvelle liste

1. Modifiez `data/listes.json` : ajoutez ou corrigez les entrées, **augmentez `version` de 1** et mettez `date` à jour.
2. Signez la liste :
   ```powershell
   dotnet run --project tools/SignerListes -- signer data\listes.json "$env:USERPROFILE\.solupulse\cle-signature-listes.pem"
   dotnet run --project tools/SignerListes -- verifier data\listes.json
   ```
3. Faites le commit des deux fichiers, puis fusionnez sur `main`. Les PC appliquent la nouvelle liste sous 24 heures.

Le fichier `.gitattributes` traite ces deux fichiers comme binaires : Git ne change pas leurs fins de ligne, sinon la signature ne correspondrait plus. Le test `ThePublishedListIsSignedWithTheEmbeddedKey` échoue si la liste publiée n'est pas signée avec la clé intégrée.

## Mises à jour de SoluPulse

Le même service consulte chaque jour les versions publiées sur GitHub, préversions comprises. Quand une version plus récente existe :

- l'application l'annonce une fois en notification, et la page Paramètres propose de l'installer ou d'ouvrir sa page ;
- **l'installation automatique** n'a lieu que si l'installeur MSI porte une signature valide de **SignPath Foundation**. Sinon, il est supprimé et l'utilisateur est renvoyé vers la page de la version. Un installeur non signé téléchargé d'Internet ne sera jamais lancé avec les droits du système ;
- chaque tentative d'installation est inscrite au journal d'audit.
