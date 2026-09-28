using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PebbleX.Windows.Hid;

/// <summary>Bluetooth HID service nodes recognized by Windows, including devices without an active HID interface.</summary>
public sealed record HidBluetoothDevice(ushort ProductId, string InstanceId);

public sealed partial class HidBluetoothInventory
{
    private const uint CrSuccess = 0;
    private const uint FilterEnumerator = 0x00000001;
    private const uint FilterPresent = 0x00000100;
    private const string ServicePrefix = "BTHLEDEVICE\\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&";

    public IReadOnlyList<HidBluetoothDevice> Enumerate()
    {
        const uint flags = FilterEnumerator | FilterPresent;
        var sizeResult = CM_Get_Device_ID_List_Size(out var required, "BTHLEDEVICE", flags);
        if (sizeResult != CrSuccess) throw new Win32Exception((int)sizeResult, "Não foi possível consultar os serviços Bluetooth HID.");
        if (required is 0 or > 65536) return [];
        var buffer = new char[required];
        var listResult = CM_Get_Device_ID_List("BTHLEDEVICE", buffer, (uint)buffer.Length, flags);
        if (listResult != CrSuccess) throw new Win32Exception((int)listResult, "Não foi possível listar os serviços Bluetooth HID.");

        var results = new List<HidBluetoothDevice>();
        var start = 0;
        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != '\0') continue;
            if (index == start) break;
            var id = new string(buffer, start, index - start);
            if (TryParse(id, out var product)) results.Add(new HidBluetoothDevice(product, id));
            start = index + 1;
        }
        return results;
    }

    internal static bool TryParse(string instanceId, out ushort productId)
    {
        productId = 0;
        if (!instanceId.StartsWith(ServicePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var start = ServicePrefix.Length;
        return instanceId.Length > start + 4 && instanceId[start + 4] == '_'
            && ushort.TryParse(instanceId.AsSpan(start, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out productId);
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List(string filter, [Out] char[] buffer, uint bufferLength, uint flags);
}
