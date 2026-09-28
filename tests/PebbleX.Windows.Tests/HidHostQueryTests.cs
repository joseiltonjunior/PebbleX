using PebbleX.Windows.Hid;

namespace PebbleX.Windows.Tests;

public sealed class HidHostQueryTests
{
    [Fact]
    public void AnalyticsCapabilityComesFromTheHighFlagByte()
    {
        var report = new byte[20];
        report[4] = 0;
        report[5] = 0xD1;
        report[8] = 0x04;
        var nonstandard = HidHostQueryProtocol.ParseControlInfo(report);
        Assert.Equal(0xD1, nonstandard.ControlId);
        Assert.False(nonstandard.HasAnalyticsEvent);
        Assert.False(nonstandard.CanDivert);
        report[12] = 0x04;
        report[8] = 0x20;
        var capable = HidHostQueryProtocol.ParseControlInfo(report);
        Assert.True(capable.HasAnalyticsEvent);
        Assert.True(capable.CanDivert);
    }

    [Fact]
    public async Task ResolvesDeviceSpecificFeatureAndIgnoresOtherApplicationsAndEvents()
    {
        using var stream = new QueryStream();
        var result = await new HidHostQueryProtocol(stream, _ => { }).QueryAsync(CancellationToken.None);
        Assert.Equal(3, result.HostCount);
        Assert.Equal(2, result.Channel);
        Assert.Equal(0x23, result.FeatureIndex);
        Assert.Equal(2, stream.Requests.Count);
        Assert.Equal(new byte[] { 0x18, 0x14 }, stream.Requests[0][4..6]);
        Assert.Equal(0x23, stream.Requests[1][2]);
        Assert.All(stream.Requests, request => Assert.Equal(0, request[3] >> 4));
    }

    [Theory]
    [InlineData(1, 0x00)]
    [InlineData(2, 0x01)]
    [InlineData(3, 0x02)]
    public void BuildsSetCurrentHostAsZeroBasedNoResponseRequest(int channel, byte hostIndex)
    {
        var request = HidHostQueryProtocol.CreateSetHostReport(0x0A, channel);
        Assert.Equal(20, request.Length);
        Assert.Equal(new byte[] { 0x11, 0xFF, 0x0A, 0x1D, hostIndex }, request[..5]);
        Assert.All(request[5..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void RejectsSetCurrentHostOutsidePhysicalChannels(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HidHostQueryProtocol.CreateSetHostReport(0x0A, channel));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(255, 0)]
    public void RejectsInvalidHostNumbers(int count, int index)
    {
        var report = new byte[20];
        report[4] = (byte)count;
        report[5] = (byte)index;
        Assert.Throws<IOException>(() => HidHostQueryProtocol.ParseHostInfo(report, 0x23));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void ConvertsZeroBasedHostToPhysicalChannel(int index, int expected)
    {
        var report = new byte[20];
        report[4] = 3;
        report[5] = (byte)index;
        Assert.Equal(expected, HidHostQueryProtocol.ParseHostInfo(report, 10).Channel);
    }

    [Fact]
    public void CorrelatesErrorsAndRejectsIncompleteReports()
    {
        byte[] request = [0x11, 0xFF, 0x23, 0x0D, .. new byte[16]];
        byte[] error = [0x11, 0xFF, 0xFF, 0x23, 0x0D, 0x02, .. new byte[14]];
        Assert.True(HidHostQueryProtocol.Matches(error, request));
        error[4] = 0x0C;
        Assert.False(HidHostQueryProtocol.Matches(error, request));
        Assert.False(HidHostQueryProtocol.Matches(request[..6], request));
    }

    [Fact]
    public async Task CancellationStopsWaitingForTheDevice()
    {
        using var stream = new QueryStream { Silent = true };
        using var source = new CancellationTokenSource();
        var query = new HidHostQueryProtocol(stream, _ => { }).QueryAsync(source.Token);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Single(stream.Requests);
    }

    [Fact]
    public async Task TimeoutDoesNotInventAChannelOrSendAnotherRequest()
    {
        using var stream = new QueryStream { Silent = true };
        await Assert.ThrowsAsync<TimeoutException>(() => new HidHostQueryProtocol(stream, _ => { }).QueryAsync(CancellationToken.None));
        Assert.Single(stream.Requests);
    }

    [Fact]
    public async Task UnsupportedFeatureDoesNotSendHostQuery()
    {
        using var stream = new QueryStream { Feature = 0 };
        await Assert.ThrowsAsync<NotSupportedException>(() => new HidHostQueryProtocol(stream, _ => { }).QueryAsync(CancellationToken.None));
        Assert.Single(stream.Requests);
    }

    [Fact]
    public async Task RejectsUnidentifiedDeviceBeforeOpening()
    {
        var collection = new HidCollectionInfo("must-not-open", 0x1234, 0xB377, 0xFF43, 0x0202, 20, null, null, null, null, null, null);
        await Assert.ThrowsAsync<ArgumentException>(() => new HidHostQuery().QueryAsync(collection, _ => { }, CancellationToken.None));
    }

    private sealed class QueryStream : Stream
    {
        private readonly Queue<byte[]> responses = new();
        internal List<byte[]> Requests { get; } = [];
        internal bool Silent { get; init; }
        internal byte Feature { get; init; } = 0x23;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = buffer.ToArray();
            Requests.Add(request);
            var response = (byte[])request.Clone();
            response[4] = request[2] == 0 ? Feature : (byte)3;
            response[5] = request[2] == 0 ? (byte)0 : (byte)1;
            var foreign = (byte[])response.Clone();
            foreign[3] = (byte)((request[3] & 0xF0) | 0x0C);
            responses.Enqueue(foreign);
            var notification = (byte[])response.Clone();
            notification[3] = 0;
            responses.Enqueue(notification);
            responses.Enqueue(response);
            return ValueTask.CompletedTask;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Silent) await Task.Delay(Timeout.Infinite, cancellationToken);
            var response = responses.Dequeue();
            response.CopyTo(buffer);
            return response.Length;
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
