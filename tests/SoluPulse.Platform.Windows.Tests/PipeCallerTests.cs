using System.IO.Pipes;
using System.Security.Principal;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class PipeCallerTests
{
    [Fact]
    public async Task IdentifiesTheClientWithoutLeakingItsTokenIntoTheRequest()
    {
        var name = $"solupulse-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connection = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(5000);
        await connection;

        // L'emprunt d'identité n'est possible qu'après une première lecture sur le canal.
        var read = server.ReadAsync(new byte[1]).AsTask();
        await client.WriteAsync(new byte[] { 1 });
        await read;

        var caller = PipeCaller.Identify(server);

        using var self = WindowsIdentity.GetCurrent();
        Assert.NotNull(caller);
        Assert.Equal(self.User, caller.User);
        Assert.Equal(self.Name, caller.UserName);

        // Régression : les actions lancées ensuite dans la requête doivent tourner sous l'identité du
        // service, pas sous celle du client (ni sur ce fil, ni sur ceux du pool).
        Assert.Null(WindowsIdentity.GetCurrent(true));
        var pool = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var impersonated = WindowsIdentity.GetCurrent(true);
            return impersonated is null;
        })));
        Assert.All(pool, Assert.True);
    }
}
