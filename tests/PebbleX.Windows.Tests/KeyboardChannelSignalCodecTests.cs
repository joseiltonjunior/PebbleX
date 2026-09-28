using System.Security.Cryptography;
using PebbleX.Core.Flow;

namespace PebbleX.Windows.Tests;

public sealed class KeyboardChannelSignalCodecTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void AuthenticatedSignalRoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var signal = new FlowSignal(Guid.NewGuid(), Guid.NewGuid(), FlowSignalKind.KeyboardChannelObserved, 2, now);

        var encoded = FlowSignalCodec.Encode(signal, Key);
        var decoded = FlowSignalCodec.Decode(encoded, Key, now.AddSeconds(1));

        Assert.Equal(signal, decoded);
    }

    [Fact]
    public void RejectsSignalSignedByAnotherPeerKey()
    {
        var now = DateTimeOffset.UtcNow;
        var encoded = FlowSignalCodec.Encode(
            new FlowSignal(Guid.NewGuid(), Guid.NewGuid(), FlowSignalKind.KeyboardChannelObserved, 1, now), Key);

        Assert.Throws<CryptographicException>(() =>
            FlowSignalCodec.Decode(encoded, RandomNumberGenerator.GetBytes(32), now));
    }

    [Fact]
    public void RejectsExpiredSignal()
    {
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var encoded = FlowSignalCodec.Encode(
            new FlowSignal(Guid.NewGuid(), Guid.NewGuid(), FlowSignalKind.KeyboardChannelObserved, 3, issuedAt), Key);

        Assert.Throws<InvalidDataException>(() =>
            FlowSignalCodec.Decode(encoded, Key, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void RejectsChannelsOutsideHardwareRange(int channel)
    {
        var signal = new FlowSignal(Guid.NewGuid(), Guid.NewGuid(), FlowSignalKind.KeyboardChannelObserved, channel, DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => FlowSignalCodec.Encode(signal, Key));
    }

    [Fact]
    public void RequiresAtLeast256BitPairKey()
    {
        Assert.Throws<ArgumentException>(() => FlowSignalCodec.Encode(
            new FlowSignal(Guid.NewGuid(), Guid.NewGuid(), FlowSignalKind.KeyboardChannelObserved, 1, DateTimeOffset.UtcNow), new byte[16]));
    }

    [Fact]
    public void SupportsMouseConfirmationSignal()
    {
        var now = DateTimeOffset.UtcNow;
        var signal = new FlowSignal(Guid.NewGuid(), Guid.NewGuid(), FlowSignalKind.MouseChannelConfirmed, 2, now);

        Assert.Equal(signal, FlowSignalCodec.Decode(FlowSignalCodec.Encode(signal, Key), Key, now));
    }
}
