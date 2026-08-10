namespace PebbleX.Windows.Hid;

public sealed record HidCollectionInfo(
    string DevicePath,
    ushort? VendorId,
    ushort? ProductId,
    ushort? UsagePage,
    ushort? Usage);