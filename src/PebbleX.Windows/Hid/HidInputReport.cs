namespace PebbleX.Windows.Hid;

public sealed record HidInputReport(DateTimeOffset Timestamp, byte[] Bytes);