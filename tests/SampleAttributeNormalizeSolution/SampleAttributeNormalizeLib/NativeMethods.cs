using System.Runtime.InteropServices;

namespace SampleAttributeNormalizeLib;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetCurrentProcess();
}
