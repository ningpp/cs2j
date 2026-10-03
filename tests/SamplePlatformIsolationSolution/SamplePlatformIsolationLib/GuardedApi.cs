using System.Runtime.Versioning;

namespace SamplePlatformIsolationLib;

/// <summary>C42 fixture: OS guard attributes that must be stripped (all positions).</summary>
[SupportedOSPlatform("windows")]
public static class GuardedApi
{
    [UnsupportedOSPlatform("linux")]
    public static string Label() => "guarded";
}