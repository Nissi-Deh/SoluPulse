<#
.SYNOPSIS
    Publie la V1 sur GitHub : envoi de la branche feat/v1, pull request, jalon V1 et tickets.

.DESCRIPTION
    À lancer depuis un réseau qui donne accès à GitHub (le réseau de l'entreprise le bloque).
    Nécessite git et GitHub CLI (gh) connecté au compte propriétaire du dépôt : `gh auth status`.
    Le script peut être relancé sans risque : il ne recrée pas ce qui existe déjà.

.EXAMPLE
    ./tools/github/publier-v1.ps1
#>
[CmdletBinding()]
param([string]$Repo = "Nissi-Deh/SoluPulse")

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root

# git et gh écrivent leur progression sur la sortie d'erreur : seul le code de retour compte.
function Invoke-Native([string]$File, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $File @Arguments 2>&1 | ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') : $output" }
    return $output
}

Write-Host "Envoi de la branche feat/v1…"
Invoke-Native git @("push", "-u", "origin", "feat/v1") | Out-Null

$pr = & gh pr list --repo $Repo --head feat/v1 --state all --json url --jq ".[0].url" 2>$null
if (-not $pr) {
    $pr = Invoke-Native gh @("pr", "create", "--repo", $Repo, "--base", "main", "--head", "feat/v1",
        "--title", "V1 de SoluPulse : agent, diagnostic, corrections validées, installeur",
        "--body-file", (Join-Path $PSScriptRoot "pr-v1.md"))
}
Write-Host "Pull request : $pr"

$labels = @(
    @("v1", "0E7490", "Livrable de la V1"),
    @("hors-code", "5F6368", "Tâche hors développement (achat, test, décision)"),
    @("securite", "B3261E", "Sécurité"),
    @("tests", "1E7A3C", "Tests et validation")
)
foreach ($label in $labels) {
    Invoke-Native gh @("label", "create", $label[0], "--repo", $Repo, "--color", $label[1], "--description", $label[2], "--force") | Out-Null
}

$milestones = Invoke-Native gh @("api", "repos/$Repo/milestones?state=all", "--jq", ".[].title")
if (-not ($milestones -contains "V1")) {
    Invoke-Native gh @("api", "repos/$Repo/milestones", "-f", "title=V1",
        "-f", "description=Première version utile : agent local, diagnostic, corrections validées, installeur signé (cahier des charges §8.2).") | Out-Null
}

$docs = "https://github.com/$Repo/blob/main/docs"
$issues = @(
    @{ Title = "Financer le certificat de signature avant la diffusion publique"; Labels = "v1,hors-code,securite"; Body = @"
Bloquant pour une diffusion publique (ENF-SEC-04), pas pour les tests ni la bêta. Détails : [docs/signature.md]($docs/signature.md).

- [ ] Tests et bêta : certificat de test gratuit (``build/new-test-certificate.ps1``), installé sur les PC de test
- [ ] Diffusion publique : certificat OV au nom de Solutech (Certum Standard cloud, environ 100 à 120 `$/an), ou SignPath Foundation si SoluPulse devient open source
- [ ] Ajouter les secrets ``SIGNING_CERT_PFX_BASE64`` et ``SIGNING_CERT_PASSWORD`` : la CI signe alors automatiquement
- [ ] Signer aussi le moteur Burn de l'installeur complet (``wix burn detach`` / ``reattach``)
"@ },
    @{ Title = "Valider l'installeur complet avec PawnIO"; Labels = "v1,tests,securite"; Body = @"
L'installeur ``SoluPulse-Setup.exe`` embarque l'installeur officiel de PawnIO, téléchargé par la CI avec vérification de signature.

- [ ] Au premier passage de la CI, relever le signataire et l'empreinte SHA-256 affichés, puis fixer ``-PawnIOSigner`` dans ``build/build-installer.ps1``
- [ ] Vérifier la clé de détection ``HKLM\SYSTEM\CurrentControlSet\Services\PawnIO`` (sinon PawnIO est réinstallé à chaque mise à jour, sans gravité)
- [ ] Scénarios I10 à I13 de [docs/tests-vm.md]($docs/tests-vm.md)
- [ ] Faire confirmer les conditions de redistribution (GPL-2.0 avec exception IOCTL ; licence et lien vers les sources fournis)
"@ },
    @{ Title = "Tester chaque action du catalogue sur machine virtuelle"; Labels = "v1,hors-code,tests"; Body = "Critère d'acceptation V1 : chaque action testée sur VM, annulation comprise. Suivre [docs/tests-vm.md]($docs/tests-vm.md) sur Windows 10 22H2 et Windows 11 (Hyper-V, points de contrôle) : installation, 22 actions, robustesse R1 à R6." },
    @{ Title = "Mesurer les performances sur 3 PC de gammes différentes"; Labels = "v1,hors-code,tests"; Body = "ENF-PERF-01 à 04 : CPU < 1 % sur 1 h, service < 80 Mo, interface < 200 Mo, diagnostic < 2 min, ouverture < 3 s. Inclure un PC à 4 Go de RAM (ENF-COMP-02). Mesure indicative en développement : 46 Mo d'octets privés pour le service (build Debug)." },
    @{ Title = "Bêta de 4 semaines sur 10 PC"; Labels = "v1,hors-code,tests"; Body = "Critère d'acceptation V1 : aucun PC endommagé, moins d'une fausse alerte critique par semaine et par PC (ENF-FIA-05). Ajuster les seuils des règles selon les retours. Installer le certificat de test sur les PC de la bêta ([docs/signature.md]($docs/signature.md))." },
    @{ Title = "Tester la compréhension des explications"; Labels = "v1,hors-code,tests"; Body = "ENF-ERG-01 : 8 testeurs non techniciens sur 10 reformulent correctement le problème à partir de l'explication simple. Tester au moins les 10 règles les plus fréquentes ; corriger les textes dans ``rules/*.yaml``." },
    @{ Title = "Analyse Microsoft Defender de l'installeur"; Labels = "v1,hors-code,securite"; Body = "Critère d'acceptation V1 : aucune détection sur l'installeur et le dossier installé (ENF-SEC-07). Soumettre l'installeur à Microsoft avant chaque diffusion : https://www.microsoft.com/wdsi/filesubmission" },
    @{ Title = "Décider de la licence de SoluPulse"; Labels = "v1,hors-code"; Body = "Point ouvert §9.2 (propriétaire ou open source). Une licence open source ouvre la signature gratuite par SignPath Foundation. Principales dépendances : MIT, Apache-2.0, MPL-2.0 (LibreHardwareMonitorLib), MS-RL (WiX), GPL-2.0 avec exception (PawnIO, redistribué sans modification). À vérifier : licence de RAMSPDToolkit-NDD (dépendance de LibreHardwareMonitorLib)." }
)

$existing = Invoke-Native gh @("issue", "list", "--repo", $Repo, "--state", "all", "--limit", "200", "--json", "title", "--jq", ".[].title")
foreach ($issue in $issues) {
    if ($existing -contains $issue.Title) {
        Write-Host "Existe déjà : $($issue.Title)"
        continue
    }

    $url = Invoke-Native gh @("issue", "create", "--repo", $Repo, "--title", $issue.Title, "--body", $issue.Body, "--label", $issue.Labels, "--milestone", "V1")
    Write-Host "Ticket créé : $url"
}

Write-Host "Terminé. Relisez la pull request, attendez que la CI passe, puis fusionnez-la."
