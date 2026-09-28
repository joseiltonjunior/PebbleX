using System.Net;
using System.Net.Sockets;
using PebbleX.Core.Flow;

namespace PebbleX.Windows.Tests;

public sealed class FlowPairingServiceTests
{
    [Fact]
    public async Task PairingRequiresMatchingShortCodeAndDerivesSameKeyOnBothComputers()
    {
        var serverId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var port = GetFreePort();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverChallenge = new TaskCompletionSource<FlowPairingChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverResult = new TaskCompletionSource<FlowPairingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pairing = new FlowPairingService();

        var listening = pairing.ListenForPairingAsync(serverId, "PC secundário", 47631,
            challenge =>
            {
                serverChallenge.TrySetResult(challenge);
                return Task.FromResult(true);
            },
            result => serverResult.TrySetResult(result), _ => { }, cancellation.Token, port);

        var peer = new FlowDiscoveredPeer(serverId, "PC secundário", IPAddress.Loopback,
            47631, port, DateTimeOffset.UtcNow);
        var client = await pairing.PairAsync(clientId, "PC principal", peer, async challenge =>
        {
            var serverRequest = await serverChallenge.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(serverRequest.VerificationCode, challenge.VerificationCode);
            return true;
        }, cancellation.Token);
        var server = await serverResult.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(serverId, client.PeerInstallationId);
        Assert.Equal(clientId, server.PeerInstallationId);
        Assert.Equal(client.SharedKeyHex, server.SharedKeyHex);
        Assert.Equal(64, client.SharedKeyHex.Length); // The derived key is 256 bits on both sides.
        Assert.Matches("^[0-9]{8}$", (await serverChallenge.Task).VerificationCode);

        cancellation.Cancel();
        await listening;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
