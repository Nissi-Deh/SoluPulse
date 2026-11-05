<#
.SYNOPSIS
    Construit l'installeur de SoluPulse : le MSI, puis l'installeur complet (.exe) qui ajoute PawnIO.

.DESCRIPTION
    1. Publie le service et l'application en mode autonome (runtime .NET embarqué, ENF-COMP-03).
    2. Signe les exécutables et bibliothèques SoluPulse si un certificat est fourni (ENF-SEC-04).
    3. Construit le MSI avec WiX.
    4. Récupère l'installeur officiel de PawnIO, vérifie sa signature Authenticode, et construit
       l'installeur complet SoluPulse-Setup-<version>.exe (PawnIO + MSI).

    PawnIO est cherché dans cet ordre : -PawnIOSetup, installer\redist\PawnIO_setup.exe, puis
    téléchargement depuis -PawnIOUrl (GitHub). Sans PawnIO, seul le MSI est produit.

    La signature utilise un certificat du magasin de l'utilisateur, désigné par son empreinte :
    -CertificateThumbprint ou variable d'environnement SOLUPULSE_SIGN_THUMBPRINT. Pour les tests et
    la bêta, build/new-test-certificate.ps1 crée un certificat auto-signé gratuit.

.EXAMPLE
    ./build/build-installer.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$CertificateThumbprint = $env:SOLUPULSE_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$PawnIOSetup,
    [string]$PawnIOUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe",
    # Nom attendu dans le certificat de PawnIO (vide : signature valide exigée, signataire affiché)
    [string]$PawnIOSigner = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publishAgent = Join-Path $artifacts "publish\Agent"
$publishApp = Join-Path $artifacts "publish\App"
$installerOut = Join-Path $artifacts "installer"
$redist = Join-Path $root "installer\redist"

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File a échoué (code $LASTEXITCODE)." }
}

function Get-SignTool {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $tool = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) { throw "signtool.exe introuvable : installez le SDK Windows." }
    return $tool.FullName
}

function Invoke-Sign([string[]]$Files) {
    if (-not $CertificateThumbprint) { return }
    $signtool = Get-SignTool
    Invoke-Checked $signtool (@("sign", "/sha1", $CertificateThumbprint, "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256") + $Files)
}

# Installeur PawnIO : fichier fourni, copie locale, ou téléchargement. Sa signature Authenticode doit
# être valide et émise pour son auteur ; sinon il n'est pas embarqué.
function Resolve-PawnIO {
    $candidate = if ($PawnIOSetup) { $PawnIOSetup } else { Join-Path $redist "PawnIO_setup.exe" }
    if (-not (Test-Path $candidate)) {
        Write-Host "Téléchargement de PawnIO depuis $PawnIOUrl…"
        New-Item -ItemType Directory -Force $redist | Out-Null
        $candidate = Join-Path $redist "PawnIO_setup.exe"
        for ($i = 1; $i -le 3 -and -not (Test-Path $candidate); $i++) {
            try {
                Invoke-WebRequest -Uri $PawnIOUrl -OutFile $candidate -UseBasicParsing -TimeoutSec 120
            } catch {
                Remove-Item $candidate -ErrorAction SilentlyContinue
                Write-Warning "Tentative $i : $($_.Exception.Message)"
                Start-Sleep -Seconds 5
            }
        }
    }

    if (-not (Test-Path $candidate)) {
        Write-Warning "PawnIO indisponible (réseau bloqué ?). Déposez PawnIO_setup.exe dans installer\redist ou passez -PawnIOSetup."
        return $null
    }

    $signature = Get-AuthenticodeSignature $candidate
    if ($signature.Status -ne "Valid" -or ($PawnIOSigner -and $signature.SignerCertificate.Subject -notmatch [regex]::Escape($PawnIOSigner))) {
        throw "Signature de PawnIO invalide ou inattendue : $($signature.Status), $($signature.SignerCertificate.Subject)"
    }

    $hash = (Get-FileHash $candidate -Algorithm SHA256).Hash
    Write-Host "PawnIO vérifié : signé par $($signature.SignerCertificate.Subject), SHA-256 $hash"
    return (Resolve-Path $candidate).Path
}

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue

$publishArgs = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
    "-p:PublishReadyToRun=true", "-p:Version=$Version", "-p:DebugType=none")

Write-Host "Publication du service…"
Invoke-Checked dotnet (@("publish", (Join-Path $root "src\SoluPulse.Agent\SoluPulse.Agent.csproj"), "-o", $publishAgent) + $publishArgs)

Write-Host "Publication de l'application…"
Invoke-Checked dotnet (@("publish", (Join-Path $root "src\SoluPulse.Desktop\SoluPulse.Desktop.csproj"), "-o", $publishApp) + $publishArgs)

if ($CertificateThumbprint) {
    Write-Host "Signature des binaires SoluPulse…"
    $ours = Get-ChildItem $publishAgent, $publishApp -Recurse -Include "SoluPulse.*.exe", "SoluPulse.*.dll" | ForEach-Object FullName
    Invoke-Sign $ours
} else {
    Write-Warning "Aucun certificat : l'installeur ne sera pas signé. Windows SmartScreen affichera « Éditeur inconnu »."
}

Write-Host "Construction du MSI…"
Invoke-Checked dotnet @("build", (Join-Path $root "installer\SoluPulse.Installer.wixproj"), "-c", $Configuration,
    "-p:ProductVersion=$Version", "-p:AgentDir=$publishAgent", "-p:AppDir=$publishApp", "-o", $installerOut)
$msi = Get-ChildItem $installerOut -Filter "*.msi" | Select-Object -First 1
Invoke-Sign @($msi.FullName)
Write-Host "MSI : $($msi.FullName) ($([math]::Round($msi.Length / 1MB, 1)) Mo)"

$pawnio = Resolve-PawnIO
if ($pawnio) {
    Write-Host "Construction de l'installeur complet (PawnIO + SoluPulse)…"
    Invoke-Checked dotnet @("build", (Join-Path $root "installer\bundle\SoluPulse.Bundle.wixproj"), "-c", $Configuration,
        "-p:ProductVersion=$Version", "-p:MsiPath=$($msi.FullName)", "-p:PawnIOSetup=$pawnio", "-o", $installerOut)
    $setup = Get-ChildItem $installerOut -Filter "SoluPulse-Setup-*.exe" | Select-Object -First 1
    # Avec un certificat acheté, signer aussi le moteur Burn (wix burn detach / reattach) avant ce dernier passage.
    Invoke-Sign @($setup.FullName)
    Write-Host "Installeur complet : $($setup.FullName) ($([math]::Round($setup.Length / 1MB, 1)) Mo)"
}
