using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PebbleX.Windows.Hid.Native;

namespace PebbleX.Windows.Hid;

public sealed class HidCollectionEnumerator
{
    public IReadOnlyList<HidCollectionInfo> Enumerate()
    {
        var interfaceClassGuid = HidNative.GetInterfaceClassGuid();
        var collections = HidNative.GetPresentInterfacePaths(interfaceClassGuid)
            .Select(CreateCollectionInfo)
            .OrderBy(collection => collection.DevicePath, StringComparer.Ordinal)
            .ToArray();

        return collections;
    }

    private static HidCollectionInfo CreateCollectionInfo(string devicePath)
    {
        ushort? vendorId = null;
        ushort? productId = null;
        ushort? usagePage = null;
        ushort? usage = null;

        using SafeFileHandle handle = HidNative.OpenForDeviceQuery(devicePath);

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

            if (HidNative.TryGetPreparsedData(handle, out var preparsedData))
            {
                try
                {
                    if (HidNative.TryGetCaps(preparsedData, out var capabilities))
                    {
                        usagePage = capabilities.UsagePage;
                        usage = capabilities.Usage;
                    }
                }
                finally
                {
                    HidNative.FreePreparsedData(preparsedData);
                }
            }
        }

        return new HidCollectionInfo(devicePath, vendorId, productId, usagePage, usage);
    }
}