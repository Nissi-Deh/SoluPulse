using System.IO.Pipes;
using System.Security.Principal;

namespace SoluPulse.Platform.Windows.Interop;

public sealed record PipeCallerIdentity(SecurityIdentifier User, string UserName, bool Interactive);

/// <summary>
/// Identifie le client d'un canal nommé par emprunt d'identité (niveau « Identification »).
/// </summary>
public static class PipeCaller
{
    private static readonly SecurityIdentifier InteractiveSid = new(WellKnownSidType.InteractiveSid, null);

    /// <remarks>
    /// Seuls le SID et les groupes sont lus sous l'identité du client. <see cref="WindowsIdentity.Name"/> passe
    /// par <c>RunImpersonated</c>, qui laisse le jeton du client dans le contexte d'exécution de la requête :
    /// toute la suite de la requête (les actions de correction comprises) tournerait alors sous l'identité de
    /// l'utilisateur au niveau « Identification », sans accès aux fichiers ni aux services. Le nom est donc
    /// résolu après le retour à l'identité du service.
    /// </remarks>
    public static PipeCallerIdentity? Identify(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        SecurityIdentifier? user = null;
        var interactive = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            interactive = identity.Groups?.Contains(InteractiveSid) == true;
            user = identity.User;
        });

        return user is null ? null : new PipeCallerIdentity(user, NameOf(user), interactive);
    }

    private static string NameOf(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
