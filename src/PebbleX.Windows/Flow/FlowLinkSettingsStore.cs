using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PebbleX.Windows.Flow;

public sealed record FlowLinkConfiguration(Guid LocalInstallationId, string LocalName,
    Guid? PeerInstallationId, string? PeerName, string? PeerAddress, int Port, string? SharedKeyHex)
{
    public bool IsPaired => PeerInstallationId is not null && PeerAddress is not null && SharedKeyHex is not null;
}

/// <summary>Persists the local identity and one explicitly linked peer, protecting the shared key with Windows DPAPI.</summary>
public sealed class FlowLinkSettingsStore
{
    private readonly string filePath;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FlowLinkSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PebbleX", "flow-link.dat")) { }

    internal FlowLinkSettingsStore(string filePath) => this.filePath = filePath;

    public FlowLinkConfiguration LoadOrCreate()
    {
        if (!File.Exists(filePath))
        {
            var created = new FlowLinkConfiguration(Guid.NewGuid(), Environment.MachineName, null, null, null, 47631, null);
            Save(created);
            return created;
        }

        var protectedBytes = File.ReadAllBytes(filePath);
        var json = Dpapi.Unprotect(protectedBytes);
        var settings = JsonSerializer.Deserialize<FlowLinkConfiguration>(json, JsonOptions)
            ?? throw new InvalidDataException("Flow link settings are empty.");
        Validate(settings);
        return settings;
    }

    public void Save(FlowLinkConfiguration settings)
    {
        Validate(settings);
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var protectedBytes = Dpapi.Protect(json);
        var directory = Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException("Flow settings directory is missing.");
        Directory.CreateDirectory(directory);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    public static byte[] ParseSharedKey(string value)
    {
        if (value.Length != 64) throw new FormatException("A chave de vínculo deve ter 64 caracteres hexadecimais (256 bits).");
        try { return Convert.FromHexString(value); }
        catch (FormatException exception) { throw new FormatException("A chave de vínculo deve conter somente caracteres hexadecimais.", exception); }
    }

    private static void Validate(FlowLinkConfiguration settings)
    {
        if (settings.LocalInstallationId == Guid.Empty || string.IsNullOrWhiteSpace(settings.LocalName))
            throw new InvalidDataException("Flow local installation identity is invalid.");
        if (settings.Port is < 1 or > 65535) throw new InvalidDataException("Flow peer port is outside TCP range.");
        var partiallyPaired = settings.PeerInstallationId is null || settings.PeerName is null
            || settings.PeerAddress is null || settings.SharedKeyHex is null;
        if (partiallyPaired)
        {
            if (settings.PeerInstallationId is not null || settings.PeerName is not null
                || settings.PeerAddress is not null || settings.SharedKeyHex is not null)
                throw new InvalidDataException("Flow peer settings are incomplete.");
            return;
        }
        if (settings.PeerInstallationId == Guid.Empty || settings.PeerInstallationId == settings.LocalInstallationId
            || string.IsNullOrWhiteSpace(settings.PeerName))
            throw new InvalidDataException("Flow peer identity is invalid.");
        if (!System.Net.IPAddress.TryParse(settings.PeerAddress, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("Flow peer address must be an IPv4 address.");
        _ = ParseSharedKey(settings.SharedKeyHex!);
    }

    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] value) => Transform(value, protect: true);
        public static byte[] Unprotect(byte[] value) => Transform(value, protect: false);

        private static byte[] Transform(byte[] value, bool protect)
        {
            var pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
            var input = new DataBlob(value.Length, pinned.AddrOfPinnedObject());
            try
            {
                DataBlob output;
                IntPtr description = IntPtr.Zero;
                var success = protect
                    ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (description != IntPtr.Zero) LocalFree(description);
                if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not protect Flow settings.");
                try
                {
                    var result = new byte[output.Length];
                    Marshal.Copy(output.Data, result, 0, result.Length);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally { pinned.Free(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct DataBlob(int length, IntPtr data)
        {
            public readonly int Length = length;
            public readonly IntPtr Data = data;
        }

        [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr optionalEntropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("Kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
