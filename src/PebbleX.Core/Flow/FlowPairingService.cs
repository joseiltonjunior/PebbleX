using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PebbleX.Core.Flow;

public sealed record FlowDiscoveredPeer(Guid InstallationId, string Name, IPAddress Address,
    int FlowPort, int PairingPort, DateTimeOffset LastSeen);

public sealed record FlowPairingChallenge(Guid PeerInstallationId, string PeerName,
    IPAddress PeerAddress, string VerificationCode);

public sealed record FlowPairingResult(Guid PeerInstallationId, string PeerName,
    string PeerAddress, int PeerFlowPort, string SharedKeyHex);

/// <summary>Local-network discovery and mutually approved ECDH pairing for PebbleX installations.</summary>
public sealed class FlowPairingService
{
    public const int DiscoveryPort = 47630;
    // Reuse the existing Flow port. Pairing and Flow are enabled in mutually exclusive UI modes.
    public const int PairingPort = 47631;
    private const int MaximumPacketBytes = 2048;
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PairingDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitialHelloDeadline = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task DiscoverAsync(Guid localInstallationId, string localName, int flowPort,
        Action<IReadOnlyList<FlowDiscoveredPeer>> onPeersChanged, CancellationToken cancellationToken)
    {
        if (localInstallationId == Guid.Empty) throw new ArgumentException("Local installation ID is required.", nameof(localInstallationId));
        ValidateName(localName);
        ValidatePort(flowPort);
        ArgumentNullException.ThrowIfNull(onPeersChanged);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        var peers = new ConcurrentDictionary<Guid, FlowDiscoveredPeer>();
        var receive = ReceiveBeaconsAsync();
        var beacon = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket(ProtocolVersion, localInstallationId,
            localName, flowPort, PairingPort), JsonOptions);
        if (beacon.Length > MaximumPacketBytes) throw new InvalidDataException("Discovery beacon is too large.");

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await udp.SendAsync(beacon, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                foreach (var stale in peers.Where(item => now - item.Value.LastSeen > PeerExpiry).Select(item => item.Key).ToArray())
                    peers.TryRemove(stale, out _);
                onPeersChanged(peers.Values.OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).ToArray());
                await Task.Delay(BeaconInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            udp.Dispose();
            try { await receive.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        async Task ReceiveBeaconsAsync()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) when (cancellationToken.IsCancellationRequested) { return; }

                if (received.Buffer.Length is 0 or > MaximumPacketBytes || !IsPrivateIpv4(received.RemoteEndPoint.Address)) continue;
                try
                {
                    var packet = JsonSerializer.Deserialize<DiscoveryPacket>(received.Buffer, JsonOptions);
                    if (packet is null || packet.Version != ProtocolVersion || packet.InstallationId == Guid.Empty
                        || packet.InstallationId == localInstallationId || packet.PairingPort != PairingPort)
                        continue;
                    ValidateName(packet.Name);
                    ValidatePort(packet.FlowPort);
                    peers[packet.InstallationId] = new FlowDiscoveredPeer(packet.InstallationId, packet.Name,
                        received.RemoteEndPoint.Address, packet.FlowPort, packet.PairingPort, DateTimeOffset.UtcNow);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException or FormatException)
                {
                    // Discovery packets are unauthenticated hints; malformed advertisements are ignored.
                }
            }
        }
    }

    public async Task ListenForPairingAsync(Guid localInstallationId, string localName, int flowPort,
        Func<FlowPairingChallenge, Task<bool>> approveRequest,
        Action<FlowPairingResult> onPaired, Action<string> onLog, CancellationToken cancellationToken,
        int pairingPort = PairingPort)
    {
        if (localInstallationId == Guid.Empty) throw new ArgumentException("Local installation ID is required.", nameof(localInstallationId));
        ValidateName(localName);
        ValidatePort(flowPort);
        ValidatePort(pairingPort);
        ArgumentNullException.ThrowIfNull(approveRequest);
        ArgumentNullException.ThrowIfNull(onPaired);
        ArgumentNullException.ThrowIfNull(onLog);

        var listener = new TcpListener(IPAddress.Any, pairingPort);
        listener.Start(backlog: 4);
        onLog($"DESCOBERTA ATIVA · UDP {DiscoveryPort}; pareamento TCP {pairingPort}.");
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                try
                {
                    using (client)
                    {
                        var result = await HandlePairingAsync(client, localInstallationId, localName, flowPort,
                            approveRequest, cancellationToken).ConfigureAwait(false);
                        if (result is not null)
                        {
                            onPaired(result);
                            onLog($"VÍNCULO PAREADO · peer={result.PeerName} · {result.PeerAddress}:{result.PeerFlowPort}.");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException
                    or CryptographicException or ArgumentException or FormatException or OperationCanceledException)
                {
                    onLog($"PEDIDO DE VÍNCULO ENCERRADO · {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }

    public async Task<FlowPairingResult> PairAsync(Guid localInstallationId, string localName,
        FlowDiscoveredPeer peer, Func<FlowPairingChallenge, Task<bool>> approvePeer,
        CancellationToken cancellationToken)
    {
        if (localInstallationId == Guid.Empty || peer.InstallationId == Guid.Empty || localInstallationId == peer.InstallationId)
            throw new ArgumentException("Local and remote installation IDs must be distinct and non-empty.");
        ValidateName(localName);
        ValidateName(peer.Name);
        ValidatePort(peer.PairingPort);
        ValidatePort(peer.FlowPort);
        ArgumentNullException.ThrowIfNull(approvePeer);

        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try { await client.ConnectAsync(peer.Address, peer.PairingPort, connectDeadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{peer.Name} não aceitou conexão de pareamento em {peer.Address}:{peer.PairingPort}.");
        }
        client.NoDelay = true;
        var stream = client.GetStream();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PairingDeadline);
        using var localKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localNonce = RandomNumberGenerator.GetBytes(32);
        var localPublicKey = localKey.ExportSubjectPublicKeyInfo();
        await WritePacketAsync(stream, new PairingPacket("hello", ProtocolVersion, localInstallationId,
            localName, Convert.ToBase64String(localPublicKey), Convert.ToBase64String(localNonce), peer.FlowPort, false), deadline.Token).ConfigureAwait(false);

        var remote = await ReadPacketAsync(stream, deadline.Token).ConfigureAwait(false);
        ValidateHello(remote, localInstallationId);
        if (remote.InstallationId != peer.InstallationId)
            throw new InvalidDataException("The discovered peer ID does not match the pairing responder.");
        var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        if (!remoteAddress.Equals(peer.Address)) throw new InvalidDataException("Pairing peer address changed during the handshake.");
        var derived = DerivePairingKey(localKey, remote, localInstallationId, localName,
            Convert.ToBase64String(localPublicKey), Convert.ToBase64String(localNonce), peer.InstallationId,
            remote.Name, remote.PublicKey, remote.Nonce, PairingPort, remote.FlowPort);
        try
        {
            var challenge = new FlowPairingChallenge(peer.InstallationId, remote.Name, peer.Address, derived.VerificationCode);
            var accepted = await approvePeer(challenge).ConfigureAwait(false);
            await WritePacketAsync(stream, ConsentPacket(accepted, derived.SharedKey, localInstallationId,
                peer.InstallationId), deadline.Token).ConfigureAwait(false);
            var decision = await ReadPacketAsync(stream, deadline.Token).ConfigureAwait(false);
            if (decision.Kind != "decision" || decision.Version != ProtocolVersion
                || !VerifyDecision(decision, derived.SharedKey, peer.InstallationId, localInstallationId))
                throw new CryptographicException("Pairing decision authentication failed.");
            if (!decision.Accepted)
                throw new InvalidOperationException("O outro computador recusou o vínculo ou o código não coincidiu.");
            return new FlowPairingResult(peer.InstallationId, remote.Name, peer.Address.ToString(), remote.FlowPort,
                Convert.ToHexString(derived.SharedKey));
        }
        finally { CryptographicOperations.ZeroMemory(derived.SharedKey); }
    }

    private static async Task<FlowPairingResult?> HandlePairingAsync(TcpClient client, Guid localId, string localName,
        int localFlowPort, Func<FlowPairingChallenge, Task<bool>> approveRequest, CancellationToken token)
    {
        var stream = client.GetStream();
        using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        helloDeadline.CancelAfter(InitialHelloDeadline);
        var remote = await ReadPacketAsync(stream, helloDeadline.Token).ConfigureAwait(false);
        ValidateHello(remote, localId);
        if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || !IsPrivateIpv4(endpoint.Address))
            throw new InvalidDataException("Pairing is accepted only from a private IPv4 address.");

        using var localKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localNonce = RandomNumberGenerator.GetBytes(32);
        var localPublicKey = localKey.ExportSubjectPublicKeyInfo();
        using var pairingDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        pairingDeadline.CancelAfter(PairingDeadline);
        await WritePacketAsync(stream, new PairingPacket("hello", ProtocolVersion, localId, localName,
            Convert.ToBase64String(localPublicKey), Convert.ToBase64String(localNonce), localFlowPort, false), pairingDeadline.Token).ConfigureAwait(false);

        var derived = DerivePairingKey(localKey, remote, remote.InstallationId, remote.Name,
            remote.PublicKey, remote.Nonce, localId, localName, Convert.ToBase64String(localPublicKey),
            Convert.ToBase64String(localNonce), remote.FlowPort, localFlowPort);
        try
        {
            var challenge = new FlowPairingChallenge(remote.InstallationId, remote.Name, endpoint.Address, derived.VerificationCode);
            var localAccepted = await approveRequest(challenge).ConfigureAwait(false);
            var consent = await ReadPacketAsync(stream, pairingDeadline.Token).ConfigureAwait(false);
            if (consent.Kind != "consent" || consent.Version != ProtocolVersion
                || !VerifyConsent(consent, derived.SharedKey, remote.InstallationId, localId))
                throw new CryptographicException("Pairing consent authentication failed.");
            var accepted = localAccepted && consent.Accepted;
            await WritePacketAsync(stream, new PairingPacket("decision", ProtocolVersion, Guid.Empty,
                string.Empty, string.Empty, string.Empty, 0, accepted,
                CreateDecisionAuthenticator(derived.SharedKey, localId, remote.InstallationId, accepted)), pairingDeadline.Token).ConfigureAwait(false);
            if (!accepted) return null;
            return new FlowPairingResult(remote.InstallationId, remote.Name, endpoint.Address.ToString(),
                remote.FlowPort, Convert.ToHexString(derived.SharedKey));
        }
        finally { CryptographicOperations.ZeroMemory(derived.SharedKey); }
    }

    private static (byte[] SharedKey, string VerificationCode) DerivePairingKey(ECDiffieHellman localKey,
        PairingPacket remoteHello, Guid initiatorId, string initiatorName, string initiatorPublicKey,
        string initiatorNonce, Guid responderId, string responderName, string responderPublicKey,
        string responderNonce, int initiatorFlowPort, int responderFlowPort)
    {
        using var remoteKey = ECDiffieHellman.Create();
        var publicBytes = Convert.FromBase64String(remoteHello.PublicKey);
        remoteKey.ImportSubjectPublicKeyInfo(publicBytes, out var bytesRead);
        if (bytesRead != publicBytes.Length) throw new InvalidDataException("Pairing public key has trailing data.");
        var sharedSecret = localKey.DeriveKeyMaterial(remoteKey.PublicKey);
        try
        {
            var transcript = JsonSerializer.SerializeToUtf8Bytes(new PairingTranscript(ProtocolVersion,
                initiatorId, initiatorName, initiatorPublicKey, initiatorNonce,
                responderId, responderName, responderPublicKey, responderNonce,
                initiatorFlowPort, responderFlowPort), JsonOptions);
            var salt = SHA256.HashData(transcript);
            var info = Encoding.UTF8.GetBytes("PebbleX local pairing v1");
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, info);
            var codeDigest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("PebbleX verification code v1"));
            var codeValue = BinaryPrimitives.ReadUInt32BigEndian(codeDigest.AsSpan(0, 4)) % 100_000_000;
            return (key, codeValue.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { CryptographicOperations.ZeroMemory(sharedSecret); }
    }

    private static void ValidateHello(PairingPacket packet, Guid localId)
    {
        if (packet.Kind != "hello" || packet.Version != ProtocolVersion || packet.InstallationId == Guid.Empty
            || packet.InstallationId == localId || packet.FlowPort is < 1 or > 65535)
            throw new InvalidDataException("Pairing hello is invalid or incompatible.");
        ValidateName(packet.Name);
        var publicKey = Convert.FromBase64String(packet.PublicKey);
        var nonce = Convert.FromBase64String(packet.Nonce);
        if (publicKey.Length is < 64 or > 256 || nonce.Length != 32)
            throw new InvalidDataException("Pairing cryptographic material has an invalid size.");
    }

    private static PairingPacket ConsentPacket(bool accepted, byte[] key, Guid senderId, Guid receiverId) => new("consent", ProtocolVersion,
        Guid.Empty, string.Empty, string.Empty, string.Empty, 0, accepted,
        CreateConsentAuthenticator(key, senderId, receiverId, accepted));

    private static bool VerifyConsent(PairingPacket packet, byte[] key, Guid senderId, Guid receiverId) =>
        VerifyAuthenticator(packet.Authenticator, key, ConsentMaterial(senderId, receiverId, packet.Accepted));

    private static bool VerifyDecision(PairingPacket packet, byte[] key, Guid senderId, Guid receiverId) =>
        VerifyAuthenticator(packet.Authenticator, key, DecisionMaterial(senderId, receiverId, packet.Accepted));

    private static string CreateConsentAuthenticator(byte[] key, Guid senderId, Guid receiverId, bool accepted) =>
        CreateAuthenticator(key, ConsentMaterial(senderId, receiverId, accepted));

    private static string CreateDecisionAuthenticator(byte[] key, Guid senderId, Guid receiverId, bool accepted) =>
        CreateAuthenticator(key, DecisionMaterial(senderId, receiverId, accepted));

    private static string ConsentMaterial(Guid senderId, Guid receiverId, bool accepted) =>
        $"PebbleX pairing consent v1:{senderId:N}:{receiverId:N}:{(accepted ? 1 : 0)}";

    private static string DecisionMaterial(Guid senderId, Guid receiverId, bool accepted) =>
        $"PebbleX pairing decision v1:{senderId:N}:{receiverId:N}:{(accepted ? 1 : 0)}";

    private static string CreateAuthenticator(byte[] key, string material) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(material)));

    private static bool VerifyAuthenticator(string supplied, byte[] key, string material)
    {
        try
        {
            var actual = Convert.FromBase64String(supplied);
            var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(material));
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static async Task WritePacketAsync(Stream stream, PairingPacket packet, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
        if (payload.Length is 0 or > MaximumPacketBytes) throw new InvalidDataException("Pairing message is too large.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<PairingPacket> ReadPacketAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is 0 or > MaximumPacketBytes) throw new InvalidDataException("Pairing message size is invalid.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PairingPacket>(payload, JsonOptions)
            ?? throw new InvalidDataException("Pairing message is empty.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("Computer name must contain 1 to 80 printable characters.");
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new InvalidDataException("Port is outside TCP/UDP range.");
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) || bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private sealed record DiscoveryPacket(int Version, Guid InstallationId, string Name, int FlowPort, int PairingPort);
    private sealed record PairingPacket(string Kind, int Version, Guid InstallationId, string Name,
        string PublicKey, string Nonce, int FlowPort, bool Accepted, string Authenticator = "");
    private sealed record PairingTranscript(int Version, Guid InitiatorId, string InitiatorName,
        string InitiatorPublicKey, string InitiatorNonce, Guid ResponderId, string ResponderName,
        string ResponderPublicKey, string ResponderNonce, int InitiatorFlowPort, int ResponderFlowPort);
}
