using System.ComponentModel;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PebbleX.Core.Flow;
using PebbleX.Windows.Hid;
using PebbleX.Windows.Flow;

namespace PebbleX.Desktop;

public partial class MainWindow : Window
{
    private const int MaxLines = 1000;
    private readonly Channel<string> pending = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Queue<string> lines = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch elapsed = new();
    private readonly Dictionary<string, DeviceOption> knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly FlowLinkSettingsStore flowSettingsStore = new();
    private readonly FlowPeerTransport flowTransport = new();
    private readonly FlowPairingService flowPairing = new();
    private readonly System.Windows.Forms.NotifyIcon trayIcon = new();
    private readonly ObservableCollection<DiscoveredPeerOption> discoveredPeerOptions = [];
    private FlowLinkConfiguration flowConfiguration = null!;
    private readonly ConcurrentDictionary<Guid, PendingFlowTransfer> pendingMouseArrivals = new();
    private readonly ConcurrentDictionary<Guid, PendingMouseRequest> pendingMouseConfirmations = new();
    private readonly ConcurrentDictionary<Guid, byte> confirmationsInProgress = new();
    private readonly ConcurrentDictionary<long, Task> flowOperations = new();
    private long nextFlowOperationId;
    private DateTimeOffset nextInventoryRefresh;
    private CancellationTokenSource? captureCancellation;
    private Task? captureTask;
    private Task? refreshTask;
    private Task? queryTask;
    private CancellationTokenSource? queryCancellation;
    private Task? analyticsTask;
    private CancellationTokenSource? analyticsCancellation;
    private CancellationTokenSource? flowCancellation;
    private CancellationTokenSource? pairingCancellation;
    private Task? flowListenerTask;
    private Task? pairingListenerTask;
    private Task? discoveryTask;
    private Task? keyboardArrivalTask;
    private Task? mouseArrivalTask;
    private long reportCount;
    private long droppedCount;
    private string status = "Parado";
    private bool closing;
    private bool canClose;
    private bool flowEnabled;
    private bool exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        DiscoveredPeersList.ItemsSource = discoveredPeerOptions;
        flowConfiguration = flowSettingsStore.LoadOrCreate();
        LocalInstallationIdText.Text = flowConfiguration.LocalInstallationId.ToString("D");
        if (flowConfiguration.IsPaired)
        {
            PeerInstallationIdInput.Text = flowConfiguration.PeerInstallationId!.Value.ToString("D");
            PeerNameInput.Text = flowConfiguration.PeerName;
            PeerAddressInput.Text = flowConfiguration.PeerAddress;
            FlowKeyInput.Password = flowConfiguration.SharedKeyHex;
            LinkStatusText.Text = $"Vínculo salvo com {flowConfiguration.PeerName}. Repita o mesmo vínculo no outro PC.";
            FlowStatusText.Text = $"Computador vinculado: {flowConfiguration.PeerName} · pronto para ativar nos dois PCs";
            FlowButton.IsEnabled = true;
        }
        timer.Tick += async (_, _) =>
        {
            Flush();
            if (DateTimeOffset.Now >= nextInventoryRefresh && !DevicePicker.IsDropDownOpen && analyticsTask is null)
                await RefreshAsync(manual: false);
        };
        timer.Start();
        Log("Pronto. Selecione um dispositivo e clique em Iniciar captura.");
        Log("Use o campo de evento para anotar a sequência de canais do teste.");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(bool manual = true)
    {
        if (closing || refreshTask is not null || captureTask is not null || queryTask is not null || analyticsTask is not null) return;
        RefreshButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        QueryButton.IsEnabled = false;
        EnableAnalyticsButton.IsEnabled = false;
        DisableAnalyticsButton.IsEnabled = false;
        SetMouseChannelButton.IsEnabled = false;
        MouseTargetChannel.IsEnabled = false;
        DevicePicker.IsEnabled = false;
        var selectedPath = (DevicePicker.SelectedItem as DeviceOption)?.Collection.DevicePath;
        try
        {
            var query = Task.Run(() =>
            {
                var hid = new HidCollectionEnumerator().Enumerate();
                IReadOnlyList<HidBluetoothDevice> bluetooth;
                try { bluetooth = new HidBluetoothInventory().Enumerate(); }
                catch (Win32Exception) { bluetooth = []; }
                return (Hid: hid, Bluetooth: bluetooth);
            });
            refreshTask = query;
            var (collections, bluetooth) = await query;
            if (closing) return;
            var present = collections.Where(item => item.VendorId == 0x046D
                    && item.UsagePage == 0xFF43 && item.Usage == 0x0202 && item.InputReportByteLength is > 0)
                .Select(item => new DeviceOption(item, Describe(item, collections), true)).ToArray();
            foreach (var path in knownDevices.Keys.Where(path => path.StartsWith("bluetooth:", StringComparison.OrdinalIgnoreCase)).ToArray())
                knownDevices.Remove(path);
            foreach (var path in knownDevices.Keys.ToArray())
                knownDevices[path] = knownDevices[path] with { IsPresent = false };
            foreach (var item in present) knownDevices[item.Collection.DevicePath] = item;
            var bluetoothByInstance = bluetooth.ToDictionary(item => item.InstanceId, StringComparer.OrdinalIgnoreCase);
            foreach (var path in knownDevices.Keys.ToArray())
            {
                var item = knownDevices[path];
                if (item.IsPresent || item.Collection.InputReportByteLength is not > 0
                    || item.Collection.ProductId is not ushort productId
                    || item.Collection.ParentDeviceInstanceId is not string parentId
                    || !bluetoothByInstance.TryGetValue(parentId, out var matchingDevice)
                    || matchingDevice.ProductId != productId)
                {
                    if (!item.IsPresent) knownDevices.Remove(path);
                    continue;
                }

                var name = productId switch { 0xB034 => "Mouse", 0xB377 => "Teclado", _ => "Dispositivo" };
                knownDevices[path] = item with
                {
                    Description = $"{name} · Logitech · {productId:X4} · Bluetooth reconhecido; diagnóstico indisponível",
                    BluetoothRecognized = true
                };
            }
            foreach (var device in bluetooth)
            {
                if (knownDevices.Values.Any(item => item.Collection.ProductId == device.ProductId
                    && item.Collection.InputReportByteLength is > 0
                    && string.Equals(item.Collection.ParentDeviceInstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase))) continue;
                var name = device.ProductId switch { 0xB034 => "Mouse", 0xB377 => "Teclado", _ => "Dispositivo" };
                var placeholder = new HidCollectionInfo($"bluetooth:{device.InstanceId}", 0x046D, device.ProductId,
                    null, null, null, null, null, null, device.InstanceId, null, null);
                knownDevices[placeholder.DevicePath] = new DeviceOption(placeholder,
                    $"{name} · Logitech · {device.ProductId:X4} · Bluetooth reconhecido; HID indisponível", false, true);
            }
            var options = knownDevices.Values.OrderBy(item => item.Collection.ProductId == 0xB377 ? 0 : 1)
                .ThenBy(item => item.Label).ToArray();
            KeyboardSummary.Text = GetDeviceSummary(options, 0xB377);
            MouseSummary.Text = GetDeviceSummary(options, 0xB034);
            var previous = DevicePicker.ItemsSource as DeviceOption[];
            var changed = previous is null || !previous.SequenceEqual(options);
            if (changed)
            {
                DevicePicker.ItemsSource = options;
                DevicePicker.SelectedItem = options.FirstOrDefault(item => string.Equals(item.Collection.DevicePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
            }
            if (manual || changed)
                Log($"Inventário atualizado: {present.Length} interfaces HID ativas; "
                    + $"{options.Count(item => item.BluetoothRecognized && !item.IsPresent)} Bluetooth sem interface HID.");
            if (options.Length == 0) DeviceDetails.Text = "Nenhum dispositivo Bluetooth de diagnóstico disponível. A lista verifica o retorno automaticamente.";
        }
        catch (Exception exception)
        {
            DeviceDetails.Text = "Não foi possível consultar os dispositivos. Tente atualizar novamente.";
            Log($"Falha no inventário: {exception.Message}");
        }
        finally
        {
            refreshTask = null;
            nextInventoryRefresh = DateTimeOffset.Now.AddSeconds(5);
            RefreshButton.IsEnabled = !closing;
            DevicePicker.IsEnabled = !closing;
            StartButton.IsEnabled = !closing && DevicePicker.SelectedItem is DeviceOption { IsPresent: true, Collection.InputReportByteLength: > 0 };
            QueryButton.IsEnabled = StartButton.IsEnabled && DevicePicker.SelectedItem is DeviceOption { IsPresent: true };
            UpdateAnalyticsButton();
            UpdateMouseSwitchControls();
        }
    }

    private static string GetDeviceSummary(IEnumerable<DeviceOption> options, ushort productId)
    {
        var device = options.FirstOrDefault(item => item.Collection.ProductId == productId);
        if (device is null) return productId == 0xB377 ? "Teclado não encontrado" : "Mouse não encontrado";
        var label = device.Description.Split('·')[1].Trim();
        return device.IsPresent ? $"{label} · conectado" : $"{label} · aguardando";
    }

    private void DeveloperMode_Click(object sender, RoutedEventArgs e)
    {
        var showDeveloper = DeveloperSurface.Visibility != Visibility.Visible;
        DeveloperSurface.Visibility = showDeveloper ? Visibility.Visible : Visibility.Collapsed;
        HomeSurface.Visibility = showDeveloper ? Visibility.Collapsed : Visibility.Visible;
        DeveloperModeButton.Content = showDeveloper ? "Voltar ao Flow" : "Modo desenvolvedor";
        Title = showDeveloper ? "PebbleX · Desenvolvedor" : "PebbleX";
    }

    private async void LinkComputer_Click(object sender, RoutedEventArgs e)
    {
        var opening = LinkPanel.Visibility != Visibility.Visible;
        if (opening && flowEnabled)
        {
            FlowStatusText.Text = "Pause o Flow nos dois PCs antes de configurar o vínculo.";
            return;
        }
        LinkPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        FlowCard.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        PeerInfoPanel.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        HomeFooter.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        LinkComputerButton.Content = opening ? "Voltar" : "Vincular computador";
        if (opening) StartPairingSetup();
        else await StopPairingSetupAsync();
    }

    private void StartPairingSetup()
    {
        if (pairingCancellation is not null) return;
        pairingCancellation = new CancellationTokenSource();
        var token = pairingCancellation.Token;
        PairingResultText.Text = string.Empty;
        DiscoveryStatusText.Text = "Procurando PebbleX na rede local… deixe esta tela aberta nos dois PCs.";
        pairingListenerTask = flowPairing.ListenForPairingAsync(flowConfiguration.LocalInstallationId,
            flowConfiguration.LocalName, flowConfiguration.Port, ConfirmIncomingPairingAsync,
            result => Dispatcher.BeginInvoke(new Action(() => SavePairingResult(result))), Log, token);
        discoveryTask = flowPairing.DiscoverAsync(flowConfiguration.LocalInstallationId,
            flowConfiguration.LocalName, flowConfiguration.Port, peers => Dispatcher.BeginInvoke(new Action(() => UpdateDiscoveredPeers(peers))), token);
        ObservePairingTask(pairingListenerTask, "pareamento");
        ObservePairingTask(discoveryTask, "descoberta");
    }

    private async Task StopPairingSetupAsync()
    {
        pairingCancellation?.Cancel();
        var tasks = new[] { pairingListenerTask, discoveryTask }.OfType<Task>().ToArray();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Log($"ENCERRAMENTO DA DESCOBERTA · {exception.GetBaseException().Message}"); }
        pairingCancellation?.Dispose();
        pairingCancellation = null;
        pairingListenerTask = null;
        discoveryTask = null;
        discoveredPeerOptions.Clear();
        PairSelectedButton.IsEnabled = false;
    }

    private void ObservePairingTask(Task task, string operation)
    {
        _ = task.ContinueWith(completed =>
        {
            if (!completed.IsFaulted) return;
            var message = completed.Exception?.GetBaseException().Message ?? "falha desconhecida";
            Log($"{operation.ToUpperInvariant()} INDISPONÍVEL · {message}");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!closing) DiscoveryStatusText.Text = $"Não foi possível iniciar {operation}: {message}. Use a configuração manual abaixo.";
            }));
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void UpdateDiscoveredPeers(IReadOnlyList<FlowDiscoveredPeer> peers)
    {
        var selectedId = (DiscoveredPeersList.SelectedItem as DiscoveredPeerOption)?.Peer.InstallationId;
        var changed = peers.Count != discoveredPeerOptions.Count || peers.Any(peer =>
        {
            var current = discoveredPeerOptions.FirstOrDefault(item => item.Peer.InstallationId == peer.InstallationId);
            return current is null || current.Peer.Name != peer.Name || !current.Peer.Address.Equals(peer.Address);
        });
        if (changed)
        {
            discoveredPeerOptions.Clear();
            foreach (var peer in peers)
                discoveredPeerOptions.Add(new DiscoveredPeerOption(peer));
            DiscoveredPeersList.SelectedItem = discoveredPeerOptions.FirstOrDefault(item => item.Peer.InstallationId == selectedId);
        }
        else
        {
            foreach (var peer in peers)
                discoveredPeerOptions.First(item => item.Peer.InstallationId == peer.InstallationId).Peer = peer;
        }
        PairSelectedButton.IsEnabled = DiscoveredPeersList.SelectedItem is not null && !flowEnabled && !closing;
        DiscoveryStatusText.Text = peers.Count switch
        {
            0 => "Nenhum computador encontrado ainda. Abra esta tela no outro PC; se ambos estiverem abertos, confira a permissão do PebbleX na rede privada/Firewall (UDP 47630). A configuração manual continua disponível abaixo.",
            1 => "1 computador encontrado. Selecione-o para iniciar o vínculo seguro.",
            _ => $"{peers.Count} computadores encontrados. Selecione o PC que deve acompanhar este teclado."
        };
    }

    private void DiscoveredPeers_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PairSelectedButton.IsEnabled = DiscoveredPeersList.SelectedItem is not null && !flowEnabled && !closing;

    private async void SearchPeers_Click(object sender, RoutedEventArgs e)
    {
        if (pairingCancellation is null || discoveryTask?.IsFaulted == true || pairingListenerTask?.IsFaulted == true)
        {
            await StopPairingSetupAsync();
            StartPairingSetup();
        }
        discoveredPeerOptions.Clear();
        PairSelectedButton.IsEnabled = false;
        DiscoveryStatusText.Text = "Busca automática ativa… deixe a tela de vínculo aberta nos dois PCs.";
    }

    private async void PairSelected_Click(object sender, RoutedEventArgs e)
    {
        if (flowEnabled || DiscoveredPeersList.SelectedItem is not DiscoveredPeerOption selected) return;
        PairSelectedButton.IsEnabled = false;
        try
        {
            DiscoveryStatusText.Text = $"Conectando a {selected.Peer.Name} e verificando o vínculo…";
            var result = await flowPairing.PairAsync(flowConfiguration.LocalInstallationId, flowConfiguration.LocalName,
                selected.Peer, ConfirmOutgoingPairingAsync, pairingCancellation?.Token ?? CancellationToken.None);
            SavePairingResult(result);
        }
        catch (OperationCanceledException) when (pairingCancellation?.IsCancellationRequested == true) { }
        catch (Exception exception)
        {
            DiscoveryStatusText.Text = $"Não foi possível vincular: {exception.Message}";
            Log($"VÍNCULO AUTOMÁTICO FALHOU · peer={selected.Peer.Name} · {exception.Message}");
        }
        finally { PairSelectedButton.IsEnabled = DiscoveredPeersList.SelectedItem is not null && !flowEnabled && !closing; }
    }

    private async Task<bool> ConfirmIncomingPairingAsync(FlowPairingChallenge challenge) =>
        await ConfirmPairingChallengeAsync(challenge, incoming: true);

    private async Task<bool> ConfirmOutgoingPairingAsync(FlowPairingChallenge challenge) =>
        await ConfirmPairingChallengeAsync(challenge, incoming: false);

    private async Task<bool> ConfirmPairingChallengeAsync(FlowPairingChallenge challenge, bool incoming)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            var action = incoming ? "solicita vínculo com este PC" : "está sendo vinculado";
            var replace = flowConfiguration.IsPaired && flowConfiguration.PeerInstallationId != challenge.PeerInstallationId
                ? $"\n\nEste vínculo substituirá o computador atual ({flowConfiguration.PeerName})." : string.Empty;
            var message = $"{challenge.PeerName} ({challenge.PeerAddress}) {action}.\n\n"
                + $"Compare este código nas duas telas: {challenge.VerificationCode}\n\n"
                + "Só confirme se o código for idêntico no outro PC e você iniciou este vínculo." + replace;
            return System.Windows.MessageBox.Show(this, message, "Confirmar vínculo PebbleX", MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }).Task;
    }

    private void SavePairingResult(FlowPairingResult result)
    {
        if (closing || flowEnabled) return;
        try
        {
            var updated = flowConfiguration with
            {
                PeerInstallationId = result.PeerInstallationId,
                PeerName = result.PeerName,
                PeerAddress = result.PeerAddress,
                Port = result.PeerFlowPort,
                SharedKeyHex = result.SharedKeyHex.ToUpperInvariant()
            };
            flowSettingsStore.Save(updated);
            flowConfiguration = updated;
            PeerInstallationIdInput.Text = result.PeerInstallationId.ToString("D");
            PeerNameInput.Text = result.PeerName;
            PeerAddressInput.Text = result.PeerAddress;
            FlowKeyInput.Password = result.SharedKeyHex;
            FlowButton.IsEnabled = true;
            FlowStatusText.Text = $"Vínculo salvo com {result.PeerName} · ative o Flow nos dois PCs";
            DiscoveryStatusText.Text = $"Vínculo seguro com {result.PeerName} concluído. A rede local e a porta do Flow foram testadas.";
            PairingResultText.Text = $"Computador vinculado: {result.PeerName} ({result.PeerAddress}). A chave foi criada e protegida neste Windows.";
            LinkStatusText.Text = $"Vínculo salvo com {result.PeerName}.";
            Log($"VÍNCULO AUTOMÁTICO SALVO · peer={result.PeerInstallationId:D} · endereço observado={result.PeerAddress}:{result.PeerFlowPort}.");
        }
        catch (Exception exception)
        {
            DiscoveryStatusText.Text = $"O pareamento foi confirmado, mas não foi possível salvar: {exception.Message}";
            Log($"PERSISTÊNCIA DO VÍNCULO FALHOU · {exception.Message}");
        }
    }

    private void CopyLocalId_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(flowConfiguration.LocalInstallationId.ToString("D"));
        LinkStatusText.Text = "ID deste PC copiado.";
    }

    private void GenerateFlowKey_Click(object sender, RoutedEventArgs e)
    {
        FlowKeyInput.Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        System.Windows.Clipboard.SetText(FlowKeyInput.Password);
        LinkStatusText.Text = "Chave de 256 bits gerada e copiada. Use a mesma nos dois PCs.";
    }

    private void SaveFlowLink_Click(object sender, RoutedEventArgs e)
    {
        if (flowEnabled)
        {
            LinkStatusText.Text = "Desative o Flow antes de alterar o vínculo.";
            return;
        }
        if (!Guid.TryParse(PeerInstallationIdInput.Text.Trim(), out var peerId) || peerId == Guid.Empty
            || !IPAddress.TryParse(PeerAddressInput.Text.Trim(), out var peerAddress)
            || peerAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            LinkStatusText.Text = "Informe o ID válido e o IPv4 local do outro PebbleX.";
            return;
        }
        var peerName = PeerNameInput.Text.Trim();
        if (peerName.Length is 0 or > 80)
        {
            LinkStatusText.Text = "Informe um nome para identificar o outro PC (1 a 80 caracteres).";
            return;
        }
        if (peerId == flowConfiguration.LocalInstallationId)
        {
            LinkStatusText.Text = "O ID remoto precisa ser diferente deste PC.";
            return;
        }

        try
        {
            _ = FlowLinkSettingsStore.ParseSharedKey(FlowKeyInput.Password);
            var updated = flowConfiguration with
            {
                PeerInstallationId = peerId,
                PeerName = peerName,
                PeerAddress = PeerAddressInput.Text.Trim(),
                Port = 47631,
                SharedKeyHex = FlowKeyInput.Password.ToUpperInvariant()
            };
            flowSettingsStore.Save(updated);
            flowConfiguration = updated;
            LinkStatusText.Text = "Vínculo protegido salvo neste PC. Configure o outro PC com este ID, IP e a mesma chave.";
            FlowStatusText.Text = $"Computador vinculado: {peerName} · ative o Flow nos dois PCs";
            FlowButton.IsEnabled = true;
            Log($"VÍNCULO LOCAL SALVO · peer={peerId:D} · endereço={updated.PeerAddress}:47631 · chave protegida pelo Windows.");
        }
        catch (Exception exception)
        {
            LinkStatusText.Text = $"Não foi possível salvar o vínculo: {exception.Message}";
            Log($"VÍNCULO FALHOU · {exception.Message}");
        }
    }

    private async void Flow_Click(object sender, RoutedEventArgs e)
    {
        if (flowEnabled)
        {
            await StopFlowAsync(null);
            return;
        }
        if (!flowConfiguration.IsPaired)
        {
            FlowStatusText.Text = "Vincule outro PebbleX antes de ativar o Flow.";
            return;
        }

        var peer = CreateFlowPeer();
        flowEnabled = true;
        flowCancellation = new CancellationTokenSource();
        var token = flowCancellation.Token;
        FlowButton.Content = "Desativar Flow";
        FlowButton.IsEnabled = true;
        FlowStatusText.Text = $"Flow ativo · aguardando o teclado · par: {flowConfiguration.PeerName}";
        Log($"FLOW ATIVADO · escuta TCP na porta {flowConfiguration.Port}; pareado com {peer.InstallationId:D}.");

        var peers = new Dictionary<Guid, FlowPeer> { [peer.InstallationId] = peer };
        flowListenerTask = flowTransport.ListenAsync(new IPEndPoint(IPAddress.Any, flowConfiguration.Port), peers,
            (signal, authenticatedPeer) => Dispatcher.BeginInvoke(new Action(() => TrackFlowOperation(HandleFlowSignalAsync(signal, authenticatedPeer)))),
            Log, token);
        _ = flowListenerTask.ContinueWith(task =>
        {
            if (task.IsFaulted)
                Dispatcher.BeginInvoke(new Action(() => _ = StopFlowAsync(task.Exception?.GetBaseException().Message ?? "Listener Flow falhou.")));
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        keyboardArrivalTask = new HidKeyboardArrivalMonitor().RunAsync((_, keyboardChannel) =>
        {
            if (!flowEnabled || flowCancellation is null) return;
            var transferId = Guid.NewGuid();
            pendingMouseArrivals[transferId] = new PendingFlowTransfer(keyboardChannel.Channel, peer, DateTimeOffset.UtcNow);
            var signal = new FlowSignal(flowConfiguration.LocalInstallationId, transferId,
                FlowSignalKind.KeyboardChannelObserved, keyboardChannel.Channel, DateTimeOffset.UtcNow);
            Log($"FLOW TECLADO · canal {keyboardChannel.Channel} confirmado · transferência {transferId:N}.");
            TrackFlowOperation(SendFlowSignalAsync(peer, signal, flowCancellation.Token));
            TrackFlowOperation(TryConfirmMouseArrivalAsync(transferId, flowCancellation.Token));
        }, Log, token);
        mouseArrivalTask = WatchMouseArrivalsAsync(token);
    }

    private async Task StopFlowAsync(string? failure)
    {
        flowEnabled = false;
        flowCancellation?.Cancel();
        var active = new[] { flowListenerTask, keyboardArrivalTask, mouseArrivalTask }.OfType<Task>().ToArray();
        try { await Task.WhenAll(active); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Log($"FLOW ENCERRADO · {exception.GetBaseException().Message}"); }
        try { await Task.WhenAll(flowOperations.Values.ToArray()); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Log($"FLOW OPERAÇÃO ENCERRADA · {exception.GetBaseException().Message}"); }
        flowCancellation?.Dispose();
        flowCancellation = null;
        flowListenerTask = null;
        keyboardArrivalTask = null;
        mouseArrivalTask = null;
        pendingMouseArrivals.Clear();
        pendingMouseConfirmations.Clear();
        confirmationsInProgress.Clear();
        flowOperations.Clear();
        FlowButton.Content = "Ativar Flow";
        FlowButton.IsEnabled = flowConfiguration.IsPaired && !closing;
        FlowStatusText.Text = failure is null ? "Flow desativado" : $"Flow interrompido: {failure}";
        Log(failure is null ? "FLOW DESATIVADO." : $"FLOW FALHOU · {failure}");
    }

    private FlowPeer CreateFlowPeer() => new(flowConfiguration.PeerInstallationId!.Value,
        new IPEndPoint(IPAddress.Parse(flowConfiguration.PeerAddress!), flowConfiguration.Port),
        FlowLinkSettingsStore.ParseSharedKey(flowConfiguration.SharedKeyHex!), flowConfiguration.PeerName);

    private async Task HandleFlowSignalAsync(FlowSignal signal, FlowPeer peer)
    {
        if (!flowEnabled || signal.SenderInstallationId != peer.InstallationId) return;
        if (signal.Kind == FlowSignalKind.MouseChannelConfirmed)
        {
            if (pendingMouseConfirmations.TryGetValue(signal.TransferId, out var request)
                && request.Channel == signal.Channel && pendingMouseConfirmations.TryRemove(signal.TransferId, out _))
            {
                FlowStatusText.Text = $"Sincronizado · teclado e mouse confirmados no canal {signal.Channel} em {peer.DisplayName ?? flowConfiguration.PeerName}.";
                Log($"FLOW CONFIRMADO · transferência {signal.TransferId:N} · canal {signal.Channel}.");
            }
            else Log($"FLOW ACK IGNORADO · transferência desconhecida ou canal divergente · {signal.TransferId:N}.");
            return;
        }

        if (signal.Kind != FlowSignalKind.KeyboardChannelObserved) return;
        pendingMouseConfirmations[signal.TransferId] = new PendingMouseRequest(signal.Channel, DateTimeOffset.UtcNow);
        FlowStatusText.Text = $"Teclado no canal {signal.Channel} · movendo mouse local…";
        Log($"FLOW PEDIDO RECEBIDO · peer={peer.DisplayName} · canal={signal.Channel} · id={signal.TransferId:N}.");
        try
        {
            var mouse = await FindConnectedMouseAsync();
            var result = await Task.Run(() => new HidHostQuery().SetMouseChannelAsync(mouse, signal.Channel, Log, flowCancellation!.Token));
            if (result.PreviousChannel == result.TargetChannel)
                Log($"FLOW MOUSE · já estava no canal {signal.Channel}; aguardando confirmação do destino.");
            else Log($"FLOW MOUSE · pedido enviado para o canal {signal.Channel}; aguardando confirmação no destino.");
            if (pendingMouseConfirmations.ContainsKey(signal.TransferId))
                FlowStatusText.Text = $"Mouse solicitado no canal {signal.Channel} · aguardando confirmação do outro PC…";
        }
        catch (OperationCanceledException) when (flowCancellation?.IsCancellationRequested == true)
        {
            pendingMouseConfirmations.TryRemove(signal.TransferId, out _);
        }
        catch (Exception exception)
        {
            if (pendingMouseConfirmations.TryRemove(signal.TransferId, out _))
            {
                FlowStatusText.Text = $"Não foi possível mover o mouse: {exception.Message}";
                Log($"FLOW MOUSE FALHOU · {exception.Message}");
            }
        }
    }

    private async Task SendFlowSignalAsync(FlowPeer peer, FlowSignal signal, CancellationToken token)
    {
        try { await flowTransport.SendAsync(peer, signal, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            pendingMouseArrivals.TryRemove(signal.TransferId, out _);
            Log($"FLOW ENVIO FALHOU · {exception.Message}");
            await Dispatcher.InvokeAsync(() => FlowStatusText.Text = $"Falha ao falar com {peer.DisplayName}: {exception.Message}");
        }
    }

    private async Task WatchMouseArrivalsAsync(CancellationToken token)
    {
        HashSet<string>? previous = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var collections = await Task.Run(() => new HidCollectionEnumerator().Enumerate(), token);
            var mousePaths = collections.Where(IsMouseDiagnosticCollection).Select(item => item.DevicePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (previous is null) previous = mousePaths;
            else
            {
                if (mousePaths.Except(previous, StringComparer.OrdinalIgnoreCase).Any())
                {
                    Log("FLOW MOUSE CHEGOU · conferindo o canal antes de confirmar ao par.");
                }
                if (mousePaths.Count > 0)
                    foreach (var transfer in pendingMouseArrivals.Keys)
                        TrackFlowOperation(TryConfirmMouseArrivalAsync(transfer, token));
                previous = mousePaths;
            }

            foreach (var transfer in pendingMouseArrivals.Where(item => DateTimeOffset.UtcNow - item.Value.CreatedAt > TimeSpan.FromSeconds(30)).ToArray())
            {
                pendingMouseArrivals.TryRemove(transfer.Key, out _);
                Log($"FLOW SEM CONFIRMAÇÃO · transferência {transfer.Key:N} expirou.");
                await Dispatcher.InvokeAsync(() => FlowStatusText.Text = "O mouse não confirmou o canal no prazo; estado desconhecido.");
            }
            foreach (var request in pendingMouseConfirmations.Where(item => DateTimeOffset.UtcNow - item.Value.CreatedAt > TimeSpan.FromSeconds(30)).ToArray())
            {
                pendingMouseConfirmations.TryRemove(request.Key, out _);
                Log($"FLOW SEM ACK · transferência {request.Key:N} expirou.");
                await Dispatcher.InvokeAsync(() => FlowStatusText.Text = "O outro PC não confirmou a chegada do mouse; estado desconhecido.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(750), token);
        }
    }

    private async Task TryConfirmMouseArrivalAsync(Guid transferId, CancellationToken token)
    {
        if (!pendingMouseArrivals.TryGetValue(transferId, out var pendingTransfer)
            || !confirmationsInProgress.TryAdd(transferId, 0)) return;
        try
        {
            var mouse = await FindConnectedMouseAsync(token);
            var channel = await Task.Run(() => new HidHostQuery().QueryAsync(mouse, Log, token), token);
            if (channel.Channel != pendingTransfer.Channel)
            {
                Log($"FLOW MOUSE AGUARDANDO · atual={channel.Channel}, teclado={pendingTransfer.Channel}.");
                return;
            }

            var confirmation = new FlowSignal(flowConfiguration.LocalInstallationId, transferId,
                FlowSignalKind.MouseChannelConfirmed, channel.Channel, DateTimeOffset.UtcNow);
            await flowTransport.SendAsync(pendingTransfer.Peer, confirmation, token);
            pendingMouseArrivals.TryRemove(transferId, out _);
            Log($"FLOW MOUSE CONFIRMADO LOCALMENTE · canal {channel.Channel} · confirmação enviada ao par.");
            await Dispatcher.InvokeAsync(() => FlowStatusText.Text = $"Teclado e mouse confirmados no canal {channel.Channel}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Nenhuma interface HID++ do mouse", StringComparison.OrdinalIgnoreCase)) { }
        catch (Exception exception)
        {
            Log($"FLOW CONFIRMAÇÃO PENDENTE · {exception.Message}");
        }
        finally { confirmationsInProgress.TryRemove(transferId, out _); }
    }

    private static async Task<HidCollectionInfo> FindConnectedMouseAsync(CancellationToken token = default)
    {
        var mice = await Task.Run(() => new HidCollectionEnumerator().Enumerate()
            .Where(IsMouseDiagnosticCollection).ToArray(), token);
        return mice.Length switch
        {
            1 => mice[0],
            0 => throw new InvalidOperationException("Nenhuma interface HID++ do mouse B034 está conectada neste PC."),
            _ => throw new InvalidOperationException("Mais de uma interface do mouse B034 foi encontrada; operação recusada por ambiguidade.")
        };
    }

    private static bool IsMouseDiagnosticCollection(HidCollectionInfo item) => item.VendorId == 0x046D
        && item.ProductId == 0xB034 && item.UsagePage == 0xFF43 && item.Usage == 0x0202 && item.InputReportByteLength == 20;

    private sealed record PendingFlowTransfer(int Channel, FlowPeer Peer, DateTimeOffset CreatedAt);
    private sealed record PendingMouseRequest(int Channel, DateTimeOffset CreatedAt);

    private void TrackFlowOperation(Task operation)
    {
        var operationId = Interlocked.Increment(ref nextFlowOperationId);
        flowOperations[operationId] = operation;
        _ = operation.ContinueWith(completed => { flowOperations.TryRemove(operationId, out Task? ignored); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string Describe(HidCollectionInfo item, IReadOnlyList<HidCollectionInfo> all)
    {
        var siblings = item.DeviceContainerId is Guid container
            ? all.Where(other => other.DeviceContainerId == container) : [item];
        var kind = siblings.Any(other => other.UsagePage == 1 && other.Usage == 6) ? "Teclado"
            : siblings.Any(other => other.UsagePage == 1 && other.Usage == 2) ? "Mouse" : "Dispositivo";
        var product = string.IsNullOrWhiteSpace(item.Product) ? "Logitech" : item.Product;
        return $"{kind} · {product} · {item.ProductId:X4} · {item.UsagePage:X4}/{item.Usage:X4}";
    }

    private void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ChannelText.Text = "Canal desconhecido · consulte com o dispositivo conectado.";
        if (DevicePicker.SelectedItem is not DeviceOption option)
        {
            UpdateAnalyticsButton();
            UpdateMouseSwitchControls();
            return;
        }
        DeviceDetails.Text = option.IsPresent
            ? $"VID 046D · PID {option.Collection.ProductId:X4} · entrada de {option.Collection.InputReportByteLength} bytes. A captura acompanha somente esta interface."
            : option.BluetoothRecognized
                ? $"PID {option.Collection.ProductId:X4} · Windows reconhece o Bluetooth, mas não oferece a interface HID de diagnóstico neste momento. Captura e consulta indisponíveis; o dispositivo ainda pode funcionar como mouse no Windows."
                : $"PID {option.Collection.ProductId:X4} · interface HID ausente no Windows. Aguardando reconexão.";
        DevicePicker.ToolTip = option.Collection.DevicePath;
        StartButton.IsEnabled = captureCancellation is null && !closing && option.IsPresent && option.Collection.InputReportByteLength is > 0;
        QueryButton.IsEnabled = StartButton.IsEnabled && queryTask is null && option.IsPresent;
        UpdateAnalyticsButton();
        UpdateMouseSwitchControls();
    }

    private void UpdateAnalyticsButton()
    {
        var available = DevicePicker.SelectedItem is DeviceOption
        {
            IsPresent: true,
            Collection.ProductId: 0xB377,
            Collection.UsagePage: 0xFF43,
            Collection.Usage: 0x0202,
            Collection.InputReportByteLength: > 0
        };
        var idle = !closing && captureTask is null && queryTask is null && refreshTask is null && analyticsTask is null;
        EnableAnalyticsButton.IsEnabled = available && idle;
        DisableAnalyticsButton.IsEnabled = available && idle;
    }

    private void UpdateMouseSwitchControls()
    {
        var available = DevicePicker.SelectedItem is DeviceOption
        {
            IsPresent: true,
            Collection.ProductId: 0xB034,
            Collection.UsagePage: 0xFF43,
            Collection.Usage: 0x0202,
            Collection.InputReportByteLength: > 0
        };
        var idle = !closing && captureTask is null && queryTask is null && refreshTask is null;
        MouseTargetChannel.IsEnabled = available && idle;
        SetMouseChannelButton.IsEnabled = available && idle;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (captureTask is not null || queryTask is not null || analyticsTask is not null || DevicePicker.SelectedItem is not DeviceOption { IsPresent: true } option) return;
        ChannelText.Text = "Canal não acompanhado na captura · saída/retorno não revelam o destino.";
        captureCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref reportCount, 0);
        elapsed.Restart();
        SetRunning(true);
        Volatile.Write(ref status, "Iniciando");
        Log($"INÍCIO · {option.Label}");
        captureTask = CaptureAsync(option.Collection, captureCancellation);
    }

    private async Task CaptureAsync(HidCollectionInfo collection, CancellationTokenSource source)
    {
        try
        {
            await Task.Run(() => new HidDiagnosticSession().RunAsync(collection,
                value => Volatile.Write(ref status, value), Log,
                report =>
                {
                    Interlocked.Increment(ref reportCount);
                    Enqueue($"{report.Timestamp:HH:mm:ss.fff}  RX [{report.Bytes.Length}] {HidReportFormatter.FormatHex(report.Bytes)}");
                }, source.Token));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) { Log($"ERRO · {exception.Message}"); }
        finally
        {
            elapsed.Stop();
            Log($"FIM · {Interlocked.Read(ref reportCount)} relatórios recebidos.");
            Volatile.Write(ref status, "Parado");
            source.Dispose();
            captureCancellation = null;
            captureTask = null;
            SetRunning(false);
            Flush();
        }
    }

    private void SetRunning(bool running)
    {
        DevicePicker.IsEnabled = !running && !closing;
        RefreshButton.IsEnabled = !running && !closing;
        StartButton.IsEnabled = !running && !closing && DevicePicker.SelectedItem is DeviceOption { IsPresent: true, Collection.InputReportByteLength: > 0 };
        StopButton.IsEnabled = running && !closing;
        QueryButton.IsEnabled = !running && !closing && DevicePicker.SelectedItem is DeviceOption { IsPresent: true };
        UpdateAnalyticsButton();
        UpdateMouseSwitchControls();
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        if (captureTask is not null || queryTask is not null || analyticsTask is not null || DevicePicker.SelectedItem is not DeviceOption option) return;
        queryCancellation = new CancellationTokenSource();
        SetRunning(true);
        StopButton.IsEnabled = false;
        ChannelText.Text = "Consultando o canal informado pelo dispositivo…";
        Volatile.Write(ref status, "Consultando canal");
        Log($"CONSULTA · {option.Label} · não altera o canal.");
        queryTask = QueryChannelAsync(option.Collection, queryCancellation);
        await queryTask;
    }

    private async Task QueryChannelAsync(HidCollectionInfo collection, CancellationTokenSource source)
    {
        try
        {
            var result = await Task.Run(() => new HidHostQuery().QueryAsync(collection, Log, source.Token));
            ChannelText.Text = $"Última consulta: canal {result.Channel} de {result.HostCount}, às {result.ObservedAt:HH:mm:ss}. Consulte novamente após trocar.";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            Log("Consulta cancelada.");
            ChannelText.Text = "Canal desconhecido · consulta cancelada.";
        }
        catch (Exception exception)
        {
            Log($"CONSULTA FALHOU · {exception.Message}");
            ChannelText.Text = "Canal desconhecido · consulte o motivo no registro.";
        }
        finally
        {
            source.Dispose();
            queryCancellation = null;
            queryTask = null;
            Volatile.Write(ref status, "Parado");
            SetRunning(false);
            Flush();
        }
    }

    private async void SetMouseChannel_Click(object sender, RoutedEventArgs e)
    {
        if (captureTask is not null || queryTask is not null || analyticsTask is not null
            || DevicePicker.SelectedItem is not DeviceOption { IsPresent: true, Collection.ProductId: 0xB034 } option
            || MouseTargetChannel.SelectedItem is not ComboBoxItem item
            || !int.TryParse(item.Content?.ToString(), out var targetChannel)) return;

        queryCancellation = new CancellationTokenSource();
        SetRunning(true);
        StopButton.IsEnabled = false;
        ChannelText.Text = $"Consultando mouse e enviando solicitação para o canal {targetChannel}…";
        Volatile.Write(ref status, "Solicitando troca do mouse");
        Log($"TROCA SOLICITADA · Mouse B034 · destino canal {targetChannel}.");
        queryTask = SetMouseChannelAsync(option.Collection, targetChannel, queryCancellation);
        await queryTask;
    }

    private async Task SetMouseChannelAsync(HidCollectionInfo collection, int targetChannel, CancellationTokenSource source)
    {
        try
        {
            var result = await Task.Run(() => new HidHostQuery().SetMouseChannelAsync(collection, targetChannel, Log, source.Token));
            ChannelText.Text = result.PreviousChannel == result.TargetChannel
                ? $"Mouse já estava no canal {result.TargetChannel}; nada foi enviado."
                : $"Solicitação enviada ao mouse: canal {result.PreviousChannel} → {result.TargetChannel}. Consulte o canal após reconectar.";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            Log("Solicitação de troca do mouse cancelada antes do envio.");
            ChannelText.Text = "Solicitação de troca cancelada.";
        }
        catch (Exception exception)
        {
            Log($"TROCA DO MOUSE FALHOU · {exception.Message}");
            ChannelText.Text = "Não foi possível solicitar a troca; consulte o registro.";
        }
        finally
        {
            source.Dispose();
            queryCancellation = null;
            queryTask = null;
            Volatile.Write(ref status, "Parado");
            SetRunning(false);
            Flush();
        }
    }

    private async void EnableAnalytics_Click(object sender, RoutedEventArgs e) => await ConfigureAnalyticsFromUiAsync(true);

    private async void DisableAnalytics_Click(object sender, RoutedEventArgs e) => await ConfigureAnalyticsFromUiAsync(false);

    private async Task ConfigureAnalyticsFromUiAsync(bool enable)
    {
        if (captureTask is not null || queryTask is not null || analyticsTask is not null
            || DevicePicker.SelectedItem is not DeviceOption { IsPresent: true, Collection.ProductId: 0xB377 } option) return;

        analyticsCancellation = new CancellationTokenSource();
        SetRunning(true);
        StopButton.IsEnabled = false;
        ChannelText.Text = enable ? "Ativando eventos Easy-Switch do teclado…" : "Desativando eventos Easy-Switch do teclado…";
        Volatile.Write(ref status, enable ? "Configurando eventos" : "Restaurando configuração");
        Log($"CONFIGURAÇÃO · teclado B377 · eventos analíticos Easy-Switch {(enable ? "ativar" : "desativar")}.");
        analyticsTask = SetAnalyticsAsync(option.Collection, enable, analyticsCancellation);
        await analyticsTask;
    }

    private async Task SetAnalyticsAsync(HidCollectionInfo collection, bool enabled, CancellationTokenSource source)
    {
        try
        {
            await Task.Run(() => new HidHostQuery().SetEasySwitchAnalyticsAsync(collection, enabled, Log, source.Token));
            ChannelText.Text = enabled
                ? "Eventos Easy-Switch ativados. Inicie a captura e pressione os botões 1, 2 e 3 do teclado."
                : "Eventos Easy-Switch desativados; configuração de teclas preservada.";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            Log("Configuração de eventos cancelada.");
            ChannelText.Text = "Configuração de eventos cancelada.";
        }
        catch (Exception exception)
        {
            Log($"CONFIGURAÇÃO FALHOU · {exception.Message}");
            ChannelText.Text = "Não foi possível configurar eventos; consulte o registro.";
        }
        finally
        {
            source.Dispose();
            analyticsCancellation = null;
            analyticsTask = null;
            Volatile.Write(ref status, "Parado");
            SetRunning(false);
            UpdateAnalyticsButton();
            Flush();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        Volatile.Write(ref status, "Parando");
        captureCancellation?.Cancel();
    }

    private void Log(string message) => Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}");
    private void Enqueue(string message)
    {
        if (!pending.Writer.TryWrite(message)) Interlocked.Increment(ref droppedCount);
    }

    private void Flush()
    {
        var changed = false;
        var dropped = Interlocked.Exchange(ref droppedCount, 0);
        if (dropped > 0)
        {
            lines.Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff}  AVISO · {dropped} registros omitidos por excesso de tráfego.");
            changed = true;
        }
        for (var count = 0; count < 1000 && pending.Reader.TryRead(out var line); count++)
        {
            lines.Enqueue(line);
            changed = true;
        }
        while (lines.Count > MaxLines) lines.Dequeue();
        if (changed)
        {
            var offset = LogBox.VerticalOffset;
            LogBox.Text = string.Join(Environment.NewLine, lines);
            if (AutoScroll.IsChecked == true) LogBox.ScrollToEnd();
            else LogBox.ScrollToVerticalOffset(offset);
        }
        StatusText.Text = Volatile.Read(ref status);
        CountersText.Text = $"{(int)elapsed.Elapsed.TotalMinutes:00}:{elapsed.Elapsed.Seconds:00} · {Interlocked.Read(ref reportCount)} relatórios";
    }

    private void Mark_Click(object sender, RoutedEventArgs e)
    {
        var text = MarkerText.Text.Trim().ReplaceLineEndings(" ");
        Log($"MARCA · {(text.Length == 0 ? "Easy-Switch acionado (canal não informado)" : text)}");
        MarkerText.Clear();
        Flush();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        while (pending.Reader.TryRead(out _)) { }
        lines.Clear();
        LogBox.Clear();
        Log("Registro limpo. Contagem da captura preservada.");
        Flush();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Registro de texto (*.txt)|*.txt", FileName = $"pebblex-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Flush();
            var header = $"PebbleX · {DateTimeOffset.Now:O}{Environment.NewLine}Últimos {MaxLines} registros; horários locais; captura, consultas e alterações HID explicitamente iniciadas pelo usuário. O registro não confirma sincronização entre computadores.{Environment.NewLine}";
            File.WriteAllText(dialog.FileName, header + string.Join(Environment.NewLine, lines));
            Log("Registro salvo.");
        }
        catch (Exception exception) { Log($"Não foi possível salvar: {exception.Message}"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir PebbleX", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Encerrar PebbleX", null, (_, _) =>
        {
            exitRequested = true;
            Close();
        });
        trayIcon.Icon = System.Drawing.SystemIcons.Application;
        trayIcon.Text = "PebbleX · Flow em segundo plano";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        trayIcon.Visible = false;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (canClose) return;
        if (flowEnabled && !exitRequested)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            trayIcon.Visible = true;
            trayIcon.ShowBalloonTip(2500, "PebbleX continua ativo", "O Flow segue acompanhando as trocas. Abra o ícone na área de notificação para voltar.", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        SetRunning(false);
        await StopPairingSetupAsync();
        if (flowEnabled) await StopFlowAsync(null);
        captureCancellation?.Cancel();
        queryCancellation?.Cancel();
        analyticsCancellation?.Cancel();
        if (captureTask is Task active) await active;
        if (queryTask is Task query) await query;
        if (analyticsTask is Task analytics) await analytics;
        if (refreshTask is Task refresh) { try { await refresh; } catch (Exception) { } }
        timer.Stop();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        await Dispatcher.Yield(DispatcherPriority.Background);
        canClose = true;
        Close();
    }

    private sealed record DeviceOption(HidCollectionInfo Collection, string Description, bool IsPresent, bool BluetoothRecognized = false)
    {
        public string Label => Collection.InputReportByteLength is null || IsPresent || BluetoothRecognized
            ? Description : $"{Description} · aguardando reconexão";
    }

    private sealed class DiscoveredPeerOption(FlowDiscoveredPeer peer)
    {
        public FlowDiscoveredPeer Peer { get; set; } = peer;
        public string Label => $"{Peer.Name} · {Peer.Address} · PebbleX encontrado";
    }
}
