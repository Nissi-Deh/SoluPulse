using System.IO.Pipes;
using System.Security.Principal;
using Grpc.Net.Client;

namespace SoluPulse.Contracts;

/// <summary>Canal nommé local entre l'application et le service (cahier des charges §6.3).</summary>
public static class AgentPipe
{
    public const string Name = "SoluPulse.Agent.v1";

    /// <summary>
    /// Crée un client gRPC sur le canal nommé. Le niveau d'emprunt d'identité « Identification » permet
    /// au service de connaître l'utilisateur appelant, sans pouvoir agir en son nom.
    /// </summary>
    public static GrpcChannel CreateChannel()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.WriteThrough | PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                try
                {
                    await pipe.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
        };

        return GrpcChannel.ForAddress("http://solupulse.local", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = 16 * 1024 * 1024,
        });
    }
}
