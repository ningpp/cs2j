using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// typeof(X&lt;&gt;) (unbound generic) must not leak the C# arity marker "`1" into
/// Java class literals. javac rejects EnumConverter`1.class as an illegal character.
/// </summary>
public class TypeOfUnboundGenericTests
{
    [Fact]
    public void TypeOfUnboundGeneric_DoesNotLeakArityMarker()
    {
        var result = Convert(@"
using System;
public class Generic<T> { }

public class Sample
{
    public Type M(Type keyType)
    {
        return typeof(Generic<>).MakeGenericType(keyType);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("`", result.GeneratedCode);
        Assert.Contains("Generic.class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOfUnboundGeneric_InNamespace_DoesNotLeakArityMarker()
    {
        var result = Convert(@"
using System;
namespace Serialization.Converters
{
    public class EnumConverter<T> { }
}

public class Sample
{
    public Type M(Type keyType)
    {
        return typeof(Serialization.Converters.EnumConverter<>).MakeGenericType(keyType);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("`", result.GeneratedCode);
        Assert.Contains("EnumConverter.class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOfUnboundNullable_DoesNotLeakArityMarker()
    {
        var result = Convert(@"
using System;
public class Sample
{
    public Type M()
    {
        return typeof(Nullable<>);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("`", result.GeneratedCode);
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
