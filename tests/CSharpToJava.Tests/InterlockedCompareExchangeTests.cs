using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class InterlockedCompareExchangeTests
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
    public void GenericCompareExchangeOnReferenceRefUsesCompatHelper()
    {
        var result = Convert("""
using System.Threading;

class Sample
{
    private static object s_Lock;

    static object GetLock()
    {
        if (s_Lock == null)
        {
            object o = new object();
            Interlocked.CompareExchange<object>(ref s_Lock, o, null);
        }

        return s_Lock;
    }
}
""");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        Assert.Contains("io.github.ningpp.compat.ObjectHolder<Object> _s_LockRef = new io.github.ningpp.compat.ObjectHolder<>(s_Lock);", code);
        Assert.Contains("InterlockedHelper.compareExchange(_s_LockRef, o, null);", code);
        Assert.Contains("s_Lock = _s_LockRef.value;", code);
        Assert.DoesNotContain("AtomicInteger.compareExchange", code);
    }
}
