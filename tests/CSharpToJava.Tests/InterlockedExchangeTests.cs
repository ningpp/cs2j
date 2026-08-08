using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class InterlockedExchangeTests
{
    private static ConversionResult Convert(string source)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = source,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void ExchangeOnReferenceFieldRefUsesCompatHelper()
    {
        var result = Convert("""
using System.Threading;

class Sample
{
    private object _runner;

    object RentRunner()
    {
        return Interlocked.Exchange(ref _runner, null) ?? new object();
    }
}
""");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        Assert.Contains("io.github.ningpp.compat.ObjectHolder<Object> __runnerRef = new io.github.ningpp.compat.ObjectHolder<>(_runner);", code);
        Assert.Contains("InterlockedHelper.exchange(__runnerRef, null)", code);
        Assert.Contains("_runner = __runnerRef.value;", code);
        Assert.DoesNotContain("AtomicInteger", code);
    }
}
