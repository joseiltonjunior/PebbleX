using System.ComponentModel;
using PebbleX.Windows.Hid;

namespace PebbleX.Windows.Tests;

public sealed class HidDiagnosticSessionTests
{
    [Fact]
    public async Task ReconnectsOnlyToSelectedPathAndCancelsTheReaderOnStop()
    {
        var selected = Collection("selected");
        HidCollectionInfo[] snapshot = [selected];
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var absent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var activeReaders = 0;
        var session = new HidDiagnosticSession(() => Volatile.Read(ref snapshot), async (item, _, token) =>
        {
            Assert.Equal(selected.DevicePath, item.DevicePath);
            Assert.Equal(1, Interlocked.Increment(ref activeReaders));
            if (Interlocked.Increment(ref calls) == 1) firstRead.SetResult();
            else secondRead.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Decrement(ref activeReaders); }
        }, TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = session.RunAsync(selected, _ => { }, message =>
        {
            if (message.Contains("DESCONECTADO", StringComparison.Ordinal)) absent.TrySetResult();
        }, _ => { }, cancellation.Token);

        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Another device with identical identifiers must not replace the selected interface.
        Volatile.Write(ref snapshot, [Collection("another-device")]);
        await absent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref calls));
        Volatile.Write(ref snapshot, [selected]);
        await secondRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, Volatile.Read(ref activeReaders));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task EnumerationFailureReleasesTheActiveReader()
    {
        var selected = Collection("selected");
        var enumerations = 0;
        var released = false;
        var session = new HidDiagnosticSession(
            () => ++enumerations == 1 ? [selected] : throw new Win32Exception("test enumeration failure"),
            async (_, _, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { released = true; }
            }, TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<Win32Exception>(() => session.RunAsync(selected, _ => { }, _ => { }, _ => { }, cancellation.Token));
        Assert.True(released);
    }

    [Theory]
    [InlineData(0x046D, 0x0001)]
    [InlineData(0x1234, 0xFF43)]
    [InlineData(0x046D, 0xFF0C)]
    public async Task RejectsNonDiagnosticCollectionsBeforeOpening(int vendor, int usagePage)
    {
        var selected = Collection("blocked") with { VendorId = (ushort)vendor, UsagePage = (ushort)usagePage };
        var session = new HidDiagnosticSession(() => throw new InvalidOperationException("Must not enumerate"),
            (_, _, _) => throw new InvalidOperationException("Must not open"), TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<ArgumentException>(() => session.RunAsync(selected, _ => { }, _ => { }, _ => { }, CancellationToken.None));
    }

    private static HidCollectionInfo Collection(string path) =>
        new(path, 0x046D, 0xB377, 0xFF43, 0x0202, 20, null, null, null, null, null, null);
}
