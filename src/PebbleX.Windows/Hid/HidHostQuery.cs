using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PebbleX.Windows.Hid.Native;

namespace PebbleX.Windows.Hid;

public sealed record HidHostInfo(int HostCount, int Channel, byte FeatureIndex, DateTimeOffset ObservedAt);
public sealed record HidHostSwitchResult(int PreviousChannel, int TargetChannel, int HostCount, DateTimeOffset SentAt);
public sealed record HidControlInfo(ushort ControlId, ushort Flags)
{
    public bool CanDivert => (Flags & 0x20) != 0;
    public bool HasAnalyticsEvent => (Flags & 0x400) != 0;
}

/// <summary>Explicit HID++ diagnostics. Writes are limited to temporary analytics reporting for the keyboard Easy-Switch controls.</summary>
public sealed class HidHostQuery
{
    public Task<HidHostInfo> QueryAsync(HidCollectionInfo collection, Action<string> log, CancellationToken token) =>
        ExecuteAsync(collection, log, protocol => protocol.QueryAsync(token), token);

    public Task<HidHostSwitchResult> SetMouseChannelAsync(HidCollectionInfo collection, int targetChannel, Action<string> log, CancellationToken token)
    {
        if (collection.ProductId != 0xB034)
            throw new ArgumentException("A troca manual está limitada ao mouse Logitech B034.", nameof(collection));
        if (targetChannel is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(targetChannel), "O canal de destino deve ser 1, 2 ou 3.");
        return ExecuteAsync(collection, log, protocol => protocol.SetMouseChannelAsync(targetChannel, token), token);
    }

    public Task<IReadOnlyList<HidControlInfo>> QueryControlsAsync(HidCollectionInfo collection, Action<string> log, CancellationToken token) =>
        ExecuteAsync(collection, log, protocol => protocol.QueryControlsAsync(token), token);

    public Task SetEasySwitchAnalyticsAsync(HidCollectionInfo collection, bool enabled, Action<string> log, CancellationToken token)
    {
        if (collection.ProductId != 0xB377)
            throw new ArgumentException("A configuração de eventos Easy-Switch está limitada ao teclado Logitech B377.", nameof(collection));
        return ExecuteAsync(collection, log, async protocol =>
        {
            await protocol.SetEasySwitchAnalyticsAsync(enabled, token).ConfigureAwait(false);
            return true;
        }, token);
    }

    private static async Task<T> ExecuteAsync<T>(HidCollectionInfo collection, Action<string> log,
        Func<HidHostQueryProtocol, Task<T>> query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(log);
        if (collection.VendorId != 0x046D || collection.ProductId is null
            || collection.UsagePage != 0xFF43 || collection.Usage != 0x0202 || collection.InputReportByteLength != 20)
            throw new ArgumentException("A consulta exige uma interface Logitech FF43/0202 de 20 bytes identificada.", nameof(collection));

        token.ThrowIfCancellationRequested();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(collection.DevicePath.ToUpperInvariant())));
        using var gate = new Semaphore(1, 1, $"Local\\PebbleX.Query.{identity}");
        if (!gate.WaitOne(0)) throw new InvalidOperationException("Já existe uma consulta PebbleX neste dispositivo. Aguarde sua conclusão.");
        try
        {
            using var handle = HidNative.OpenForDiagnosticQueries(collection.DevicePath);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível abrir o dispositivo para consulta.");
            // Revalidate the actual handle before sending anything; the inventory may be stale.
            var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            if (!HidNative.TryGetAttributes(handle, ref attributes) || attributes.VendorId != collection.VendorId
                || attributes.ProductId != collection.ProductId)
                throw new InvalidOperationException("A identidade do dispositivo não pôde ser confirmada.");
            if (!HidNative.TryGetPreparsedData(handle, out var data))
                throw new InvalidOperationException("Descritor HID indisponível.");
            try
            {
                if (!HidNative.TryGetCaps(data, out var caps) || caps.UsagePage != 0xFF43 || caps.Usage != 0x0202
                    || caps.InputReportByteLength != 20 || caps.OutputReportByteLength != 20)
                    throw new InvalidOperationException("A interface não oferece os relatórios HID++ de entrada e saída esperados.");
            }
            finally { HidNative.FreePreparsedData(data); }

            using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
            return await query(new HidHostQueryProtocol(stream, log)).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}

// Kept independent of the native handle so correlation, timeout and decoding can be exercised without hardware.
internal sealed class HidHostQueryProtocol(Stream stream, Action<string> log)
{
    private const byte SoftwareId = 0x0D;

    internal async Task<IReadOnlyList<HidControlInfo>> QueryControlsAsync(CancellationToken token)
    {
        var feature = await ResolveAsync(0x1B04, token).ConfigureAwait(false);
        if (feature == 0) throw new NotSupportedException("O dispositivo não anunciou a tabela de controles 1B04.");
        var countReply = await RequestAsync(feature, 0, [], token).ConfigureAwait(false);
        var count = countReply[4];
        if (count > 64) throw new NotSupportedException("Tabela com mais de 64 controles; diagnóstico limitado a 64.");
        var result = new List<HidControlInfo>();
        for (byte index = 0; index < count; index++)
        {
            var reply = await RequestAsync(feature, 1, [index], token).ConfigureAwait(false);
            var control = ParseControlInfo(reply);
            if (result.Any(item => item.ControlId == control.ControlId))
                throw new IOException("Controle duplicado na resposta; consulta inconclusiva.");
            result.Add(control);
            log($"CONTROLE · CID {control.ControlId:X4} · flags {control.Flags:X4} · desvio {control.CanDivert} · evento analítico {control.HasAnalyticsEvent}");
        }
        log($"CONTROLES · {count} consultados. Nenhuma configuração de tecla alterada.");
        return result;
    }

    internal async Task SetEasySwitchAnalyticsAsync(bool enabled, CancellationToken token)
    {
        var feature = await ResolveAsync(0x1B04, token).ConfigureAwait(false);
        if (feature == 0) throw new NotSupportedException("O teclado não anunciou ReprogControls (1B04).");

        var countReply = await RequestAsync(feature, 0, [], token).ConfigureAwait(false);
        var count = countReply[4];
        if (count > 64) throw new NotSupportedException("Tabela com mais de 64 controles; operação limitada a 64.");
        var controls = new Dictionary<ushort, HidControlInfo>();
        for (byte index = 0; index < count; index++)
        {
            var reply = await RequestAsync(feature, 1, [index], token).ConfigureAwait(false);
            var control = ParseControlInfo(reply);
            if (!controls.TryAdd(control.ControlId, control))
                throw new IOException("Controle duplicado na resposta; nenhuma configuração foi alterada.");
        }

        ushort[] easySwitchControls = [0x00D1, 0x00D2, 0x00D3];
        foreach (var controlId in easySwitchControls)
        {
            if (!controls.TryGetValue(controlId, out var control) || !control.HasAnalyticsEvent)
                throw new NotSupportedException($"O controle Easy-Switch {controlId:X4} não anunciou suporte a eventos analíticos; nenhuma configuração foi alterada.");
        }

        var original = new Dictionary<ushort, bool>();
        foreach (var controlId in easySwitchControls)
        {
            var reply = await RequestAsync(feature, 2, [(byte)(controlId >> 8), (byte)controlId], token).ConfigureAwait(false);
            if (((reply[4] << 8) | reply[5]) != controlId)
                throw new IOException($"Resposta de configuração não corresponde ao controle {controlId:X4}.");
            original[controlId] = (reply[9] & 0x01) != 0;
        }

        var attempted = new List<ushort>();
        try
        {
            foreach (var controlId in easySwitchControls)
            {
                if (original[controlId] == enabled)
                {
                    log($"EVENTO EASY-SWITCH · CID {controlId:X4} · já estava {(enabled ? "ativado" : "desativado")}.");
                    continue;
                }
                attempted.Add(controlId);
                await SetAnalyticsReportingAsync(feature, controlId, enabled, token).ConfigureAwait(false);
                log($"EVENTO EASY-SWITCH · CID {controlId:X4} · analytics={(enabled ? "ativado" : "desativado")} · remapeamento preservado.");
            }
        }
        catch (Exception operationError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var controlId in attempted.AsEnumerable().Reverse())
            {
                try { await SetAnalyticsReportingAsync(feature, controlId, original[controlId], CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Falha ao configurar analytics; parte da configuração pode exigir restauração manual usando Desativar eventos Easy-Switch.", [operationError, .. rollbackErrors]);
            throw;
        }

        log($"CONFIGURAÇÃO · eventos analíticos Easy-Switch {(enabled ? "ativados" : "desativados")} nos CIDs D1/D2/D3. Nenhuma tecla foi desviada ou remapeada.");
    }

    private async Task SetAnalyticsReportingAsync(byte feature, ushort controlId, bool enabled, CancellationToken token)
    {
        // ReprogControls v4: update only analytics-event reporting (high-byte flag 0x01,
        // valid bit 0x02). Other reporting flags and the existing remap remain untouched.
        var requestParameters = new byte[]
        {
            (byte)(controlId >> 8), (byte)controlId,
            0x00, // Do not modify low-byte reporting flags.
            0x00, 0x00, // Remap = 0 means keep the existing mapping.
            enabled ? (byte)0x03 : (byte)0x02,
        };
        var reply = await RequestAsync(feature, 3, requestParameters, token).ConfigureAwait(false);
        if (!reply.AsSpan(4, requestParameters.Length).SequenceEqual(requestParameters))
            throw new IOException($"O teclado não confirmou a configuração de eventos do controle {controlId:X4}.");
        var verification = await RequestAsync(feature, 2, [(byte)(controlId >> 8), (byte)controlId], token).ConfigureAwait(false);
        if (((verification[4] << 8) | verification[5]) != controlId || ((verification[9] & 0x01) != 0) != enabled)
            throw new IOException($"A leitura de confirmação do controle {controlId:X4} divergiu da configuração solicitada.");
    }

    internal static HidControlInfo ParseControlInfo(ReadOnlySpan<byte> report)
    {
        if (report.Length != 20) throw new IOException("Resposta de controle incompleta.");
        return new HidControlInfo((ushort)((report[4] << 8) | report[5]), (ushort)(report[8] | (report[12] << 8)));
    }

    internal async Task<HidHostInfo> QueryAsync(CancellationToken token)
    {
        var feature = await ResolveAsync(0x1814, token).ConfigureAwait(false);
        if (feature == 0) throw new NotSupportedException("O dispositivo não anunciou a feature Change Host (1814).");
        var reply = await RequestAsync(feature, 0, [], token).ConfigureAwait(false);
        var result = ParseHostInfo(reply, feature);
        log($"CANAL CONFIRMADO · {result.Channel} de {result.HostCount} · feature 1814 no índice {feature:X2} · consulta pontual.");
        return result;
    }

    internal async Task<HidHostSwitchResult> SetMouseChannelAsync(int targetChannel, CancellationToken token)
    {
        if (targetChannel is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(targetChannel));
        var current = await QueryAsync(token).ConfigureAwait(false);
        if (targetChannel > current.HostCount)
            throw new ArgumentOutOfRangeException(nameof(targetChannel), $"O mouse informou somente {current.HostCount} canais configurados.");
        if (current.Channel == targetChannel)
        {
            log($"TROCA MOUSE · já está no canal {targetChannel}; nenhum comando enviado.");
            return new HidHostSwitchResult(current.Channel, targetChannel, current.HostCount, DateTimeOffset.Now);
        }

        // ChangeHost.setCurrentHost (function 1) is intentionally sent without
        // waiting for a response: a successful switch drops this HID connection.
        var request = CreateSetHostReport(current.FeatureIndex, targetChannel);
        log($"TX TROCA MOUSE · {HidReportFormatter.FormatHex(request)} · canal {current.Channel} → {targetChannel}.");
        await stream.WriteAsync(request, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        var sentAt = DateTimeOffset.Now;
        log($"COMANDO ENVIADO · B034 solicitado para o canal {targetChannel}. A confirmação depende da reconexão e de nova consulta.");
        return new HidHostSwitchResult(current.Channel, targetChannel, current.HostCount, sentAt);
    }

    internal static byte[] CreateSetHostReport(byte featureIndex, int targetChannel)
    {
        if (targetChannel is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(targetChannel));
        var request = new byte[20];
        request[0] = 0x11;
        request[1] = 0xFF;
        request[2] = featureIndex;
        request[3] = (byte)((1 << 4) | SoftwareId);
        request[4] = (byte)(targetChannel - 1);
        return request;
    }

    private async Task<byte> ResolveAsync(ushort feature, CancellationToken token)
    {
        var reply = await RequestAsync(0, 0, [(byte)(feature >> 8), (byte)feature], token).ConfigureAwait(false);
        log($"FEATURE · {feature:X4} → índice {reply[4]:X2}, versão {reply[6]}.");
        return reply[4];
    }

    // Requests are limited to feature discovery, host info, control-table/reporting reads,
    // and the narrowly scoped Easy-Switch analytics reporting update.
    private async Task<byte[]> RequestAsync(byte feature, byte function, byte[] parameters, CancellationToken token)
    {
        var request = new byte[20];
        request[0] = 0x11;
        request[1] = 0xFF;
        request[2] = feature;
        request[3] = (byte)((function << 4) | SoftwareId);
        parameters.CopyTo(request, 4);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            log($"TX HID++ · {HidReportFormatter.FormatHex(request)}");
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            var buffer = new byte[20];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("O dispositivo encerrou a conexão durante a consulta.");
                var report = buffer.AsSpan(0, count).ToArray();
                if (!Matches(report, request)) continue;
                log($"RX RESPOSTA · {HidReportFormatter.FormatHex(report)}");
                if (report[2] == 0xFF)
                    throw new IOException($"O dispositivo rejeitou a consulta HID++ (erro {report[5]:X2}).");
                return report;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Sem resposta à consulta em 3 segundos. Mantenha o dispositivo conectado e acordado neste PC.");
        }
    }

    internal static bool Matches(ReadOnlySpan<byte> report, ReadOnlySpan<byte> request)
    {
        if (request.Length != 20 || report.Length != 20 || report[0] != 0x11 || report[1] != request[1]) return false;
        return report[2] == 0xFF
            ? report[3] == request[2] && report[4] == request[3]
            : report[2] == request[2] && report[3] == request[3];
    }

    internal static HidHostInfo ParseHostInfo(ReadOnlySpan<byte> report, byte feature)
    {
        if (report.Length != 20 || report[4] is < 1 or > 3 || report[5] >= report[4])
            throw new IOException("Resposta de canal inválida: nenhum canal foi assumido.");
        return new HidHostInfo(report[4], report[5] + 1, feature, DateTimeOffset.Now);
    }
}
