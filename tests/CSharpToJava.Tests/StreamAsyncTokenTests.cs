using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StreamAsyncTokenTests
{
    [Fact]
    public void StreamFlushAsync_DropsCancellationTokenAsBoolean()
    {
        var result = Convert(@"
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class Sample
{
    public static void M(Stream s, CancellationToken ct)
    {
        s.FlushAsync(ct);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("flushAsync(false)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamWriteAsync_ByteArrayOverload_DropsToken()
    {
        var result = Convert(@"
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class Sample
{
    public static void M(Stream s, byte[] buf, CancellationToken ct)
    {
        s.WriteAsync(buf, 0, buf.Length, ct);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("writeAsync(buf, 0, buf.length)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("writeAsync(buf, 0, buf.length,", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamWriteAsync_MemoryOverload_ConvertsTokenToBoolean()
    {
        var result = Convert(@"
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class Sample
{
    public static void M(Stream s, byte[] buf, CancellationToken ct)
    {
        s.WriteAsync(new System.ReadOnlyMemory<byte>(buf), ct);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("writeAsync(new ReadOnlyMemory<Integer>(buf), false)", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
