global using System.IO.Ports;
using SP = System.IO.Ports.SerialPort;

namespace SamplePlatformIsolationInteropLib;

/// <summary>
/// C42 fixture: platform identity must be captured semantically, independent of
/// the using form (global using vs alias) — design §7.2.
/// </summary>
public static class Ports
{
    public static void UseAlias(SP port)
    {
    }

    public static void UseGlobal(SerialPort port)
    {
    }
}