using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# IPAddress.Loopback is mapped to InetAddress.getLoopbackAddress()
/// and IPAddress.IPv6Loopback is mapped to IPAddressHelper.ipv6Loopback() in Java.
/// Reproduces: IdnCheckHostNameTest returns Unknown instead of IPv6 because
/// IPv6Loopback was incorrectly mapped to InetAddress.getLoopbackAddress() (IPv4).
/// Also tests that IPAddress.ToString() is mapped to IPAddressHelper.toString()
/// which handles IPv6 address compression (Java returns "0:0:0:0:0:0:0:1" but C# returns "::1").
/// </summary>
public class IPAddressLoopbackMappingTests
{
    [Fact]
    public void IPAddress_Loopback_MappedToGetLoopbackAddress()
    {
        var result = Convert(@"
using System.Net;
class Test
{
    void M()
    {
        var addr = IPAddress.Loopback;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("InetAddress.Loopback", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getLoopbackAddress", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IPAddress_IPv6Loopback_MappedToIPAddressHelperIpv6Loopback()
    {
        var result = Convert(@"
using System.Net;
class Test
{
    void M()
    {
        var addr = IPAddress.IPv6Loopback;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("InetAddress.IPv6Loopback", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("InetAddress.getLoopbackAddress", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IPAddressHelper.ipv6Loopback()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IPAddress_Loopback_ToString_MappedCorrectly()
    {
        var result = Convert(@"
using System.Net;
class Test
{
    string M()
    {
        return IPAddress.Loopback.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("InetAddress.Loopback", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IPAddressHelper.toString", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IPAddress_IPv6Loopback_ToString_MappedCorrectly()
    {
        var result = Convert(@"
using System.Net;
class Test
{
    string M()
    {
        return IPAddress.IPv6Loopback.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("InetAddress.IPv6Loopback", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IPAddressHelper.toString", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
