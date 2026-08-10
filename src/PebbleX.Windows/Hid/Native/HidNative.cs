using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PebbleX.Windows.Hid.Native;

internal static partial class HidNative
{
    private const uint CrSuccess = 0;
    private const uint CrBufferSmall = 26;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int HidpStatusSuccess = 0x00110000;

    internal static Guid GetInterfaceClassGuid()
    {
        HidD_GetHidGuid(out var interfaceClassGuid);
        return interfaceClassGuid;
    }

    internal static IReadOnlyList<string> GetPresentInterfacePaths(Guid interfaceClassGuid)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sizeResult = CM_Get_Device_Interface_List_Size(
                out var characterCount,
                in interfaceClassGuid,
                null,
                CmGetDeviceInterfaceListPresent);

            if (sizeResult != CrSuccess)
            {
                throw new InvalidOperationException($"CM_Get_Device_Interface_List_SizeW failed with CONFIGRET {sizeResult}.");
            }

            if (characterCount == 0)
            {
                return Array.Empty<string>();
            }

            var buffer = new char[checked((int)characterCount)];
            var listResult = CM_Get_Device_Interface_List(
                in interfaceClassGuid,
                null,
                buffer,
                characterCount,
                CmGetDeviceInterfaceListPresent);

            if (listResult == CrSuccess)
            {
                return HidDevicePathParser.Parse(buffer);
            }

            if (listResult != CrBufferSmall)
            {
                throw new InvalidOperationException($"CM_Get_Device_Interface_ListW failed with CONFIGRET {listResult}.");
            }
        }

        throw new InvalidOperationException("The HID interface list changed repeatedly while it was being enumerated.");
    }

    internal static SafeFileHandle OpenForDeviceQuery(string devicePath)
    {
        return CreateFile(
            devicePath,
            desiredAccess: 0,
            shareMode: FileShareRead | FileShareWrite,
            securityAttributes: IntPtr.Zero,
            creationDisposition: OpenExisting,
            flagsAndAttributes: 0,
            templateFile: IntPtr.Zero);
    }

    internal static bool TryGetAttributes(SafeFileHandle handle, ref HiddAttributes attributes)
    {
        return HidD_GetAttributes(handle, ref attributes);
    }

    internal static bool TryGetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData)
    {
        return HidD_GetPreparsedData(handle, out preparsedData);
    }

    internal static bool TryGetCaps(IntPtr preparsedData, out HidpCaps capabilities)
    {
        return HidP_GetCaps(preparsedData, out capabilities) == HidpStatusSuccess;
    }

    internal static void FreePreparsedData(IntPtr preparsedData)
    {
        _ = HidD_FreePreparsedData(preparsedData);
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_Size(
        out uint length,
        in Guid interfaceClassGuid,
        string? deviceId,
        uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List(
        in Guid interfaceClassGuid,
        string? deviceId,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);
}

[StructLayout(LayoutKind.Sequential)]
internal struct HiddAttributes
{
    internal int Size;
    internal ushort VendorId;
    internal ushort ProductId;
    internal ushort VersionNumber;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HidpCaps
{
    internal ushort Usage;
    internal ushort UsagePage;
    internal ushort InputReportByteLength;
    internal ushort OutputReportByteLength;
    internal ushort FeatureReportByteLength;
    internal ushort Reserved01;
    internal ushort Reserved02;
    internal ushort Reserved03;
    internal ushort Reserved04;
    internal ushort Reserved05;
    internal ushort Reserved06;
    internal ushort Reserved07;
    internal ushort Reserved08;
    internal ushort Reserved09;
    internal ushort Reserved10;
    internal ushort Reserved11;
    internal ushort Reserved12;
    internal ushort Reserved13;
    internal ushort Reserved14;
    internal ushort Reserved15;
    internal ushort Reserved16;
    internal ushort Reserved17;
    internal ushort NumberLinkCollectionNodes;
    internal ushort NumberInputButtonCaps;
    internal ushort NumberInputValueCaps;
    internal ushort NumberInputDataIndices;
    internal ushort NumberOutputButtonCaps;
    internal ushort NumberOutputValueCaps;
    internal ushort NumberOutputDataIndices;
    internal ushort NumberFeatureButtonCaps;
    internal ushort NumberFeatureValueCaps;
    internal ushort NumberFeatureDataIndices;
}