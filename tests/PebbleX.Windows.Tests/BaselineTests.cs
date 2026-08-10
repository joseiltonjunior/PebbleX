using PebbleX.Cli;
using PebbleX.Windows.Hid;

namespace PebbleX.Windows.Tests;

public sealed class BaselineTests
{
    [Fact]
    public void TestHostIsAvailable()
    {
        Assert.True(true);
    }

    [Fact]
    public void TryParseVendor_AcceptsFourHexDigits()
    {
        var parsed = HidCollectionFilter.TryParseVendor("046D", out var vendorId);

        Assert.True(parsed);
        Assert.Equal(0x046D, vendorId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("46D")]
    [InlineData("046D0")]
    [InlineData("logi")]
    public void TryParseVendor_RejectsInvalidInput(string value)
    {
        Assert.False(HidCollectionFilter.TryParseVendor(value, out _));
    }

    [Fact]
    public void MonitorCommandParser_AcceptsStrictSelection()
    {
        var parsed = MonitorCommandParser.TryParse(
            ["monitor", "--vendor", "046D", "--product", "B377", "--usage-page", "FF43", "--usage", "0202", "--duration", "30"],
            out var command);

        Assert.True(parsed);
        Assert.NotNull(command);
        Assert.Equal(0x046D, command.Criteria.VendorId);
        Assert.Equal(0xB377, command.Criteria.ProductId);
        Assert.Equal(0xFF43, command.Criteria.UsagePage);
        Assert.Equal(0x0202, command.Criteria.Usage);
        Assert.Equal(TimeSpan.FromSeconds(30), command.Duration);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("thirty")]
    public void MonitorCommandParser_RejectsInvalidDuration(string duration)
    {
        var parsed = MonitorCommandParser.TryParse(
            ["monitor", "--vendor", "046D", "--product", "B377", "--usage-page", "FF43", "--usage", "0202", "--duration", duration],
            out _);

        Assert.False(parsed);
    }

    [Fact]
    public void HidCollectionSelector_ReturnsTheExactMatch()
    {
        var matching = CreateCollection(0x046D, 0xB377, 0xFF43, 0x0202, "matching");
        var collections = new[]
        {
            CreateCollection(0x046D, 0xB377, 0xFF43, 0x0201, "different-usage"),
            matching,
            CreateCollection(0x046D, 0xC548, 0xFF43, 0x0202, "different-product"),
        };

        var matches = HidCollectionSelector.Select(
            collections,
            new HidCollectionSelectionCriteria(0x046D, 0xB377, 0xFF43, 0x0202));

        var selected = Assert.Single(matches);
        Assert.Equal("matching", selected.DevicePath);
    }

    [Fact]
    public void HidCollectionSelector_ReturnsNoMatchWhenCriteriaDoNotMatch()
    {
        var matches = HidCollectionSelector.Select(
            [CreateCollection(0x046D, 0xB377, 0xFF43, 0x0202, "only")],
            new HidCollectionSelectionCriteria(0x046D, 0xB377, 0xFF43, 0x0201));

        Assert.Empty(matches);
    }

    [Fact]
    public void HidCollectionSelector_PreservesMultipleMatches()
    {
        var matches = HidCollectionSelector.Select(
            [
                CreateCollection(0x046D, 0xB377, 0xFF43, 0x0202, "first"),
                CreateCollection(0x046D, 0xB377, 0xFF43, 0x0202, "second"),
            ],
            new HidCollectionSelectionCriteria(0x046D, 0xB377, 0xFF43, 0x0202));

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void HidReportFormatter_FormatsBytesAsSpacedUppercaseHex()
    {
        Assert.Equal("11 FF 00", HidReportFormatter.FormatHex([0x11, 0xFF, 0x00]));
    }

    private static HidCollectionInfo CreateCollection(ushort vendorId, ushort productId, ushort usagePage, ushort usage, string devicePath)
    {
        return new HidCollectionInfo(
            devicePath,
            vendorId,
            productId,
            usagePage,
            usage,
            20,
            null,
            null,
            null,
            null,
            null,
            null);
    }
}