using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Type.EmptyTypes must render as a zero-length Class[]: System.Type maps
/// to java.lang.Class, which has no EmptyTypes member, so a bare
/// `Type.EmptyTypes` leaves javac with "找不到符号: 变量 Type"
/// (RegexAssemblyCompiler passes it to TypeHelper.getConstructor argument lists).
/// </summary>
public class TypeEmptyTypesBridgeTests
{
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

    /// <summary>
    /// C# shape from RegexAssemblyCompiler.GenerateCreateInstance:
    /// Newobj(type.GetConstructor(Type.EmptyTypes)!).
    /// </summary>
    [Fact]
    public void GetConstructorWithTypeEmptyTypes_RendersZeroLengthClassArray()
    {
        var result = Convert(@"
using System;
using System.Reflection;
class Sample
{
    object? Build(Type type)
    {
        return type.GetConstructor(Type.EmptyTypes);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("TypeHelper.getConstructor(type, new Class[0])", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Type.EmptyTypes", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Standalone local of the C# Type[] element type: still a zero-length Class[].
    /// </summary>
    [Fact]
    public void EmptyTypesLocal_RendersZeroLengthClassArray()
    {
        var result = Convert(@"
using System;
class Sample
{
    int Count()
    {
        Type[] empty = Type.EmptyTypes;
        return empty.Length;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Class[] empty = new Class[0]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Type.EmptyTypes", result.GeneratedCode, StringComparison.Ordinal);
    }
}
