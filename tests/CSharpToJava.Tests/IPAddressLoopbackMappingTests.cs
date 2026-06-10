using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# IPAddress.Loopback and IPAddress.IPv6Loopback static properties
/// are mapped to InetAddress.getLoopbackAddress() in Java.
/// Reproduces: "找不到符号: 变量 Loopback 位置: 类 java.net.InetAddress"
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
    public void IPAddress_IPv6Loopback_MappedToGetLoopbackAddress()
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
        Assert.Contains("getLoopbackAddress", result.GeneratedCode, StringComparison.Ordinal);
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
        Assert.Contains("getLoopbackAddress", result.GeneratedCode, StringComparison.Ordinal);
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
