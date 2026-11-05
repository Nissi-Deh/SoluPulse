# Signature de code sans budget

Le cahier des charges exige des exécutables et un installeur signés (ENF-SEC-04). Un certificat reconnu par Windows coûte de l'argent (environ 100 à 300 $ par an). Voici comment avancer sans dépenser, et ce qu'il faudra le jour de la diffusion publique.

## Ce qui se passe sans signature

SoluPulse fonctionne normalement. À l'ouverture de l'installeur, Windows SmartScreen affiche « Windows a protégé votre ordinateur » et « Éditeur : inconnu ». L'utilisateur clique sur **Informations complémentaires**, puis **Exécuter quand même**. Pour une diffusion au grand public, c'est un frein réel ; pour des tests et une bêta encadrée, c'est acceptable.

## Gratuit, tout de suite : certificat de test auto-signé

Pour les machines virtuelles et les 10 PC de la bêta :

```powershell
./build/new-test-certificate.ps1                     # crée le certificat et affiche son empreinte
./build/build-installer.ps1 -Version 1.0.0 -CertificateThumbprint <empreinte>
```

Puis, sur chaque PC de test, en administrateur, installer la partie publique (`artifacts\certificat\SoluPulse-Test.cer`) :

```powershell
Import-Certificate -FilePath SoluPulse-Test.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath SoluPulse-Test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Sur ces PC, SoluPulse apparaît signé par « Solutech SoluPulse (test) ». La chaîne de signature est testée de bout en bout. Sur les autres PC, le certificat n'est pas reconnu : il ne sert pas pour une diffusion publique.

## Piste écartée : SignPath Foundation

[SignPath Foundation](https://signpath.org/) signe gratuitement les projets open source. SoluPulse étant **propriétaire** (décision du 5 novembre 2026), cette piste ne s'applique pas.

## Gratuit, à faire dans tous les cas

- **Soumettre l'installeur à Microsoft** pour analyse avant chaque diffusion : <https://www.microsoft.com/wdsi/filesubmission>. Cela évite les fausses détections de Defender (critère d'acceptation V1).
- **Diffuser toujours depuis la même adresse** (page des versions GitHub par exemple) : la réputation SmartScreen se construit avec le nombre de téléchargements sans incident.

## Le jour de la diffusion publique

SoluPulse étant propriétaire, un certificat payant est le seul moyen d'obtenir une signature reconnue par tous les PC. Le moins cher est un certificat OV, par exemple Certum Standard (cloud), autour de 100 à 120 $ par an. Il est émis au nom de Solutech et devrait être pris en charge par l'entreprise, comme le reste du budget de diffusion. Une fois acheté :

1. ajouter les secrets `SIGNING_CERT_PFX_BASE64` et `SIGNING_CERT_PASSWORD` au dépôt (ou adapter le script au service de signature en ligne du fournisseur) ;
2. l'intégration continue signe automatiquement les exécutables, le MSI et l'installeur complet ;
3. pour l'installeur complet, signer aussi le moteur Burn (`wix burn detach` puis `reattach`) avant la signature finale.
