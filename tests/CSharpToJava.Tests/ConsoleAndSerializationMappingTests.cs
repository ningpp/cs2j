using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class ConsoleAndSerializationMappingTests
{

    [Fact]
    public void ExceptionSerializationConstructor_IsOmitted()
    {
        // A .NET serialization constructor (SerializationInfo, StreamingContext) is
        // meaningless in Java: java.io.Serializable does not use it, and a base
        // forwarding super(info, context) cannot compile because the mapped base
        // (Exception -> RuntimeException) has no (Object, Object) constructor.
        // The constructor must be omitted entirely.
        var result = Convert("""
using System;
using System.Runtime.Serialization;

class AcceptException : Exception
{
    internal AcceptException()
    {
    }

    protected AcceptException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("AcceptException()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SerializationInfo", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("StreamingContext", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("__suppress__", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("super(info, context)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("super(SerializationInfo", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorBaseInitializer_WithNull_IsEmittedAsSuperCall()
    {
        var result = Convert("""
class Base
{
    public Base(object value)
    {
    }
}

class Parser : Base
{
    public Parser() : base(null)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public Parser() {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super(null);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public Parser();", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticAndInstanceParameterlessConstructors_AreBothEmitted()
    {
        var result = Convert("""
abstract class Base
{
    protected Base(object scanner)
    {
    }
}

class Parser : Base
{
    static Parser()
    {
        int x = 0;
    }

    public Parser() : base(null)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("static {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Parser() {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super(null);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatExceptionCatch_DoesNotDuplicateArgumentExceptionCatch()
    {
        var result = Convert("""
using System;

class Test
{
    int M(string text)
    {
        try
        {
            return int.Parse(text);
        }
        catch (FormatException)
        {
            return -1;
        }
        catch (ArgumentException)
        {
            return -2;
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("catch (FormatException _ex)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("catch (IllegalArgumentException _ex)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = CreateOptions(),
        });
    }

    private static ConversionOptions CreateOptions()
        => new()
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
}
