using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a C# field named Length is accessed as a field in Java, not mapped
/// to the property getter getLength() used for String/Array/StringBuilder Length.
/// </summary>
public class LengthFieldAccessTests
{
    [Fact]
    public void LengthField_ReadAccess_UsesField()
    {
        var result = Convert("""
            class Sample
            {
                class Metrics
                {
                    public int Length;
                }

                int Read(Metrics m)
                {
                    return m.Length;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("return m.Length;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getLength()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LengthField_Assignment_UsesField()
    {
        var result = Convert("""
            class Sample
            {
                class Metrics
                {
                    public int Length;
                }

                void Set(Metrics m, int value)
                {
                    m.Length = value;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("m.Length = value;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getLength()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("setLength", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LengthProperty_OnString_StillUsesLengthMethod()
    {
        var result = Convert("""
            class Sample
            {
                int Read(string s)
                {
                    return s.Length;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("s.length()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LengthField_UnderscoreReceiver_UsesField()
    {
        var result = Convert("""
            class Sample
            {
                class Metrics
                {
                    public int Length;
                }

                int Read(Metrics _metrics)
                {
                    return _metrics.Length;
                }

                void Set(Metrics _metrics, int value)
                {
                    _metrics.Length = value;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("return _metrics.Length;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_metrics.Length = value;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getLength()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("setLength", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LengthField_InNamespace_InternalClass_UsesField()
    {
        var result = Convert("""
            namespace Data
            {
                internal class Metrics
                {
                    internal int Length;
                    internal int MinLength;
                }

                class Sample
                {
                    internal int Read(Metrics m)
                    {
                        return m.Length;
                    }

                    internal void Set(Metrics m, int value)
                    {
                        m.Length = value;
                    }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("return m.Length;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("m.Length = value;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getLength()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("setLength", result.GeneratedCode, StringComparison.Ordinal);
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
