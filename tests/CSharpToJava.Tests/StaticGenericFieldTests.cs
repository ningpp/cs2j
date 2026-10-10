using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class StaticGenericFieldTests
{
    private const string ThreadStaticUsing = "using System; ";

    [Fact]
    public void ThreadStatic_StaticField_DirectConversion_FailsFast_RequiresPreprocessor()
    {
        var result = Convert(ThreadStaticUsing + """
            public class Counter
            {
                [ThreadStatic] private static int _hits;

                public int Bump() => ++_hits;
            }
            """);

        Assert.False(result.Success);
        var message = string.Join("\n", result.Diagnostics.Select(d => d.Message));
        Assert.Contains("CS2J normalization required", message, StringComparison.Ordinal);
        Assert.Contains("thread-static preprocessor", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreadStatic_InstanceField_DirectConversion_PassesThrough()
    {
        // The CLR ignores [ThreadStatic] on instance fields; the thread-static
        // preprocessor leaves them untouched (CS2J1201) and the converter must
        // keep emitting an ordinary field with the attribute governed/stripped.
        var result = Convert(ThreadStaticUsing + """
            public class State
            {
                [ThreadStatic] private int _value;

                public int Value
                {
                    get => _value;
                    set => _value = value;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("private int _value", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ThreadLocal", result.GeneratedCode, StringComparison.Ordinal);
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
