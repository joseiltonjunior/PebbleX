using System.Globalization;

namespace PebbleX.Windows.Hid;

public static class HidCollectionFilter
{
    public static bool TryParseVendor(string value, out ushort vendorId)
    {
        vendorId = 0;

        return value.Length == 4
            && ushort.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vendorId);
    }

    public static bool MatchesVendor(HidCollectionInfo collection, ushort? vendorId)
    {
        return !vendorId.HasValue || collection.VendorId == vendorId.Value;
    }
}