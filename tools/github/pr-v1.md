## Résumé

Implémentation de la **V1** de SoluPulse selon le cahier des charges : service Windows, application de bureau, diagnostic local par règles, alertes, corrections validées et réversibles, installeur MSI, intégration continue.

## Contenu

| Lot | Exigences couvertes |
| --- | --- |
| Domaine et moteur de règles (`SoluPulse.Core`) | EF-DIA-01 à 04, 06 à 08 ; ENF-MAIN-05 |
| Stockage SQLite WAL (`SoluPulse.Storage`) | EF-COL-10, ENF-PERF-05 (rétention 90 j puis agrégats horaires) |
| Collecteurs Windows (`SoluPulse.Platform.Windows`) | EF-COL-02 à 08, 11, 12 ; capteurs via PawnIO, sans WinRing0 (ENF-SEC-07) |
| Catalogue d'actions : 18 actions + 4 recommandations | EF-COR-07 et familles V1 du §3.3 |
| Exécuteur de corrections | EF-COR-01 à 06, ENF-FIA-02, ENF-SEC-06 |
| Service (`SoluPulse.Agent`) | EF-COL-01, ENF-SEC-01/02, ENF-MAIN-04, ENF-PERF-06 |
| Application Avalonia (`SoluPulse.Desktop`) | EF-IHM-01 à 06, ENF-ERG-02 à 04 |
| Installeur complet `SoluPulse-Setup.exe` (PawnIO + MSI WiX 5), script de signature, certificat de test gratuit | ENF-COMP-03, ENF-FIA-01, ENF-SEC-04 (signature reconnue dès qu'un certificat existe) |
| CI GitHub Actions, CodeQL, Dependabot | §6.5, §7.3 |
| 33 règles matériel, système, réseau ; documentation | Livrables V1 du §8.1 |

## Sécurité

- Une correction ne s'exécute qu'avec un jeton à usage unique délivré à l'affichage de la fiche, valable 10 min et lié au compte Windows qui valide. Test automatisé : `CorrectionExecutorTests.NoActionRunsWithoutAPreparedAndValidatedCard`.
- Canal nommé : utilisateurs interactifs seulement, accès réseau refusé, identité de l'appelant lue sur son jeton, chemin du client vérifié en production.
- Paramètres choisis dans des listes fournies par le service et revalidés ; outils Windows appelés avec des arguments fixes.
- Point de restauration avant tout risque moyen ou élevé, sauvegarde et annulation, reprise après coupure, audit chaîné par SHA-256.

## Vérifications

- `dotnet build -c Release -warnaserror` : aucun avertissement.
- 84 tests : Core 54, Storage 8, Platform.Windows 22.
- Couverture : moteur de règles 94 %, chargeur 100 %, exécuteur 88 %, stockage 96 %, couche Windows 64 %. L'exécution réelle des actions se teste sur VM.
- Essai réel sur un PC de développement, service lancé sans droits administrateur : 43 métriques relevées, capteurs inaccessibles signalés sans plantage, parcours complet par le canal nommé (fiche, refus d'un jeton annulé ou inventé, audit intact). L'essai a révélé un bug de seuil personnalisé fantôme (fausses alertes), corrigé avec un test de non-régression.
- MSI et installeur complet construits localement (≈ 99 Mo, deux runtimes .NET embarqués). Pas encore installés : à faire sur VM. Le premier passage de la CI téléchargera le vrai PawnIO (réseau d'entreprise sans GitHub en local).

## Reste à faire avant la sortie V1

Voir le jalon **V1** : financement du certificat de signature (ou SignPath si open source), validation de l'installeur PawnIO, tests de chaque action sur VM, mesures de performance, bêta, tests de compréhension des explications, analyse Defender, licence.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
