namespace PebbleX.Windows.Hid;

/// <summary>Reports a keyboard channel only after a new local HID interface has appeared and answered ChangeHost.</summary>
public sealed class HidKeyboardArrivalMonitor
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<HidCollectionInfo>>> enumerate;
    private readonly Func<HidCollectionInfo, CancellationToken, Task<HidHostInfo>> queryChannel;
    private readonly TimeSpan pollInterval;

    public HidKeyboardArrivalMonitor()
        : this(token => Task.Run<IReadOnlyList<HidCollectionInfo>>(() => new HidCollectionEnumerator().Enumerate(), token),
            (collection, token) => Task.Run(() => new HidHostQuery().QueryAsync(collection, _ => { }, token), token),
            TimeSpan.FromSeconds(1))
    {
    }

    internal HidKeyboardArrivalMonitor(
        Func<CancellationToken, Task<IReadOnlyList<HidCollectionInfo>>> enumerate,
        Func<HidCollectionInfo, CancellationToken, Task<HidHostInfo>> queryChannel,
        TimeSpan pollInterval)
    {
        this.enumerate = enumerate;
        this.queryChannel = queryChannel;
        this.pollInterval = pollInterval;
    }

    public async Task RunAsync(Action<HidCollectionInfo, HidHostInfo> onConfirmedArrival,
        Action<string> onLog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onConfirmedArrival);
        ArgumentNullException.ThrowIfNull(onLog);
        HashSet<string>? present = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collections = await enumerate(cancellationToken).ConfigureAwait(false);
            var keyboards = collections.Where(IsSupportedKeyboard)
                .ToDictionary(collection => collection.DevicePath, StringComparer.OrdinalIgnoreCase);

            if (present is null)
            {
                present = keyboards.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                onLog($"BASELINE TECLADO · {present.Count} interface(s) presente(s); sem sinal de troca emitido.");
            }
            else
            {
                foreach (var removed in present.Except(keyboards.Keys, StringComparer.OrdinalIgnoreCase))
                    onLog($"TECLADO SAIU · interface {removed}.");

                foreach (var path in keyboards.Keys.Except(present, StringComparer.OrdinalIgnoreCase))
                {
                    var collection = keyboards[path];
                    onLog("TECLADO CHEGOU · consultando ChangeHost antes de emitir sinal.");
                    try
                    {
                        var channel = await queryChannel(collection, cancellationToken).ConfigureAwait(false);
                        onLog($"CANAL DO TECLADO CONFIRMADO · {channel.Channel} de {channel.HostCount}.");
                        onConfirmedArrival(collection, channel);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        onLog($"CANAL DO TECLADO NÃO CONFIRMADO · sinal não emitido · {exception.Message}");
                    }
                }

                present = keyboards.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSupportedKeyboard(HidCollectionInfo item) => item.VendorId == 0x046D
        && item.ProductId == 0xB377 && item.UsagePage == 0xFF43 && item.Usage == 0x0202
        && item.InputReportByteLength == 20;
}
