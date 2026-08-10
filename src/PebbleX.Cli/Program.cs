using PebbleX.Cli;
using PebbleX.Windows.Hid;

if (args.Length > 0 && string.Equals(args[0], "devices", StringComparison.Ordinal))
{
    return ListDevices(args);
}

if (MonitorCommandParser.TryParse(args, out var monitorCommand))
{
    return await MonitorCollectionAsync(monitorCommand);
}

Console.Error.WriteLine("Usage: pebbleX development CLI devices [--vendor VVVV]");
Console.Error.WriteLine("       pebbleX development CLI monitor --vendor VVVV --product PPPP --usage-page UUUU --usage UUUU --duration N");
return 1;

static int ListDevices(string[] arguments)
{
    ushort? vendorId = null;

    if (arguments.Length == 3
        && string.Equals(arguments[1], "--vendor", StringComparison.Ordinal)
        && HidCollectionFilter.TryParseVendor(arguments[2], out var parsedVendorId))
    {
        vendorId = parsedVendorId;
    }
    else if (arguments.Length != 1)
    {
        Console.Error.WriteLine("Usage: pebbleX development CLI devices [--vendor VVVV]");
        return 1;
    }

    var collections = new HidCollectionEnumerator()
        .Enumerate()
        .Where(collection => HidCollectionFilter.MatchesVendor(collection, vendorId))
        .ToArray();

    Console.WriteLine($"HID collections: {collections.Length}");

    foreach (var collection in collections)
    {
        Console.WriteLine();
        Console.WriteLine($"Device path: {collection.DevicePath}");
        Console.WriteLine($"Vendor ID: {FormatIdentifier(collection.VendorId)}");
        Console.WriteLine($"Product ID: {FormatIdentifier(collection.ProductId)}");
        Console.WriteLine($"Usage page: {FormatIdentifier(collection.UsagePage)}");
        Console.WriteLine($"Usage: {FormatIdentifier(collection.Usage)}");
        Console.WriteLine($"Manufacturer: {FormatText(collection.Manufacturer)}");
        Console.WriteLine($"Product: {FormatText(collection.Product)}");
        Console.WriteLine($"Serial number: {FormatText(collection.SerialNumber)}");
        Console.WriteLine($"Device instance ID: {FormatText(collection.DeviceInstanceId)}");
        Console.WriteLine($"Parent device instance ID: {FormatText(collection.ParentDeviceInstanceId)}");
        Console.WriteLine($"Device container ID: {FormatContainerId(collection.DeviceContainerId)}");
    }

    return 0;
}

static async Task<int> MonitorCollectionAsync(MonitorCommand command)
{
    var matches = HidCollectionSelector.Select(new HidCollectionEnumerator().Enumerate(), command.Criteria);

    if (matches.Count == 0)
    {
        Console.Error.WriteLine("No HID collection matched the requested vendor, product, usage page, and usage.");
        return 2;
    }

    if (matches.Count > 1)
    {
        Console.Error.WriteLine($"{matches.Count} HID collections matched. Refine the selection; no collection was opened.");
        return 2;
    }

    var collection = matches[0];

    if (collection.UsagePage == 0x0001 && collection.Usage == 0x0006)
    {
        Console.Error.WriteLine("Monitoring a keyboard collection is prohibited.");
        return 2;
    }

    using var cancellationSource = new CancellationTokenSource(command.Duration);
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationSource.Cancel();
    };

    Console.CancelKeyPress += cancelHandler;
    var reportCount = 0;

    try
    {
        Console.WriteLine($"Monitoring one HID collection for {command.Duration.TotalSeconds:0} seconds. Press Ctrl+C to stop.");
        await new HidInputMonitor().MonitorAsync(
            collection,
            report =>
            {
                reportCount++;
                Console.WriteLine($"{report.Timestamp:HH:mm:ss.fff} RX [{report.Bytes.Length}] {HidReportFormatter.FormatHex(report.Bytes)}");
            },
            cancellationSource.Token);
    }
    catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
    {
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }

    Console.WriteLine($"Monitoring complete. Reports received: {reportCount}");
    return 0;
}

static string FormatIdentifier(ushort? value)
{
    return value is ushort identifier ? $"0x{identifier:X4}" : "unknown";
}

static string FormatContainerId(Guid? value)
{
    return value?.ToString("D") ?? "unknown";
}

static string FormatText(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}