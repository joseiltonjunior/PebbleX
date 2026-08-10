using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using PebbleX.Windows.Hid.Native;

namespace PebbleX.Windows.Hid;

public sealed class HidInputMonitor
{
    public async Task MonitorAsync(
        HidCollectionInfo collection,
        Action<HidInputReport> onReport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(onReport);

        if (collection.InputReportByteLength is not ushort inputReportByteLength || inputReportByteLength == 0)
        {
            throw new InvalidOperationException("The selected collection does not expose a usable input report length.");
        }

        using SafeFileHandle handle = HidNative.OpenForInputReports(collection.DevicePath);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW could not open the selected HID collection for input reports.");
        }

        using var stream = new FileStream(handle, FileAccess.Read, inputReportByteLength, isAsync: true);
        var buffer = new byte[inputReportByteLength];

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            var report = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, report, 0, bytesRead);
            onReport(new HidInputReport(DateTimeOffset.Now, report));
        }
    }
}