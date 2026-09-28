using System.Security.Cryptography;
using System.Text.Json;

namespace PebbleX.Core.Flow;

public enum FlowSignalKind
{
    KeyboardChannelObserved,
    MouseChannelConfirmed
}

/// <summary>A short-lived authenticated observation exchanged by paired PebbleX installations.</summary>
public sealed record FlowSignal(Guid SenderInstallationId, Guid TransferId, FlowSignalKind Kind,
    int Channel, DateTimeOffset IssuedAt);

/// <summary>
/// Encodes narrow Flow signals. The shared key is provisioned by the pairing flow;
/// messages never carry HID paths, raw reports, keystrokes, or pointer data.
/// </summary>
public static class FlowSignalCodec
{
    private const int ProtocolVersion = 1;
    private const int MaximumEnvelopeBytes = 4096;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Encode(FlowSignal signal, ReadOnlySpan<byte> sharedKey)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ValidateKey(sharedKey);
        ValidateSignal(signal);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new WirePayload(
            ProtocolVersion, signal.Kind.ToString(), signal.SenderInstallationId,
            signal.TransferId, signal.Channel, signal.IssuedAt.ToUniversalTime()), JsonOptions);
        var mac = HMACSHA256.HashData(sharedKey, payload);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new WireEnvelope(
            Convert.ToBase64String(payload), Convert.ToBase64String(mac)), JsonOptions);
        if (envelope.Length > MaximumEnvelopeBytes) throw new InvalidOperationException("Flow message exceeds its size limit.");
        return envelope;
    }

    public static FlowSignal Decode(ReadOnlySpan<byte> envelopeBytes, ReadOnlySpan<byte> sharedKey, DateTimeOffset now)
    {
        ValidateKey(sharedKey);
        if (envelopeBytes.Length is 0 or > MaximumEnvelopeBytes)
            throw new InvalidDataException("Flow message size is invalid.");

        WireEnvelope envelope;
        try { envelope = JsonSerializer.Deserialize<WireEnvelope>(envelopeBytes, JsonOptions) ?? throw new InvalidDataException("Flow envelope is empty."); }
        catch (JsonException exception) { throw new InvalidDataException("Flow envelope is malformed.", exception); }

        byte[] payload;
        byte[] receivedMac;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            receivedMac = Convert.FromBase64String(envelope.Mac);
        }
        catch (FormatException exception) { throw new InvalidDataException("Flow envelope encoding is malformed.", exception); }
        var expectedMac = HMACSHA256.HashData(sharedKey, payload);
        if (receivedMac.Length != expectedMac.Length || !CryptographicOperations.FixedTimeEquals(receivedMac, expectedMac))
            throw new CryptographicException("Flow message authentication failed.");

        WirePayload body;
        try { body = JsonSerializer.Deserialize<WirePayload>(payload, JsonOptions) ?? throw new InvalidDataException("Flow payload is empty."); }
        catch (JsonException exception) { throw new InvalidDataException("Flow payload is malformed.", exception); }
        if (body.Version != ProtocolVersion || !Enum.TryParse<FlowSignalKind>(body.Kind, false, out var kind))
            throw new InvalidDataException("Flow message type or protocol version is unsupported.");

        var signal = new FlowSignal(body.SenderInstallationId, body.TransferId, kind, body.Channel, body.IssuedAt);
        ValidateSignal(signal);
        var age = now - signal.IssuedAt;
        if (age > MaximumAge || age < -MaximumFutureSkew)
            throw new InvalidDataException("Flow signal is expired or dated in the future.");
        return signal;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32) throw new ArgumentException("A paired Flow key must contain at least 256 bits.", nameof(key));
    }

    private static void ValidateSignal(FlowSignal signal)
    {
        if (signal.SenderInstallationId == Guid.Empty || signal.TransferId == Guid.Empty)
            throw new ArgumentException("Flow installation and transfer IDs must be non-empty.", nameof(signal));
        if (signal.Kind is not (FlowSignalKind.KeyboardChannelObserved or FlowSignalKind.MouseChannelConfirmed))
            throw new ArgumentOutOfRangeException(nameof(signal), "Flow signal kind is not supported.");
        if (signal.Channel is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(signal), "Channel must be 1, 2, or 3.");
    }

    private sealed record WireEnvelope(string Payload, string Mac);
    private sealed record WirePayload(int Version, string Kind, Guid SenderInstallationId,
        Guid TransferId, int Channel, DateTimeOffset IssuedAt);
}
