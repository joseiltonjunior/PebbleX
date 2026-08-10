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
}