using System.Runtime.InteropServices;

namespace SamplePlatformIsolationInteropLib;

/// <summary>
/// C42 fixture: the conventional P/Invoke container. The M2 inventory registers
/// this file (no longer a silent filename escape), so CS2J3203/3201 must appear
/// in the report and Reject mode must fail fast (design §7.4).
/// </summary>
internal static class NativeMethods
{
    [DllImport("kernel32.dll")]
    internal static extern bool Beep(uint frequency, uint duration);
}