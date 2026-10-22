using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SuppressTypeTests
{

    [Fact]
    public void GetObjectData_TrivialBody_EmitsSuppressedParameterVersion()
    {
        // A GetObjectData whose body does not use the suppressed params is emitted with
        // __suppress__-typed parameters so that derived call sites (e.g. base.GetObjectData
        // forwarding) still compile.
        var result = Convert(@"
using System.Runtime.Serialization;

public class MyUri : ISerializable
{
    public void GetObjectData(SerializationInfo info, StreamingContext context) { }
}");

        Assert.True(result.Success);
        Assert.Contains("getObjectData(__suppress__ info, __suppress__ context)", result.GeneratedCode);
    }

    [Fact]
    public void FormatException_Maps_To_Compat_FormatException()
    {
        var result = Convert(@"
public class MyFormatException : FormatException
{
    public MyFormatException() { }
    public MyFormatException(string message) : base(message) { }
    public MyFormatException(string message, Exception inner) : base(message, inner) { }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("NumberFormatException", result.GeneratedCode);
        Assert.Contains("FormatException", result.GeneratedCode);
    }

    [Fact]
    public void SuppressedInterface_KeptInImplementsClause_ForRuntimeCast()
    {
        // ((ISerializable)r).GetObjectData(...) 渲染为 __suppress__ cast（迭代13收紧后
        // 基类显式实现不参与解包，cast 形式保留）。类到接口的 cast 在 javac 层恒可编译，
        // 但若类未真正 implements __suppress__，运行时抛 ClassCastException 而非接口
        // default 方法里的 PlatformNotSupportedException——因此被抑制接口必须保留在
        // implements 子句中。
        var result = Convert(@"
using System;
using System.Runtime.Serialization;

public class MySerializable : ISerializable
{
    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
    {
        throw new PlatformNotSupportedException();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("class MySerializable implements __suppress__", result.GeneratedCode);
        Assert.Contains("import io.github.ningpp.compat.__suppress__;", result.GeneratedCode);
    }

    [Fact]
    public void SuppressedInterface_DerivedClassInheritsImplements_WithoutRepetition()
    {
        // 派生类未直接声明 ISerializable（symbol.Interfaces 只含直接声明）——
        // implements 子句不应重复出现 __suppress__，Java 继承已覆盖 cast 场景。
        var result = Convert(@"
using System.Runtime.Serialization;

public class Base : ISerializable
{
    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context) { }
}

public class Derived : Base { }");

        Assert.True(result.Success);
        Assert.Contains("class Base implements __suppress__", result.GeneratedCode);
        Assert.DoesNotContain("class Derived implements __suppress__", result.GeneratedCode);
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
