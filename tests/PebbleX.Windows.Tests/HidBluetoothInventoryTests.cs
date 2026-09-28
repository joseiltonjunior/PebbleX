using PebbleX.Windows.Hid;

namespace PebbleX.Windows.Tests;

public sealed class HidBluetoothInventoryTests
{
    [Theory]
    [InlineData("BTHLEDEVICE\\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B034_REV&0006_D67D8AEB2790\\8&2BDDC5DF&0&001F", 0xB034)]
    [InlineData("BTHLEDEVICE\\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B377_REV&0011_DDCA26B2AB5B\\8&30A2F090&0&001F", 0xB377)]
    public void IdentifiesOnlyLogitechBluetoothHidService(string id, int expected)
    {
        Assert.True(HidBluetoothInventory.TryParse(id, out var product));
        Assert.Equal(expected, product);
        Assert.False(HidBluetoothInventory.TryParse(id.Replace("02046D", "020123", StringComparison.Ordinal), out _));
        Assert.False(HidBluetoothInventory.TryParse(id.Replace("00001812", "00001801", StringComparison.Ordinal), out _));
    }
}
