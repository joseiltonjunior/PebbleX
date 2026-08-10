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
    public void Parse_StopsAtTheMultiStringTerminator()
    {
        var paths = HidDevicePathParser.Parse("path-one\0path-two\0\0ignored".ToCharArray());

        Assert.Equal(["path-one", "path-two"], paths);
    }
}