namespace PebbleX.Windows.Hid;

public sealed record HidCollectionInfo(
    string DevicePath,
    ushort? VendorId,
    ushort? ProductId,
    ushort? UsagePage,
    ushort? Usage,
    ushort? InputReportByteLength,
    string? Manufacturer,
    string? Product,
    string? SerialNumber,
    string? DeviceInstanceId,
    string? ParentDeviceInstanceId,
    Guid? DeviceContainerId);