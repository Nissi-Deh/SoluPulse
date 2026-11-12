using Microsoft.Win32;

namespace SoluPulse.Platform.Windows.Interop;

/// <summary>Un compte utilisateur du PC : son identifiant, son dossier et sa ruche si elle est chargée.</summary>
public sealed record UserProfile(string Sid, string Folder)
{
    public string LocalAppData => Path.Combine(Folder, "AppData", "Local");

    /// <summary>La ruche HKEY_USERS du compte : chargée seulement quand l'utilisateur a une session ouverte.</summary>
    public RegistryKey? OpenKey(string subKey)
    {
        try
        {
            return Registry.Users.OpenSubKey($@"{Sid}\{subKey}");
        }
        catch (System.Security.SecurityException)
        {
            // Ruche d'un autre compte, illisible hors du service.
            return null;
        }
    }
}

/// <summary>Les comptes de personnes (S-1-5-21-…) déclarés dans la liste des profils de Windows.</summary>
public static class UserProfiles
{
    public static IReadOnlyList<UserProfile> All() =>
        StartupInventory.UserProfiles().Select(p => new UserProfile(p.Sid, Environment.ExpandEnvironmentVariables(p.Path))).ToList();
}
