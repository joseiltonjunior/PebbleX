using PebbleX.Windows.Hid;

if (!TryParseArguments(args, out var vendorId))
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

static bool TryParseArguments(string[] arguments, out ushort? vendorId)
{
    vendorId = null;

    if (arguments.Length == 1 && string.Equals(arguments[0], "devices", StringComparison.Ordinal))
    {
        return true;
    }

    if (arguments.Length == 3
        && string.Equals(arguments[0], "devices", StringComparison.Ordinal)
        && string.Equals(arguments[1], "--vendor", StringComparison.Ordinal)
        && HidCollectionFilter.TryParseVendor(arguments[2], out var parsedVendorId))
    {
        vendorId = parsedVendorId;
        return true;
    }

    return false;
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