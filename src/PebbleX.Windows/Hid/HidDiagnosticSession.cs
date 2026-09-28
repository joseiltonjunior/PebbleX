using System.ComponentModel;

namespace PebbleX.Windows.Hid;

public sealed class HidDiagnosticSession
{
    private readonly Func<IReadOnlyList<HidCollectionInfo>> enumerate;
    private readonly Func<HidCollectionInfo, Action<HidInputReport>, CancellationToken, Task> monitor;
    private readonly TimeSpan pollInterval;

    public HidDiagnosticSession()
        : this(new HidCollectionEnumerator().Enumerate, new HidInputMonitor().MonitorAsync, TimeSpan.FromSeconds(1))
    {
    }

    internal HidDiagnosticSession(
        Func<IReadOnlyList<HidCollectionInfo>> enumerate,
        Func<HidCollectionInfo, Action<HidInputReport>, CancellationToken, Task> monitor,
        TimeSpan pollInterval)
    {
        this.enumerate = enumerate;
        this.monitor = monitor;
        this.pollInterval = pollInterval;
    }

    // Reopen only the selected interface path; never substitute another device with the same VID/PID.
    public async Task RunAsync(HidCollectionInfo selected, Action<string> onStatus,
        Action<string> onLog, Action<HidInputReport> onReport, CancellationToken cancellationToken)
    {
        if (selected.VendorId != 0x046D || selected.UsagePage != 0xFF43 || selected.Usage != 0x0202
            || selected.InputReportByteLength is null or 0)
        {
            throw new ArgumentException("Selecione uma collection Logitech de diagnóstico com relatórios de entrada.", nameof(selected));
        }

        Task? readTask = null;
        CancellationTokenSource? readCancellation = null;
        bool? wasPresent = null;
        string? lastStatus = null;
        string? lastAlternatePaths = null;
        var retryAt = DateTimeOffset.MinValue;

        onLog($"Origem selecionada · {FormatIdentity(selected)}");

        void Status(string value)
        {
            if (lastStatus == value) return;
            lastStatus = value;
            onStatus(value);
        }

        async Task FinishReadAsync()
        {
            if (readTask is null) return;
            try { await readTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (readCancellation!.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                onLog($"Leitura interrompida: {exception.Message}");
            }
            finally
            {
                readTask = null;
                readCancellation?.Dispose();
                readCancellation = null;
            }
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var collections = enumerate();
                var current = collections.FirstOrDefault(item =>
                    string.Equals(item.DevicePath, selected.DevicePath, StringComparison.OrdinalIgnoreCase));
                var present = current is not null;
                if (present != wasPresent)
                {
                    onLog(present
                        ? wasPresent == false
                            ? $"RETORNO · interface no caminho de origem · {FormatIdentity(current!)}"
                            : $"CONECTADO · interface de origem · {FormatIdentity(current!)}"
                        : $"DESCONECTADO · caminho de origem · {FormatIdentity(selected)}");
                    wasPresent = present;
                }

                string[] alternatePaths = present ? [] : collections
                    .Where(item => item.VendorId == selected.VendorId && item.ProductId == selected.ProductId
                        && item.UsagePage == selected.UsagePage && item.Usage == selected.Usage
                        && item.InputReportByteLength is > 0
                        && !string.Equals(item.DevicePath, selected.DevicePath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.DevicePath, StringComparer.OrdinalIgnoreCase)
                    .Select(FormatIdentity)
                    .ToArray();
                var alternateSignature = string.Join(" | ", alternatePaths);
                if (alternateSignature.Length > 0 && alternateSignature != lastAlternatePaths)
                    onLog($"OUTRO CAMINHO HID · não associado automaticamente · origem=[{FormatIdentity(selected)}] · candidato=[{alternateSignature}]");
                lastAlternatePaths = alternateSignature;

                if (!present)
                {
                    Status("Aguardando reconexão");
                    readCancellation?.Cancel();
                    await FinishReadAsync().ConfigureAwait(false);
                }
                else
                {
                    if (readTask?.IsCompleted == true)
                    {
                        await FinishReadAsync().ConfigureAwait(false);
                        Status("Leitura interrompida · tentando novamente");
                        retryAt = DateTimeOffset.UtcNow.AddSeconds(3);
                    }
                    if (readTask is null && DateTimeOffset.UtcNow >= retryAt)
                    {
                        readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        onLog("Abrindo a collection selecionada para leitura.");
                        readTask = monitor(current!, onReport, readCancellation.Token);
                        Status("Capturando");
                    }
                }

                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            readCancellation?.Cancel();
            await FinishReadAsync().ConfigureAwait(false);
        }
    }

    private static string FormatIdentity(HidCollectionInfo device) =>
        $"path=[{device.DevicePath}] · instance=[{device.DeviceInstanceId ?? "?"}] · parent=[{device.ParentDeviceInstanceId ?? "?"}] · container=[{device.DeviceContainerId?.ToString("D") ?? "?"}]";
}
