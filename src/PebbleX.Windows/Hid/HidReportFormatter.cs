namespace PebbleX.Windows.Hid;

public static class HidReportFormatter
{
    public static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        return string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2")));
    }
}