using PebbleX.Windows.Hid;

if (args.Length != 1 || !string.Equals(args[0], "devices", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: pebbleX development CLI devices");
    return 1;
}

var collections = new HidCollectionEnumerator().Enumerate();

Console.WriteLine($"HID collections: {collections.Count}");

foreach (var collection in collections)
{
    Console.WriteLine();
    Console.WriteLine($"Device path: {collection.DevicePath}");
    Console.WriteLine($"Vendor ID: {FormatIdentifier(collection.VendorId)}");
    Console.WriteLine($"Product ID: {FormatIdentifier(collection.ProductId)}");
    Console.WriteLine($"Usage page: {FormatIdentifier(collection.UsagePage)}");
    Console.WriteLine($"Usage: {FormatIdentifier(collection.Usage)}");
}

return 0;

static string FormatIdentifier(ushort? value)
{
    return value is ushort identifier ? $"0x{identifier:X4}" : "unknown";
}