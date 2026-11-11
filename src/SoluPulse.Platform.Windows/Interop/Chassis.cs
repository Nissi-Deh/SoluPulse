namespace SoluPulse.Platform.Windows.Interop;

/// <summary>Type de boîtier déclaré par le BIOS (Win32_SystemEnclosure.ChassisTypes).</summary>
public static class Chassis
{
    // Portable, ordinateur portable, bloc-notes, ordinateur de poche, station d'accueil, sous-notebook,
    // boîtier d'extension, tablette, convertible, détachable.
    private static readonly HashSet<ushort> PortableTypes = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];

    private static readonly Lazy<bool?> Portable = new(Read);

    /// <summary><c>true</c> pour un portable, <c>false</c> pour un PC fixe, <c>null</c> si le BIOS ne le dit pas.</summary>
    public static bool? IsPortable() => Portable.Value;

    internal static bool? Classify(IEnumerable<ushort> types)
    {
        var known = types.Where(t => t is not (1 or 2)).ToList();
        return known.Count == 0 ? null : known.Any(PortableTypes.Contains);
    }

    private static bool? Read()
    {
        try
        {
            return Wmi.QueryFirst(@"\\.\root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure") is { } enclosure
                && enclosure["ChassisTypes"] is ushort[] types
                ? Classify(types)
                : null;
        }
        catch (System.Management.ManagementException)
        {
            return null;
        }
    }
}
