using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RigControl.Core;

/// <summary>
/// A HID handle for feature reports only. Opened with no read/write access, like hidapi does, so it works on
/// collections Windows keeps for itself (a mouse's pointer interface), where a normal open is refused.
/// </summary>
sealed class FeatureHandle : IDisposable
{
    readonly SafeFileHandle _handle;

    FeatureHandle(SafeFileHandle handle) => _handle = handle;

    public static FeatureHandle? Open(string devicePath)
    {
        var h = CreateFile(devicePath, 0, 3 /* share read|write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
        return h.IsInvalid ? null : new FeatureHandle(h);
    }

    /// <summary>Byte 0 of the buffer is the report id.</summary>
    public void SetFeature(byte[] report)
    {
        if (!HidD_SetFeature(_handle, report, report.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void GetFeature(byte[] report)
    {
        if (!HidD_GetFeature(_handle, report, report.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
}
