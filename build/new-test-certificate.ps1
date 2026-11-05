<#
.SYNOPSIS
    Crée un certificat de signature de code auto-signé, gratuit, pour les tests sur VM et la bêta.

.DESCRIPTION
    Un certificat auto-signé n'est reconnu que par les PC sur lesquels on l'a explicitement installé.
    Il permet de tester toute la chaîne de signature (ENF-SEC-04) et d'éviter l'avertissement
    « Éditeur inconnu » sur les machines de test et les PC de la bêta, sans rien acheter.
    Il ne remplace pas un certificat d'une autorité reconnue pour une diffusion publique.

    Le script :
    1. crée le certificat dans le magasin personnel de l'utilisateur (clé non exportable par défaut) ;
    2. exporte la partie publique dans artifacts\certificat\SoluPulse-Test.cer ;
    3. affiche l'empreinte à passer à build-installer.ps1.

    Sur chaque PC de test, en administrateur :
        Import-Certificate -FilePath SoluPulse-Test.cer -CertStoreLocation Cert:\LocalMachine\Root
        Import-Certificate -FilePath SoluPulse-Test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
#>
[CmdletBinding()]
param(
    [string]$Subject = "CN=Solutech SoluPulse (test), O=Solutech",
    [int]$Years = 2
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\certificat"
New-Item -ItemType Directory -Force $out | Out-Null

$certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
    -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears($Years)

$cer = Join-Path $out "SoluPulse-Test.cer"
Export-Certificate -Cert $certificate -FilePath $cer | Out-Null

Write-Host "Certificat de test créé : $($certificate.Subject)"
Write-Host "Empreinte : $($certificate.Thumbprint)"
Write-Host "Partie publique à installer sur les PC de test : $cer"
Write-Host ""
Write-Host "Construire l'installeur signé :"
Write-Host "  ./build/build-installer.ps1 -Version 1.0.0 -CertificateThumbprint $($certificate.Thumbprint)"
