using Avalonia.Media;

namespace SoluPulse.Desktop.Services;

/// <summary>
/// Icônes au trait, dessinées dans une grille de 24 × 24 et affichées par <c>IconView</c>.
/// Elles remplacent les émojis, dont le rendu change d'une version de Windows à l'autre.
/// </summary>
public static class Icons
{
    public static readonly Geometry Dashboard = Parse("M4 17 A8 8 0 1 1 20 17 M12 17 L16 11.5 M4 20 H20");
    public static readonly Geometry Computer = Parse("M5 5.5 H19 V15 H5 Z M3 18.5 H21");
    public static readonly Geometry Alert = Parse("M12 4 L21 19.5 H3 Z M12 10 V14 M12 16.8 V17");
    public static readonly Geometry History = Parse("M4 4 V20 H20 M7.5 15 L11 11 L14 14 L19 8");
    public static readonly Geometry Journal = Parse("M9 3.5 H15 V6.5 H9 Z M7 5 H5.5 V20.5 H18.5 V5 H17 M9 13.5 L11 15.5 L15 11");
    public static readonly Geometry Inventory = Parse("M9 6.5 H20 M9 12 H20 M9 17.5 H20 M4.5 6.5 H5 M4.5 12 H5 M4.5 17.5 H5");
    public static readonly Geometry Settings = Parse("M4 7 H13 M17 7 H20 M4 17 H7 M11 17 H20 M13 7 A2 2 0 1 0 17 7 A2 2 0 1 0 13 7 M7 17 A2 2 0 1 0 11 17 A2 2 0 1 0 7 17");
    public static readonly Geometry Search = Parse("M4 10.5 A6.5 6.5 0 1 0 17 10.5 A6.5 6.5 0 1 0 4 10.5 M15.5 15.5 L20 20");
    public static readonly Geometry Refresh = Parse("M19.5 12 A7.5 7.5 0 1 1 17.3 6.7 M19.5 4 V8 H15.5");
    public static readonly Geometry Shield = Parse("M12 3 L19 6 V11 C19 15.5 16 19 12 21 C8 19 5 15.5 5 11 V6 Z M9 12 L11 14 L15 10");
    public static readonly Geometry Processor = Parse("M7 7 H17 V17 H7 Z M10 10 H14 V14 H10 Z M9.5 3.5 V7 M14.5 3.5 V7 M9.5 17 V20.5 M14.5 17 V20.5 M3.5 9.5 H7 M3.5 14.5 H7 M17 9.5 H20.5 M17 14.5 H20.5");
    public static readonly Geometry Memory = Parse("M3 7 H21 V15 H3 Z M7 10 V12 M11 10 V12 M15 10 V12 M6 15 V18 M10 15 V18 M14 15 V18 M18 15 V18");
    public static readonly Geometry Graphics = Parse("M3 6 H18 V17 H3 Z M18 9 H21 M18 13 H21 M7.5 11.5 A3 3 0 1 0 13.5 11.5 A3 3 0 1 0 7.5 11.5 M6 17 V20 M10 17 V20");
    public static readonly Geometry Disk = Parse("M4 5 H20 V19 H4 Z M4 14 H20 M15.5 16.5 H16.5 M7.5 16.5 H11");
    public static readonly Geometry Volumes = Parse("M4 7 C4 5 20 5 20 7 V17 C20 19 4 19 4 17 Z M4 7 C4 9 20 9 20 7 M4 12 C4 14 20 14 20 12");
    public static readonly Geometry Battery = Parse("M3 8 H18 V16 H3 Z M21 10.5 V13.5 M6 11 V13 M9 11 V13 M12 11 V13");
    public static readonly Geometry Network = Parse("M2.5 9 A14 14 0 0 1 21.5 9 M5.5 12.5 A9.5 9.5 0 0 1 18.5 12.5 M8.5 16 A5 5 0 0 1 15.5 16 M12 19.5 V19.6");
    public static readonly Geometry Peripherals = Parse("M3 4.5 H21 V15.5 H3 Z M8 20 H16 M12 15.5 V20");
    public static readonly Geometry Windows = Parse("M4 4 H11 V11 H4 Z M13 4 H20 V11 H13 Z M4 13 H11 V20 H4 Z M13 13 H20 V20 H13 Z");
    public static readonly Geometry Check = Parse("M4 12 A8 8 0 1 0 20 12 A8 8 0 1 0 4 12 M8.5 12 L11 14.5 L15.5 9.5");

    /// <summary>Icône d'un domaine du score de santé.</summary>
    public static Geometry ForDomain(string domain) => domain switch
    {
        "materiel" => Processor,
        "systeme" => Windows,
        "reseau" => Network,
        "securite" => Shield,
        _ => Check,
    };

    private static Geometry Parse(string data) => Geometry.Parse(data);
}
