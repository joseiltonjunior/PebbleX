using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PebbleX.Core.Flow;

namespace PebbleX.Windows.Tests;

public sealed class FlowPeerTransportTests
{
    [Fact]
    public async Task DeliversOnlyAuthenticatedSignalFromConfiguredPeer()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var port = GetFreePort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var peer = new FlowPeer(senderId, endpoint, key);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<FlowSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FlowPeerTransport();
        var listening = transport.ListenAsync(endpoint,
            new Dictionary<Guid, FlowPeer> { [senderId] = peer },
            (signal, _) => { received.TrySetResult(signal); cancellation.Cancel(); },
            _ => { }, cancellation.Token);

        var sentSignal = new FlowSignal(senderId, Guid.NewGuid(), FlowSignalKind.KeyboardChannelObserved, 2, DateTimeOffset.UtcNow);
        await transport.SendAsync(new FlowPeer(receiverId, endpoint, key), sentSignal, CancellationToken.None);

        Assert.Equal(sentSignal, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await listening;
    }

    [Fact]
    public async Task RejectsUnpairedPeerBeforeCallback()
    {
        var trustedKey = RandomNumberGenerator.GetBytes(32);
        var untrustedKey = RandomNumberGenerator.GetBytes(32);
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var endpoint = new IPEndPoint(IPAddress.Loopback, GetFreePort());
        using var cancellation = new CancellationTokenSource();
        var received = 0;
        var transport = new FlowPeerTransport();
        var listening = transport.ListenAsync(endpoint,
            new Dictionary<Guid, FlowPeer> { [senderId] = new(senderId, endpoint, trustedKey) },
            (_, _) => Interlocked.Increment(ref received), _ => { }, cancellation.Token);

        var message = new FlowSignal(senderId, Guid.NewGuid(), FlowSignalKind.KeyboardChannelObserved, 1, DateTimeOffset.UtcNow);
        await transport.SendAsync(new FlowPeer(receiverId, endpoint, untrustedKey), message, CancellationToken.None);
        await Task.Delay(150);
        cancellation.Cancel();
        await listening;

        Assert.Equal(0, received);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
