<#
.SYNOPSIS
    Construit l'installeur MSI de SoluPulse.

.DESCRIPTION
    1. Publie le service et l'application en mode autonome (runtime .NET embarqué, ENF-COMP-03).
    2. Signe les exécutables et bibliothèques SoluPulse si un certificat est fourni (ENF-SEC-04).
    3. Construit le MSI avec WiX, puis le signe.

    La signature utilise un certificat du magasin de l'utilisateur, désigné par son empreinte :
    -CertificateThumbprint ou variable d'environnement SOLUPULSE_SIGN_THUMBPRINT.
    Sans certificat, l'installeur est produit non signé et un avertissement est affiché.

.EXAMPLE
    ./build/build-installer.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$CertificateThumbprint = $env:SOLUPULSE_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publishAgent = Join-Path $artifacts "publish\Agent"
$publishApp = Join-Path $artifacts "publish\App"
$installerOut = Join-Path $artifacts "installer"

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
    Write-Warning "Aucun certificat de signature : l'installeur ne sera pas signé (interdit pour une diffusion, ENF-SEC-04)."
}

Write-Host "Construction du MSI…"
Invoke-Checked dotnet @("build", (Join-Path $root "installer\SoluPulse.Installer.wixproj"), "-c", $Configuration,
    "-p:ProductVersion=$Version", "-p:AgentDir=$publishAgent", "-p:AppDir=$publishApp", "-o", $installerOut)

$msi = Get-ChildItem $installerOut -Filter "*.msi" | Select-Object -First 1
Invoke-Sign @($msi.FullName)
Write-Host "Installeur : $($msi.FullName) ($([math]::Round($msi.Length / 1MB, 1)) Mo)"
