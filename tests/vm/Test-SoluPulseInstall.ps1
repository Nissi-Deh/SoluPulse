<#
.SYNOPSIS
    Vérifie automatiquement une installation de SoluPulse sur une machine de test (docs/tests-vm.md).

.DESCRIPTION
    À lancer en administrateur sur la VM, après avoir installé SoluPulse-Setup-<version>.exe.
    Couvre les vérifications I2, I3, I4, I10 et I13, plus l'entrée de démarrage et le canal nommé.
    Le test I3 arrête de force le service pour vérifier qu'il redémarre seul : ne pas lancer sur un
    PC de production.

.EXAMPLE
    ./Test-SoluPulseInstall.ps1
    ./Test-SoluPulseInstall.ps1 -SkipRestartTest
#>
[CmdletBinding()]
param([switch]$SkipRestartTest)

$ErrorActionPreference = "Stop"
$results = New-Object System.Collections.Generic.List[object]

function Check([string]$Id, [string]$Description, [scriptblock]$Test) {
    try {
        $detail = & $Test
        $results.Add([pscustomobject]@{ Id = $Id; Verification = $Description; Resultat = "OK"; Detail = "$detail" })
    }
    catch {
        $results.Add([pscustomobject]@{ Id = $Id; Verification = $Description; Resultat = "ÉCHEC"; Detail = $_.Exception.Message })
    }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Lancez ce script en administrateur."
}

$install = Join-Path $env:ProgramFiles "SoluPulse"
$data = Join-Path $env:ProgramData "SoluPulse"

Check "I2" "Service SoluPulse en démarrage automatique sous LocalSystem" {
    $service = Get-CimInstance Win32_Service -Filter "Name='SoluPulse'"
    if (-not $service) { throw "Service absent." }
    if ($service.StartMode -ne "Auto") { throw "Démarrage : $($service.StartMode)" }
    if ($service.StartName -ne "LocalSystem") { throw "Compte : $($service.StartName)" }
    if ($service.State -ne "Running") { throw "État : $($service.State)" }
    "PID $($service.ProcessId)"
}

Check "I2b" "Redémarrage automatique configuré après échec" {
    $failure = (& sc.exe qfailure SoluPulse) -join " "
    if ($failure -notmatch "RESTART") { throw $failure }
    "actions de reprise : redémarrage"
}

Check "I4" "Dossier de données réservé à SYSTEM et aux administrateurs" {
    $acl = Get-Acl $data
    $others = $acl.Access | Where-Object {
        $_.AccessControlType -eq "Allow" -and
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @("S-1-5-18", "S-1-5-32-544")
    }
    if ($others) { throw "Accès en trop : $(($others | ForEach-Object IdentityReference) -join ', ')" }
    if ($acl.AreAccessRulesProtected -ne $true) { throw "Héritage des droits non coupé." }
    "SYSTEM, Administrateurs"
}

Check "I4b" "Base SQLite créée" {
    $db = Join-Path $data "data\solupulse.db"
    if (-not (Test-Path $db)) { throw "Base absente : $db" }
    "$([math]::Round((Get-Item $db).Length / 1KB)) Ko"
}

Check "I5" "Application lancée à l'ouverture de session (clé Run machine)" {
    $run = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -Name SoluPulse -ErrorAction Stop
    if ($run.SoluPulse -notmatch "SoluPulse\.Desktop\.exe.+--background") { throw $run.SoluPulse }
    $run.SoluPulse
}

Check "I5b" "Canal nommé du service ouvert" {
    if (-not (Test-Path "\\.\pipe\SoluPulse.Agent.v1")) { throw "Canal SoluPulse.Agent.v1 absent." }
    "\\.\pipe\SoluPulse.Agent.v1"
}

Check "I10" "Pilote PawnIO installé" {
    if (-not (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO")) { throw "Clé de service PawnIO absente (vérifier le nom réel du service)." }
    "service PawnIO présent"
}

Check "I13" "Licences installées" {
    $files = @("PawnIO-GPL-2.0.txt", "COMPOSANTS-TIERS.txt") | ForEach-Object { Join-Path $install "Licences\$_" }
    $missing = $files | Where-Object { -not (Test-Path $_) }
    if ($missing) { throw "Absents : $($missing -join ', ')" }
    "2 fichiers"
}

Check "SEC" "Aucun pilote WinRing0 installé" {
    $winring = Get-CimInstance Win32_SystemDriver | Where-Object { $_.Name -match "WinRing0" -or $_.PathName -match "WinRing0" }
    if ($winring) { throw "Pilote WinRing0 trouvé : $($winring.Name -join ', ')" }
    "aucun"
}

Check "LOG" "Journal technique sans erreur" {
    $log = Get-ChildItem (Join-Path $data "logs") -Filter "agent-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { throw "Aucun journal." }
    $errors = Select-String -Path $log.FullName -Pattern "\[(ERR|FTL)\]"
    if ($errors) { throw "$($errors.Count) erreur(s), première : $($errors[0].Line)" }
    $log.Name
}

if (-not $SkipRestartTest) {
    Check "I3" "Reprise du service en moins d'une minute après un arrêt brutal (ENF-FIA-01)" {
        $before = (Get-CimInstance Win32_Service -Filter "Name='SoluPulse'").ProcessId
        Stop-Process -Id $before -Force
        $watch = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Seconds 2
            $service = Get-CimInstance Win32_Service -Filter "Name='SoluPulse'"
        } while (($service.State -ne "Running" -or $service.ProcessId -eq $before) -and $watch.Elapsed.TotalSeconds -lt 90)
        if ($service.State -ne "Running") { throw "Service non redémarré après 90 s." }
        if ($watch.Elapsed.TotalSeconds -gt 60) { throw "Redémarré en $([int]$watch.Elapsed.TotalSeconds) s (cible : 60 s)." }
        "redémarré en $([int]$watch.Elapsed.TotalSeconds) s"
    }
}

$results | Format-Table -AutoSize -Wrap
$failed = @($results | Where-Object Resultat -ne "OK").Count
if ($failed -gt 0) {
    Write-Host "$failed vérification(s) en échec." -ForegroundColor Red
    exit 1
}

Write-Host "Toutes les vérifications automatiques sont OK. Restent les tests manuels de docs/tests-vm.md." -ForegroundColor Green
