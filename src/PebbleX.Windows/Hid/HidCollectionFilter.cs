using System.Globalization;

namespace PebbleX.Windows.Hid;

public static class HidCollectionFilter
{
    public static bool TryParseVendor(string value, out ushort vendorId)
    {
        return TryParseHexIdentifier(value, out vendorId);
    }

    public static bool TryParseHexIdentifier(string value, out ushort identifier)
    {
        identifier = 0;

        return value.Length == 4
            && ushort.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out identifier);
    }

    public static bool MatchesVendor(HidCollectionInfo collection, ushort? vendorId)
    {
        return !vendorId.HasValue || collection.VendorId == vendorId.Value;
    }
}

public sealed record HidCollectionSelectionCriteria(
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage);

public static class HidCollectionSelector
{
    public static IReadOnlyList<HidCollectionInfo> Select(
        IEnumerable<HidCollectionInfo> collections,
        HidCollectionSelectionCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(criteria);

        return collections
            .Where(collection =>
                collection.VendorId == criteria.VendorId
                && collection.ProductId == criteria.ProductId
                && collection.UsagePage == criteria.UsagePage
                && collection.Usage == criteria.Usage)
            .OrderBy(collection => collection.DevicePath, StringComparer.Ordinal)
            .ToArray();
    }
}