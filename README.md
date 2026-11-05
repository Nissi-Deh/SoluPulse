# SoluPulse

Socle de la V1 de SoluPulse, agent de surveillance Windows défini dans le cahier des charges.

## Projets

- `SoluPulse.Core` : types métier partagés, sans dépendance Windows.
- `SoluPulse.Agent` : service hébergé Windows, point d’entrée pour les collecteurs.
- `SoluPulse.Desktop` : interface utilisateur Avalonia.
- `SoluPulse.Core.Tests` : tests unitaires du domaine.

## Prérequis

- SDK .NET 10
- Windows 10 22H2 ou Windows 11 pour exécuter le service Windows

## Construire et tester

```powershell
dotnet restore SoluPulse.sln
dotnet build SoluPulse.sln
dotnet test SoluPulse.sln
```

Le service et l’interface sont encore des squelettes : aucun capteur, stockage, moteur de règles ni action système n’est activé. En particulier, aucune correction n’est exécutée à ce stade.
