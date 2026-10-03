namespace SamplePlatformIsolationLib;

/// <summary>
/// C42 fixture: platform service type in a member signature (D5 port). The
/// platform type is intentionally unresolved (System.IO.Ports is not referenced):
/// the pass detects it via the text fallback and ports the signature to object.
/// </summary>
public static class PlatformWrappers
{
    public static void Write(System.IO.Ports.SerialPort port, string text)
    {
        System.Console.WriteLine(text);
    }
}