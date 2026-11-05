<#
.SYNOPSIS
    Mesure la consommation de SoluPulse installé et la compare aux cibles du cahier des charges.

.DESCRIPTION
    ENF-PERF-01 : CPU moyen du service < 1 % sur la durée de mesure (1 heure par défaut).
    ENF-PERF-02 : octets privés du service < 80 Mo, de l'application < 200 Mo.
    ENF-PERF-04 : ouverture de l'interface < 3 s (mesurée si -MesurerOuverture).

    Le CPU est rapporté à l'ensemble des cœurs, comme le Gestionnaire des tâches. Lancer en
    administrateur pour lire le processus du service (compte SYSTEM). Le résultat est aussi écrit
    dans un fichier CSV pour le joindre au ticket.

.EXAMPLE
    ./tools/mesurer-performances.ps1                    # 1 heure
    ./tools/mesurer-performances.ps1 -Minutes 10 -MesurerOuverture
#>
[CmdletBinding()]
param(
    [int]$Minutes = 60,
    [int]$IntervalleSecondes = 10,
    [switch]$MesurerOuverture,
    [string]$Sortie = (Join-Path $env:TEMP "solupulse-performances.csv")
)

$ErrorActionPreference = "Stop"
$cores = [Environment]::ProcessorCount
$appPath = Join-Path $env:ProgramFiles "SoluPulse\App\SoluPulse.Desktop.exe"

function Get-Sample([string]$Name) {
    $process = Get-Process -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) { return $null }
    return [pscustomobject]@{ Cpu = $process.TotalProcessorTime.TotalSeconds; Prive = $process.PrivateMemorySize64 / 1MB }
}

if ($MesurerOuverture) {
    Get-Process SoluPulse.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $app = Start-Process $appPath -PassThru
    while ($app.MainWindowHandle -eq 0 -and $watch.Elapsed.TotalSeconds -lt 30) {
        Start-Sleep -Milliseconds 100
        $app.Refresh()
    }
    $ouverture = $watch.Elapsed.TotalSeconds
    Write-Host ("Ouverture de l'interface : {0:N1} s (cible < 3 s)" -f $ouverture)
}

$first = @{ Agent = Get-Sample "SoluPulse.Agent"; App = Get-Sample "SoluPulse.Desktop" }
if (-not $first.Agent) { throw "Processus SoluPulse.Agent introuvable : SoluPulse est-il installé et le script lancé en administrateur ?" }

$rows = New-Object System.Collections.Generic.List[object]
$watch = [Diagnostics.Stopwatch]::StartNew()
$previous = $first
$end = (Get-Date).AddMinutes($Minutes)
Write-Host "Mesure pendant $Minutes min, un relevé toutes les $IntervalleSecondes s…"
while ((Get-Date) -lt $end) {
    Start-Sleep -Seconds $IntervalleSecondes
    $current = @{ Agent = Get-Sample "SoluPulse.Agent"; App = Get-Sample "SoluPulse.Desktop" }
    $row = [ordered]@{ Heure = (Get-Date).ToString("HH:mm:ss") }
    foreach ($key in "Agent", "App") {
        if ($current[$key] -and $previous[$key]) {
            $row["$key CPU %"] = [math]::Round(100 * ($current[$key].Cpu - $previous[$key].Cpu) / $IntervalleSecondes / $cores, 2)
            $row["$key Mo"] = [math]::Round($current[$key].Prive, 1)
        }
    }
    $rows.Add([pscustomobject]$row)
    $previous = $current
}

$rows | Export-Csv $Sortie -NoTypeInformation -Encoding UTF8 -Delimiter ";"

$last = @{ Agent = Get-Sample "SoluPulse.Agent"; App = Get-Sample "SoluPulse.Desktop" }
$cpuAgent = 100 * ($last.Agent.Cpu - $first.Agent.Cpu) / $watch.Elapsed.TotalSeconds / $cores
$maxAgent = ($rows | Measure-Object "Agent Mo" -Maximum).Maximum
$maxApp = ($rows | Where-Object { $_.PSObject.Properties["App Mo"] } | Measure-Object "App Mo" -Maximum).Maximum

function Verdict([bool]$ok) { if ($ok) { "OK" } else { "HORS CIBLE" } }
Write-Host ""
Write-Host ("Service : CPU moyen {0:N2} % (cible < 1 %) : {1}" -f $cpuAgent, (Verdict ($cpuAgent -lt 1)))
Write-Host ("Service : mémoire privée max {0:N0} Mo (cible < 80 Mo) : {1}" -f $maxAgent, (Verdict ($maxAgent -lt 80)))
if ($maxApp) {
    Write-Host ("Application : mémoire privée max {0:N0} Mo (cible < 200 Mo) : {1}" -f $maxApp, (Verdict ($maxApp -lt 200)))
}
Write-Host "Machine : $cores cœurs, $([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)) Go de RAM"
Write-Host "Relevés détaillés : $Sortie"
