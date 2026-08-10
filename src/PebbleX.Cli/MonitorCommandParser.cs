using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PebbleX.Windows.Hid;

namespace PebbleX.Cli;

internal sealed record MonitorCommand(HidCollectionSelectionCriteria Criteria, TimeSpan Duration);

internal static class MonitorCommandParser
{
    internal static bool TryParse(string[] arguments, [NotNullWhen(true)] out MonitorCommand? command)
    {
        command = null;

        if (arguments.Length != 11 || !string.Equals(arguments[0], "monitor", StringComparison.Ordinal))
        {
            return false;
        }

        ushort? vendorId = null;
        ushort? productId = null;
        ushort? usagePage = null;
        ushort? usage = null;
        int? durationSeconds = null;

        for (var index = 1; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            var value = arguments[index + 1];

            switch (option)
            {
                case "--vendor" when !vendorId.HasValue && HidCollectionFilter.TryParseHexIdentifier(value, out var parsedVendorId):
                    vendorId = parsedVendorId;
                    break;
                case "--product" when !productId.HasValue && HidCollectionFilter.TryParseHexIdentifier(value, out var parsedProductId):
                    productId = parsedProductId;
                    break;
                case "--usage-page" when !usagePage.HasValue && HidCollectionFilter.TryParseHexIdentifier(value, out var parsedUsagePage):
                    usagePage = parsedUsagePage;
                    break;
                case "--usage" when !usage.HasValue && HidCollectionFilter.TryParseHexIdentifier(value, out var parsedUsage):
                    usage = parsedUsage;
                    break;
                case "--duration" when !durationSeconds.HasValue
                    && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDuration)
                    && parsedDuration > 0:
                    durationSeconds = parsedDuration;
                    break;
                default:
                    return false;
            }
        }

        if (!vendorId.HasValue || !productId.HasValue || !usagePage.HasValue || !usage.HasValue || !durationSeconds.HasValue)
        {
            return false;
        }

        command = new MonitorCommand(
            new HidCollectionSelectionCriteria(vendorId.Value, productId.Value, usagePage.Value, usage.Value),
            TimeSpan.FromSeconds(durationSeconds.Value));
        return true;
    }
}