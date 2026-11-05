using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SoluPulse.Agent.Security;

/// <summary>Listes de contrôle d'accès du canal nommé et du dossier de données (ENF-SEC-02, §7.2).</summary>
internal static class AccessControl
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    /// <summary>
    /// Le canal n'est ouvert qu'aux utilisateurs connectés localement : les accès depuis le réseau sont
    /// explicitement refusés, les autres comptes de service n'ont aucun droit.
    /// </summary>
    public static PipeSecurity PipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Le compte du service doit pouvoir créer les instances suivantes du canal.
        using (var self = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(Administrators, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(Interactive, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Le dossier de données (base SQLite, journaux) n'est lisible que par SYSTEM et les administrateurs :
    /// l'interface y accède uniquement à travers le service.
    /// </summary>
    public static void ProtectDataDirectory(string directory)
    {
        var info = Directory.CreateDirectory(directory);
        if (!IsSystemOrElevated())
        {
            return; // Développement sans élévation : rien à protéger.
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(Administrators);
        info.SetAccessControl(security);
    }

    public static bool IsSystemOrElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
