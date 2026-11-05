using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using SoluPulse.Contracts;

namespace SoluPulse.Desktop.Services;

/// <summary>
/// Accès au service SoluPulse par le canal nommé local. L'application n'a aucun droit sur le système :
/// tout passe par ce client, et toute correction par la fiche de validation.
/// </summary>
public sealed class AgentClient : IDisposable
{
    private readonly GrpcChannel _channel = AgentPipe.CreateChannel();
    private readonly CancellationTokenSource _stop = new();

    public AgentClient()
    {
        Api = new AgentService.AgentServiceClient(_channel);
    }

    public AgentService.AgentServiceClient Api { get; }

    public bool Connected { get; private set; }

    public event EventHandler<AgentEvent>? EventReceived;

    public event EventHandler<bool>? ConnectionChanged;

    public static Empty Empty { get; } = new();

    /// <summary>
    /// Suit le flux d'événements du service et se reconnecte tant que l'application tourne : le service
    /// peut redémarrer (mise à jour, plantage) sans que l'application ait à être relancée.
    /// </summary>
    public void StartWatching()
    {
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(2);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var call = Api.WatchEvents(Empty, cancellationToken: _stop.Token);
                    await foreach (var agentEvent in call.ResponseStream.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                    {
                        SetConnected(true);
                        delay = TimeSpan.FromSeconds(2);
                        EventReceived?.Invoke(this, agentEvent);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is RpcException or IOException or TimeoutException or HttpRequestException)
                {
                    SetConnected(false);
                }

                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _channel.Dispose();
    }

    private void SetConnected(bool connected)
    {
        if (Connected != connected)
        {
            Connected = connected;
            ConnectionChanged?.Invoke(this, connected);
        }
    }

    /// <summary>Message lisible pour une erreur d'appel.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded } =>
            "Le service SoluPulse ne répond pas. Vérifiez qu'il est démarré.",
        RpcException { StatusCode: StatusCode.PermissionDenied } =>
            "Le service SoluPulse a refusé l'accès à cette application.",
        RpcException rpc => rpc.Status.Detail,
        IOException or TimeoutException or HttpRequestException =>
            "Le service SoluPulse ne répond pas. Vérifiez qu'il est démarré.",
        _ => ex.Message,
    };
}
