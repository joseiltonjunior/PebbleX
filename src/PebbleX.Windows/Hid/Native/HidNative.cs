using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PebbleX.Windows.Hid.Native;

internal static partial class HidNative
{
    private const uint CrSuccess = 0;
    private const uint CrBufferSmall = 26;
    private const uint CmLocateDevNodeNormal = 0;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorNoMoreItems = 259;
    private const uint DevPropTypeGuid = 0x0000000D;
    private const int HidpStatusSuccess = 0x00110000;
    private const uint HidStringBufferLengthInBytes = 1024;
    private static readonly DevPropKey DeviceContainerIdProperty = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        2);

    internal static Guid GetInterfaceClassGuid()
    {
        HidD_GetHidGuid(out var interfaceClassGuid);
        return interfaceClassGuid;
    }

    internal static IReadOnlyList<HidDeviceInterface> GetPresentHidInterfaces(Guid interfaceClassGuid)
    {
        using var deviceInfoSet = SafeDeviceInfoSetHandle.FromRawHandle(SetupDiGetClassDevs(
            in interfaceClassGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface));

        if (deviceInfoSet.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevsW failed.");
        }

        var interfaces = new List<HidDeviceInterface>();

        for (uint index = 0; ; index++)
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                Size = Marshal.SizeOf<SpDeviceInterfaceData>(),
            };

            if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, in interfaceClassGuid, index, ref interfaceData))
            {
                var error = Marshal.GetLastWin32Error();

                if (error == ErrorNoMoreItems)
                {
                    break;
                }

                throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed.");
            }

            interfaces.Add(GetDeviceInterface(deviceInfoSet, interfaceData));
        }

        return interfaces;
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

    internal static string? TryGetManufacturerString(SafeFileHandle handle)
    {
        return TryGetHidString(handle, HidD_GetManufacturerString);
    }

    internal static string? TryGetProductString(SafeFileHandle handle)
    {
        return TryGetHidString(handle, HidD_GetProductString);
    }

    internal static string? TryGetSerialNumberString(SafeFileHandle handle)
    {
        return TryGetHidString(handle, HidD_GetSerialNumberString);
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

    private static HidDeviceInterface GetDeviceInterface(SafeDeviceInfoSetHandle deviceInfoSet, SpDeviceInterfaceData interfaceData)
    {
        var deviceInfoData = new SpDevInfoData
        {
            Size = Marshal.SizeOf<SpDevInfoData>(),
        };

        _ = SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out var requiredSize,
            ref deviceInfoData);

        var sizeError = Marshal.GetLastWin32Error();

        if (requiredSize == 0 || sizeError != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(sizeError, "SetupDiGetDeviceInterfaceDetailW could not determine the required buffer size.");
        }

        var detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));

        try
        {
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
            deviceInfoData = new SpDevInfoData
            {
                Size = Marshal.SizeOf<SpDevInfoData>(),
            };

            if (!SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    detailBuffer,
                    requiredSize,
                    out _,
                    ref deviceInfoData))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetailW failed.");
            }

            var devicePath = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, sizeof(int)));

            if (string.IsNullOrEmpty(devicePath))
            {
                throw new InvalidOperationException("SetupDiGetDeviceInterfaceDetailW returned an empty device path.");
            }

            var deviceInstanceId = TryGetDeviceInstanceId(deviceInfoData.DevInst);
            var parentDeviceInstanceId = TryGetParentDeviceInstanceId(deviceInfoData.DevInst);
            var deviceContainerId = TryGetDeviceContainerId(deviceInfoData.DevInst);

            return new HidDeviceInterface(
                devicePath,
                deviceInstanceId,
                parentDeviceInstanceId,
                deviceContainerId);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static string? TryGetDeviceInstanceId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out var characterCount, devInst, 0) != CrSuccess)
        {
            return null;
        }

        var buffer = new char[checked((int)characterCount + 1)];
        return CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) == CrSuccess
            ? ToOptionalString(new string(buffer).TrimEnd('\0'))
            : null;
    }

    private static string? TryGetParentDeviceInstanceId(uint devInst)
    {
        if (CM_Get_Parent(out var parentDevInst, devInst, 0) != CrSuccess)
        {
            return null;
        }

        return TryGetDeviceInstanceId(parentDevInst);
    }

    private static Guid? TryGetDeviceContainerId(uint devInst)
    {
        uint propertyType;
        uint propertySize = 0;
        var sizeResult = CM_Get_DevNode_Property(
            devInst,
            in DeviceContainerIdProperty,
            out propertyType,
            IntPtr.Zero,
            ref propertySize,
            0);

        if (sizeResult != CrBufferSmall || propertySize != Marshal.SizeOf<Guid>())
        {
            return null;
        }

        var propertyBuffer = Marshal.AllocHGlobal(checked((int)propertySize));

        try
        {
            var propertyResult = CM_Get_DevNode_Property(
                devInst,
                in DeviceContainerIdProperty,
                out propertyType,
                propertyBuffer,
                ref propertySize,
                0);

            return propertyResult == CrSuccess && propertyType == DevPropTypeGuid
                ? Marshal.PtrToStructure<Guid>(propertyBuffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(propertyBuffer);
        }
    }

    private static string? TryGetHidString(SafeFileHandle handle, HidStringQuery query)
    {
        var buffer = Marshal.AllocHGlobal(checked((int)HidStringBufferLengthInBytes));

        try
        {
            Marshal.Copy(new byte[HidStringBufferLengthInBytes], 0, buffer, checked((int)HidStringBufferLengthInBytes));

            return query(handle, buffer, HidStringBufferLengthInBytes)
                ? ToOptionalString(Marshal.PtrToStringUni(buffer))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ToOptionalString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static bool DestroyDeviceInfoList(IntPtr handle)
    {
        return SetupDiDestroyDeviceInfoList(handle);
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupDiGetClassDevs(
        in Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(
        SafeDeviceInfoSetHandle deviceInfoSet,
        IntPtr deviceInfoData,
        in Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevInfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    private static partial uint CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID(uint devInst, [Out] char[] buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(
        uint devInst,
        in DevPropKey propertyKey,
        out uint propertyType,
        IntPtr propertyBuffer,
        ref uint propertyBufferSize,
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
    private static partial bool HidD_GetManufacturerString(SafeFileHandle hidDeviceObject, IntPtr buffer, uint bufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetProductString(SafeFileHandle hidDeviceObject, IntPtr buffer, uint bufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetSerialNumberString(SafeFileHandle hidDeviceObject, IntPtr buffer, uint bufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);
}

internal sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeDeviceInfoSetHandle()
        : base(ownsHandle: true)
    {
    }

    internal static SafeDeviceInfoSetHandle FromRawHandle(IntPtr rawHandle)
    {
        var handle = new SafeDeviceInfoSetHandle();
        handle.SetHandle(rawHandle);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        return HidNative.DestroyDeviceInfoList(handle);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpDeviceInterfaceData
{
    internal int Size;
    internal Guid InterfaceClassGuid;
    internal int Flags;
    internal IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpDevInfoData
{
    internal int Size;
    internal Guid ClassGuid;
    internal uint DevInst;
    internal IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct DevPropKey
{
    internal DevPropKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    internal Guid FormatId { get; }

    internal uint PropertyId { get; }
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

internal delegate bool HidStringQuery(SafeFileHandle handle, IntPtr buffer, uint bufferLength);

internal sealed record HidDeviceInterface(
    string DevicePath,
    string? DeviceInstanceId,
    string? ParentDeviceInstanceId,
    Guid? DeviceContainerId);