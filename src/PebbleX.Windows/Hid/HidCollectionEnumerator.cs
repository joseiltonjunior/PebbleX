using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PebbleX.Windows.Hid.Native;

namespace PebbleX.Windows.Hid;

public sealed class HidCollectionEnumerator
{
    public IReadOnlyList<HidCollectionInfo> Enumerate()
    {
        var interfaceClassGuid = HidNative.GetInterfaceClassGuid();
        var collections = HidNative.GetPresentHidInterfaces(interfaceClassGuid)
            .Select(CreateCollectionInfo)
            .OrderBy(collection => collection.DevicePath, StringComparer.Ordinal)
            .ToArray();

        return collections;
    }

    private static HidCollectionInfo CreateCollectionInfo(HidDeviceInterface deviceInterface)
    {
        ushort? vendorId = null;
        ushort? productId = null;
        ushort? usagePage = null;
        ushort? usage = null;
        ushort? inputReportByteLength = null;
        string? manufacturer = null;
        string? product = null;
        string? serialNumber = null;

        using SafeFileHandle handle = HidNative.OpenForDeviceQuery(deviceInterface.DevicePath);

        if (!handle.IsInvalid)
        {
            var attributes = new HiddAttributes
            {
                Size = Marshal.SizeOf<HiddAttributes>(),
            };

            if (HidNative.TryGetAttributes(handle, ref attributes))
            {
                vendorId = attributes.VendorId;
                productId = attributes.ProductId;
            }

            manufacturer = HidNative.TryGetManufacturerString(handle);
            product = HidNative.TryGetProductString(handle);
            serialNumber = HidNative.TryGetSerialNumberString(handle);

            if (HidNative.TryGetPreparsedData(handle, out var preparsedData))
            {
                try
                {
                    if (HidNative.TryGetCaps(preparsedData, out var capabilities))
                    {
                        usagePage = capabilities.UsagePage;
                        usage = capabilities.Usage;
                        inputReportByteLength = capabilities.InputReportByteLength;
                    }
                }
                finally
                {
                    HidNative.FreePreparsedData(preparsedData);
                }
            }
        }

        return new HidCollectionInfo(
            deviceInterface.DevicePath,
            vendorId,
            productId,
            usagePage,
            usage,
            inputReportByteLength,            manufacturer,
            product,
            serialNumber,
            deviceInterface.DeviceInstanceId,
            deviceInterface.ParentDeviceInstanceId,
            deviceInterface.DeviceContainerId);
    }
}