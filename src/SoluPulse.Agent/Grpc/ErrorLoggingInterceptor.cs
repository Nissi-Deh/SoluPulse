using Grpc.Core;
using Grpc.Core.Interceptors;

namespace SoluPulse.Agent.Grpc;

/// <summary>Journalise les erreurs inattendues ; l'application ne reçoit qu'un message générique.</summary>
internal sealed class ErrorLoggingInterceptor(ILogger<ErrorLoggingInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            logger.LogError(ex, "Erreur inattendue dans {Method}.", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, "Erreur interne du service SoluPulse. Le détail est dans le journal technique."));
        }
    }
}
