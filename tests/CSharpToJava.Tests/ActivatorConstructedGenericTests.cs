using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# Activator.CreateInstance(constructedGenericType, ..., args: null) succeeds
/// because the open generic type has an implicit parameterless constructor. The
/// Java conversion injects Class&lt;?&gt; runtime parameters into converter
/// constructors, so the reflection call must forward the MakeGenericType type
/// arguments. Regression: ObjectConverterFactory / IEnumerableConverterFactory.
/// </summary>
public class ActivatorConstructedGenericTests
{
    [Fact]
    public void CreateInstanceWithNullArgsOnConstructedGeneric_PassesTypeArgument()
    {
        var result = Convert(@"
using System;
using System.Reflection;

class GenericConverter<T> { }

class Sample
{
    object M(Type typeToConvert)
    {
        Type converterType = typeof(GenericConverter<>).MakeGenericType(typeToConvert);
        return Activator.CreateInstance(converterType, BindingFlags.Instance | BindingFlags.Public, null, null, null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains(
            "Activator.createInstanceForGenericType(converterType, BindingFlags.Instance | BindingFlags.Public, null, typeToConvert)",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInstanceWithNullArgsOnArrayTypeArguments_PassesTypeArgumentArray()
    {
        var result = Convert(@"
using System;
using System.Reflection;

class GenericConverter<T1, T2> { }

class Sample
{
    object M(Type typeToConvert)
    {
        Type[] typeArguments = new Type[2];
        typeArguments[0] = typeToConvert;
        typeArguments[1] = typeof(object);
        Type converterType = typeof(GenericConverter<,>).MakeGenericType(typeArguments);
        return Activator.CreateInstance(converterType, BindingFlags.Instance | BindingFlags.Public, null, null, null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains(
            "Activator.createInstanceForGenericType(converterType, BindingFlags.Instance | BindingFlags.Public, null, typeArguments)",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInstanceWithNullArgsOnInlineTypeArray_FlattensTypeArguments()
    {
        var result = Convert(@"
using System;
using System.Reflection;

class GenericConverter<T1, T2> { }

class Sample
{
    object M(Type keyType, Type valueType)
    {
        return Activator.CreateInstance(
            typeof(GenericConverter<,>).MakeGenericType(new Type[] { keyType, valueType }),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            null,
            null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains(
            "Activator.createInstanceForGenericType(GenericConverter.class, BindingFlags.Instance | BindingFlags.Public, null, keyType, valueType)",
            code,
            StringComparison.Ordinal);
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
