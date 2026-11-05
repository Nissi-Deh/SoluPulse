# Signature de code

Le cahier des charges exige des exécutables et un installeur signés (ENF-SEC-04). SoluPulse étant open source (GPL-3.0), la signature est **gratuite** grâce à SignPath Foundation.

## Ce qui se passe sans signature

SoluPulse fonctionne normalement, mais Windows SmartScreen affiche « Windows a protégé votre ordinateur » et « Éditeur : inconnu ». L'utilisateur doit cliquer sur **Informations complémentaires**, puis **Exécuter quand même**.

## Signature gratuite par SignPath Foundation

[SignPath Foundation](https://signpath.org) signe gratuitement les projets open source, avec un certificat reconnu par Windows. L'éditeur affiché est « SignPath Foundation ».

### Candidature (une seule fois)

Conditions principales : licence approuvée par l'OSI (GPL-3.0 ✓), dépôt public (✓), construction par l'intégration continue (✓, GitHub Actions), politique de signature publiée (✓, [politique-signature.md](politique-signature.md)), authentification à deux facteurs sur GitHub et SignPath.

1. Activer l'authentification à deux facteurs sur le compte GitHub.
2. Remplir le formulaire de candidature sur <https://signpath.org/apply> avec l'adresse du dépôt.
3. Une fois accepté, SignPath crée l'organisation et le projet. Dans SignPath :
   - relier le projet au dépôt GitHub (« trusted build system » GitHub.com) ;
   - créer une politique de signature `release-signing` et y importer [`.signpath/artifact-configuration.xml`](../.signpath/artifact-configuration.xml) ;
   - créer un jeton d'API pour l'intégration continue.
4. Dans GitHub, **Settings > Secrets and variables > Actions** :
   - secret `SIGNPATH_API_TOKEN` : le jeton d'API ;
   - variable `SIGNPATH_ORGANIZATION_ID` : l'identifiant de l'organisation SignPath.

### Fonctionnement

Dès que ces deux valeurs existent, l'intégration continue envoie l'installeur à SignPath à chaque construction de `main`. La signature attend l'approbation manuelle dans SignPath, puis les fichiers signés sont publiés comme artefact `installeur-signe`. Sans ces valeurs, l'installeur reste produit non signé, comme aujourd'hui.

Limite actuelle : l'installeur complet embarque le MSI tel qu'il était avant signature. La signature de `SoluPulse-Setup.exe` couvre le fichier que l'utilisateur télécharge, c'est celle que vérifient SmartScreen et la demande d'autorisation de Windows. Le MSI diffusé seul est signé en profondeur (exécutables et bibliothèques SoluPulse compris). Amélioration possible : signer en deux passes (binaires et MSI, puis installeur complet et son moteur Burn).

## En attendant : certificat de test gratuit

Pour les machines virtuelles et la bêta, avant l'acceptation par SignPath :

```powershell
./build/new-test-certificate.ps1                     # crée le certificat et affiche son empreinte
./build/build-installer.ps1 -Version 1.0.0 -CertificateThumbprint <empreinte>
```

Puis, sur chaque PC de test, en administrateur, installer la partie publique (`artifacts\certificat\SoluPulse-Test.cer`) :

```powershell
Import-Certificate -FilePath SoluPulse-Test.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath SoluPulse-Test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Sur ces PC seulement, SoluPulse apparaît signé par « Solutech SoluPulse (test) ».

## Gratuit, à faire dans tous les cas

- **Soumettre l'installeur à Microsoft** avant chaque diffusion : <https://www.microsoft.com/wdsi/filesubmission>. Cela évite les fausses détections de Defender.
- **Diffuser toujours depuis la même adresse** (page des versions GitHub) : la réputation SmartScreen se construit avec les téléchargements sans incident.
