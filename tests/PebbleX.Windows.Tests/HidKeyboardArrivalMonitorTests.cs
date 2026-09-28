using PebbleX.Windows.Hid;

namespace PebbleX.Windows.Tests;

public sealed class HidKeyboardArrivalMonitorTests
{
    [Fact]
    public async Task EmitsOnlyAfterKeyboardArrivalAndConfirmedChannel()
    {
        var keyboard = new HidCollectionInfo("keyboard-path", 0x046D, 0xB377, 0xFF43, 0x0202,
            20, "Logitech", "K380", null, null, null, Guid.NewGuid());
        var mouse = keyboard with { DevicePath = "mouse-path", ProductId = 0xB034 };
        var sample = 0;
        var enumerations = new Func<CancellationToken, Task<IReadOnlyList<HidCollectionInfo>>>(_ =>
            Task.FromResult<IReadOnlyList<HidCollectionInfo>>(Interlocked.Increment(ref sample) == 1 ? [mouse] : [mouse, keyboard]));
        var queries = 0;
        var monitor = new HidKeyboardArrivalMonitor(enumerations, (_, _) =>
        {
            Interlocked.Increment(ref queries);
            return Task.FromResult(new HidHostInfo(3, 2, 0x0A, DateTimeOffset.UtcNow));
        }, TimeSpan.FromMilliseconds(1));
        using var cancellation = new CancellationTokenSource();
        HidHostInfo? confirmed = null;
        HidCollectionInfo? arrived = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunAsync((device, channel) =>
        {
            arrived = device;
            confirmed = channel;
            cancellation.Cancel();
        }, _ => { }, cancellation.Token));

        Assert.Equal(keyboard.DevicePath, arrived?.DevicePath);
        Assert.Equal(2, confirmed?.Channel);
        Assert.Equal(1, queries);
    }
}
