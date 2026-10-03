namespace SamplePlatformIsolationLib;

/// <summary>C42 fixture: OS branch helper driven by probes (redirected to the conservative shim).</summary>
public static class OsInfo
{
    public static bool RunsOnWindows() => System.OperatingSystem.IsWindows();

    public static bool RunsOnLinux() => System.OperatingSystem.IsLinux();

    public static string Describe() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
}