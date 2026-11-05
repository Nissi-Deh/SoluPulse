# SoluPulse

Agent Windows qui surveille le PC en continu, explique les problèmes en français simple et propose des corrections que l'utilisateur valide. Projet Solutech ; le cahier des charges complet est dans [`SoluPulse Cahier des charges et cahier des exigences.md`](SoluPulse%20Cahier%20des%20charges%20et%20cahier%20des%20exigences.md).

Ce dépôt contient la **V1** : service Windows, application de bureau, diagnostic local, alertes, corrections réversibles et installeur MSI.

## Architecture

Deux processus séparés sur le PC (cahier des charges §5) :

```
┌──────────────────────────────┐   gRPC sur canal nommé local   ┌───────────────────────────────────┐
│ SoluPulse.Desktop            │   protégé par ACL, appelant     │ SoluPulse.Agent (service, SYSTEM) │
│ session de l'utilisateur,    │ ◄─────────────────────────────► │ collecteurs → SQLite → règles     │
│ sans droits administrateur   │   authentifié                   │ → alertes ; exécuteur d'actions   │
└──────────────────────────────┘                                 └───────────────────────────────────┘
```

| Projet | Rôle |
| --- | --- |
| `SoluPulse.Core` | Domaine, moteur de règles YAML, scores, exécuteur de corrections, audit chaîné. Aucun appel Windows (ENF-MAIN-05). |
| `SoluPulse.Storage` | Base SQLite (WAL) : mesures et agrégats, alertes, exécutions, audit, paramètres. |
| `SoluPulse.Platform.Windows` | Collecteurs (capteurs, disques, batterie, performances, journaux, inventaire, réseau, Windows Update) et catalogue d'actions. |
| `SoluPulse.Contracts` | Contrat gRPC entre l'application et le service. |
| `SoluPulse.Agent` | Service Windows : planification, évaluation, API sur canal nommé. |
| `SoluPulse.Desktop` | Application Avalonia : tableau de bord, alertes, fiche de validation, historique, journal, paramètres, notifications, icône de zone de notification. |
| `rules/` | Base de règles livrée (33 règles matériel, système, réseau). Format : [docs/regles.md](docs/regles.md). |
| `installer/` | Installeur MSI (WiX 5). |

### Garanties de sécurité

- **Aucune action sans validation** (EF-COR-01) : l'exécuteur n'agit qu'avec un jeton à usage unique, délivré quand la fiche est affichée, valable 10 minutes et réservé à l'utilisateur Windows qui l'a demandée. Un test automatisé le vérifie (`CorrectionExecutorTests`).
- **Catalogue fermé** (EF-COR-07) : 18 actions compilées et 4 recommandations guidées. Les paramètres sont choisis dans une liste fournie par le service et revalidés avant exécution. Les outils Windows sont appelés avec des arguments fixes, sans interpréteur de commandes.
- **Avant d'agir** : point de restauration pour tout risque moyen ou élevé, sauvegarde de l'état modifié, journal écrit avant chaque étape. Une correction interrompue est annulée au redémarrage (ENF-FIA-02).
- **Canal local** : accessible aux seuls utilisateurs connectés localement, accès réseau refusé ; en production, seul l'exécutable SoluPulse installé est accepté comme client.
- **Journal d'audit** chaîné par SHA-256 : toute modification ou suppression d'une entrée est détectée.
- **Aucun pilote vulnérable** : les capteurs passent par PawnIO, jamais par WinRing0.

## Développement

Prérequis : Windows 10 22H2 ou 11 x64, SDK .NET 10.

```powershell
dotnet build SoluPulse.sln
dotnet test SoluPulse.sln
```

Lancer le service et l'application sans les installer :

```powershell
# Terminal 1 : le service, en mode développement (client du canal non vérifié, données dans un dossier local)
$env:DOTNET_ENVIRONMENT = "Development"; $env:SoluPulse__DataDirectory = "$PWD\.devdata"
dotnet src\SoluPulse.Agent\bin\Debug\net10.0-windows\win-x64\SoluPulse.Agent.dll

# Terminal 2 : l'application
dotnet run --project src\SoluPulse.Desktop
```

Sans droits administrateur, certains capteurs sont absents (SMART, durée de démarrage) : ils sont signalés dans le tableau de bord et la surveillance continue. Lancé depuis un terminal administrateur, le service les lit tous.

## Installeur

```powershell
./build/build-installer.ps1 -Version 1.0.0                                  # non signé, pour les tests
./build/build-installer.ps1 -Version 1.0.0 -CertificateThumbprint <empreinte>  # signé (diffusion)
```

Le MSI (`artifacts/installer/`) installe le service (compte SYSTEM, démarrage automatique, redémarrage après échec), l'application (lancée à chaque ouverture de session) et le runtime .NET. L'intégration continue le produit à chaque commit ; elle le signe si les secrets `SIGNING_CERT_PFX_BASE64` et `SIGNING_CERT_PASSWORD` sont configurés.

## Documentation

- [Guide utilisateur](docs/guide-utilisateur.md)
- [Données collectées](docs/donnees-collectees.md) (ENF-CONF-02)
- [Format des règles](docs/regles.md)
- [Plan de test sur machines virtuelles](docs/tests-vm.md) (critères d'acceptation V1)
- [Signaler une vulnérabilité](SECURITY.md)
