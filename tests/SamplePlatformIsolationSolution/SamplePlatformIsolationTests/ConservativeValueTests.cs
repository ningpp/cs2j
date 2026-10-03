using SamplePlatformIsolationLib;
using Xunit;

namespace SamplePlatformIsolationTests;

/// <summary>
/// C42 M2 gate: after Stub-mode isolation the probe call sites route to the
/// conservative Cs2jPlatformProbe shim (false / "Unknown"), so these runtime
/// assertions hold on the intermediate tree regardless of the host OS.
/// </summary>
public class ConservativeValueTests
{
    [Fact]
    public void WindowsProbe_IsConservative()
        => Assert.False(OsInfo.RunsOnWindows());

    [Fact]
    public void LinuxProbe_IsConservative()
        => Assert.False(OsInfo.RunsOnLinux());

    [Fact]
    public void OsDescription_IsNeutral()
        => Assert.Equal("Unknown", OsInfo.Describe());
}