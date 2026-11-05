using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Options;
using SoluPulse.Core.Corrections;

namespace SoluPulse.Agent.Security;

/// <summary>
/// Authentifie l'appelant du canal nommé (ENF-SEC-02, §7.1) : utilisateur Windows connecté localement
/// (jeton de l'appelant obtenu par emprunt d'identité en mode « Identification »), et, en production,
/// processus client qui est bien l'application SoluPulse installée.
/// </summary>
internal sealed partial class CallerAuthenticator(IOptions<AgentOptions> options, ILogger<CallerAuthenticator> logger)
{
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);

    public CallerIdentity Authenticate(ServerCallContext context)
    {
        var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe
            ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "Seul le canal nommé local est accepté."));

        CallerIdentity? caller = null;
        var interactive = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            interactive = identity.Groups?.Contains(Interactive) == true;
            if (identity.User is { } sid)
            {
                caller = new CallerIdentity(sid.Value, identity.Name);
            }
        });

        if (caller is null || !caller.UserSid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || !interactive)
        {
            logger.LogWarning("Appel refusé : appelant non interactif ou inconnu ({Caller}).", caller?.UserName);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Appelant non autorisé."));
        }

        if (options.Value.RequireTrustedClient)
        {
            VerifyClientProcess(pipe.SafePipeHandle, caller);
        }

        return caller;
    }

    private void VerifyClientProcess(Microsoft.Win32.SafeHandles.SafePipeHandle handle, CallerIdentity caller)
    {
        string? path = null;
        if (GetNamedPipeClientProcessId(handle, out var pid))
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                path = null;
            }
        }

        if (!string.Equals(path, options.Value.ExpectedClientPath, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Appel refusé : client {Path} (utilisateur {User}) n'est pas l'application SoluPulse.", path ?? "inconnu", caller.UserName);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Client non reconnu."));
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
