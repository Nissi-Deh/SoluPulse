namespace SoluPulse.Agent;

public sealed class AgentOptions
{
    public const string Section = "SoluPulse";

    /// <summary>Base, journaux techniques. Par défaut %ProgramData%\SoluPulse, protégé par ACL.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoluPulse");

    /// <summary>Règles livrées avec l'installation, dans le dossier du programme (écriture réservée aux administrateurs).</summary>
    public string RulesDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "rules");

    /// <summary>
    /// N'accepter que l'application SoluPulse installée comme client du canal nommé. Désactivé
    /// uniquement en développement, où l'application tourne depuis le dossier de compilation.
    /// </summary>
    public bool RequireTrustedClient { get; set; } = true;

    /// <summary>Chemin attendu de l'application cliente ; par défaut à côté du service.</summary>
    public string? TrustedClientPath { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, "data", "solupulse.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string ExpectedClientPath =>
        TrustedClientPath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "App", "SoluPulse.Desktop.exe"));
}
