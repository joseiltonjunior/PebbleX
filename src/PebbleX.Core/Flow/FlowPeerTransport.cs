using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace PebbleX.Core.Flow;

public sealed record FlowPeer(Guid InstallationId, IPEndPoint Endpoint, byte[] SharedKey, string? DisplayName = null);

/// <summary>Length-framed TCP transport for explicitly paired peers. Only authenticated, fresh signals reach the callback.</summary>
public sealed class FlowPeerTransport
{
    private const int MaximumEnvelopeBytes = 4096;
    private const int MaximumConcurrentConnections = 8;
    private readonly ConcurrentDictionary<(Guid Sender, Guid Transfer, FlowSignalKind Kind), DateTimeOffset> seen = new();

    public async Task SendAsync(FlowPeer peer, FlowSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (peer.InstallationId == Guid.Empty || signal.SenderInstallationId == Guid.Empty
            || peer.InstallationId == signal.SenderInstallationId)
            throw new ArgumentException("Peer and sender installation IDs must be specified.");
        if (peer.SharedKey is null || peer.SharedKey.Length < 32)
            throw new ArgumentException("Paired Flow key must contain at least 256 bits.", nameof(peer));
        var envelope = FlowSignalCodec.Encode(signal, peer.SharedKey);
        using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(peer.Endpoint.AddressFamily);
        await client.ConnectAsync(peer.Endpoint, sendDeadline.Token).ConfigureAwait(false);
        await WriteFrameAsync(client.GetStream(), envelope, sendDeadline.Token).ConfigureAwait(false);
    }

    public async Task ListenAsync(IPEndPoint localEndpoint, IReadOnlyDictionary<Guid, FlowPeer> trustedPeers,
        Action<FlowSignal, FlowPeer> onSignal, Action<string> onLog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(trustedPeers);
        ArgumentNullException.ThrowIfNull(onSignal);
        ArgumentNullException.ThrowIfNull(onLog);
        if (trustedPeers.Count > 32) throw new ArgumentOutOfRangeException(nameof(trustedPeers), "A maximum of 32 paired peers is supported.");

        var listener = new TcpListener(localEndpoint);
        listener.Start(backlog: MaximumConcurrentConnections);
        using var slots = new SemaphoreSlim(MaximumConcurrentConnections, MaximumConcurrentConnections);
        var active = new ConcurrentDictionary<long, Task>();
        long nextTaskId = 0;
        onLog($"FLOW ESCUTANDO · {listener.LocalEndpoint} · pares confiáveis={trustedPeers.Count}.");
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                if (!slots.Wait(0))
                {
                    client.Dispose();
                    onLog("FLOW DESCARTADO · limite de conexões concorrentes atingido.");
                    continue;
                }
                var taskId = Interlocked.Increment(ref nextTaskId);
                var task = HandleAsync(client);
                active[taskId] = task;
                _ = task.ContinueWith(completed => { active.TryRemove(taskId, out Task? ignored); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            try { await Task.WhenAll(active.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }

        async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    frameDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var envelope = await ReadFrameAsync(client.GetStream(), frameDeadline.Token).ConfigureAwait(false);
                    var (signal, peer) = Authenticate(envelope, trustedPeers, DateTimeOffset.UtcNow);
                    if (client.Client.RemoteEndPoint is not IPEndPoint remote
                        || !remote.Address.MapToIPv4().Equals(peer.Endpoint.Address.MapToIPv4()))
                        throw new InvalidDataException("Authenticated peer arrived from an IP address different from the configured peer.");
                    if (!Remember(signal))
                    {
                        onLog($"FLOW REPETIDO · {signal.Kind} · transferência {signal.TransferId}.");
                        return;
                    }
                    onSignal(signal, peer);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (OperationCanceledException) { onLog("FLOW DESCARTADO · conexão expirou antes de completar o frame."); }
                catch (Exception exception) when (exception is IOException or SocketException or CryptographicException or InvalidDataException or ArgumentException)
                {
                    onLog($"FLOW DESCARTADO · {exception.Message}");
                }
                finally { slots.Release(); }
            }
        }
    }

    private bool Remember(FlowSignal signal)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in seen.Where(pair => pair.Value < now).Select(pair => pair.Key).ToArray())
            seen.TryRemove(item, out _);
        var key = (signal.SenderInstallationId, signal.TransferId, signal.Kind);
        if (!seen.ContainsKey(key) && seen.Count >= 4096) return false;
        return seen.TryAdd(key, signal.IssuedAt.AddSeconds(30));
    }

    private static (FlowSignal Signal, FlowPeer Peer) Authenticate(byte[] envelope,
        IReadOnlyDictionary<Guid, FlowPeer> peers, DateTimeOffset now)
    {
        foreach (var (id, peer) in peers)
        {
            try
            {
                var signal = FlowSignalCodec.Decode(envelope, peer.SharedKey, now);
                if (signal.SenderInstallationId != id || peer.InstallationId != id)
                    throw new InvalidDataException("Authenticated sender does not match its paired installation ID.");
                return (signal, peer);
            }
            catch (CryptographicException) { }
        }
        throw new CryptographicException("No paired peer authenticated this Flow message.");
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] envelope, CancellationToken token)
    {
        if (envelope.Length is 0 or > MaximumEnvelopeBytes) throw new InvalidDataException("Flow message size is invalid.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, envelope.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(envelope, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is 0 or > MaximumEnvelopeBytes) throw new InvalidDataException("Flow frame size is invalid.");
        var envelope = new byte[length];
        await stream.ReadExactlyAsync(envelope, token).ConfigureAwait(false);
        return envelope;
    }
}
