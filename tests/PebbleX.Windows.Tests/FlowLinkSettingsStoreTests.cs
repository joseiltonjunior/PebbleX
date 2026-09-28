using PebbleX.Windows.Flow;

namespace PebbleX.Windows.Tests;

public sealed class FlowLinkSettingsStoreTests
{
    [Fact]
    public void ProtectsAndRestoresPairedFlowSettingsForCurrentWindowsUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PebbleX.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "flow-link.dat");
        try
        {
            var store = new FlowLinkSettingsStore(file);
            var initial = store.LoadOrCreate();
            var paired = initial with
            {
                PeerInstallationId = Guid.NewGuid(),
                PeerName = "PC secundário",
                PeerAddress = "192.168.1.20",
                SharedKeyHex = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            };

            store.Save(paired);
            var restored = store.LoadOrCreate();

            Assert.True(restored.IsPaired);
            Assert.Equal(paired, restored);
            Assert.NotEqual(System.Text.Json.JsonSerializer.Serialize(paired), File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParsesOnlyA256BitHexPairingKey()
    {
        var key = FlowLinkSettingsStore.ParseSharedKey(new string('A', 64));

        Assert.Equal(32, key.Length);
        Assert.Throws<FormatException>(() => FlowLinkSettingsStore.ParseSharedKey("short"));
        Assert.Throws<FormatException>(() => FlowLinkSettingsStore.ParseSharedKey(new string('Z', 64)));
    }

    [Fact]
    public void RejectsPeerAddressThatListenerCannotBindTo()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PebbleX.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new FlowLinkSettingsStore(Path.Combine(directory, "flow-link.dat"));
            var initial = store.LoadOrCreate();
            var invalid = initial with
            {
                PeerInstallationId = Guid.NewGuid(), PeerName = "IPv6 peer", PeerAddress = "::1",
                SharedKeyHex = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            };
            Assert.Throws<InvalidDataException>(() => store.Save(invalid));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
