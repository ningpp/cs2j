using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# byte maps to Java int in this converter's type system (sbyte maps to
/// byte), so typeof(byte) must emit int.class — byte.class (Class&lt;Byte&gt;)
/// would mismatch CSharpList&lt;Integer&gt; etc. Primitive `is` patterns must
/// also test the boxed wrapper type (instanceof Double), since Java's primitive
/// pattern matching is a preview feature.
/// </summary>
public class PrimitiveClassLiteralAndPatternTests
{

    [Fact]
    public void PrimitiveIsPatterns_UseBoxedWrapperTypes()
    {
        var result = Convert("""
class C {
    String M(Object o) => o is double d ? "d" : o is float f ? "f" : "x";
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("instanceof Double", code, StringComparison.Ordinal);
        Assert.Contains("instanceof Float", code, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceof double", code, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceof float", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// C# 数值类型在 Java 载体上会合并（byte/int/uint/ushort 都是 Integer），
    /// typeof(byte) 必须用 compat 的 CSharpByte 身份令牌，typeof(int) 才是 Integer。
    /// </summary>
    [Fact]
    public void TypeOfNumericPrimitives_UseDistinctCSharpRuntimeTokens()
    {
        var result = Convert("""
using System;

class C
{
    Type B() => typeof(byte);
    Type I() => typeof(int);
    Type U() => typeof(ushort);
    Type L() => typeof(ulong);
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("return CSharpByte.class;", code, StringComparison.Ordinal);
        Assert.Contains("return Integer.class;", code, StringComparison.Ordinal);
        Assert.Contains("return CSharpUInt16.class;", code, StringComparison.Ordinal);
        Assert.Contains("return CSharpUInt64.class;", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpByte;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 泛型基类成员的 typeof(T)（真实链 JsonConverter&lt;T&gt;.TypeToConvert）在实例上下文
    /// 走 TypeHelper.getTypeArgument 反查运行时实参；派生类钉住 C# 实参令牌，使 byte/int
    /// 这类 Java 擦除后同载体的类型仍然可区分。
    /// </summary>
    [Fact]
    public void GenericBaseTypeOfParameter_ResolvesRuntimeArgumentAndPinsDerivedToken()
    {
        var result = Convert("""
using System;

class Converter<T>
{
    public Type TypeToConvert => typeof(T);
}

class ByteConverter : Converter<byte> { }
class IntConverter : Converter<int> { }
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains(
            "return TypeHelper.getTypeArgument(this, Converter.class, 0, Object.class);",
            code, StringComparison.Ordinal);
        Assert.Contains("__cs2j_typeArg_Converter_0 = CSharpByte.class", code, StringComparison.Ordinal);
        Assert.Contains("__cs2j_typeArg_Converter_0 = Integer.class", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 构造器初始化器（真实链 ArrayBuilder(int) : this(capacity, typeof(T))）的实参
    /// 先于构造器体求值，Java 禁止在那里引用 this：此处必须退回 Object.class。
    /// </summary>
    [Fact]
    public void GenericTypeOfInConstructorInitializer_DoesNotReferenceThis()
    {
        var result = Convert("""
using System;

class Builder<T>
{
    public Builder(int capacity) : this(capacity, typeof(T)) { }
    public Builder(int capacity, Type token) { }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("this(capacity, Object.class);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("this(capacity, TypeHelper.getTypeArgument(this", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 泛型实参仍含本类类型参数时不能钉住（真实链 NullableConverter&lt;T&gt; : JsonConverter&lt;T?&gt;）：
    /// Java 没有 T.class，必须跳过 pin。
    /// </summary>
    [Fact]
    public void GenericBaseTypeArgumentContainingTypeParameter_IsNotPinned()
    {
        var result = Convert("""
using System;

class Converter<T>
{
    public Type TypeToConvert => typeof(T);
}

class NullableConverter<T> : Converter<T?> where T : struct { }
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("= T.class", code, StringComparison.Ordinal);
        Assert.DoesNotContain("__cs2j_typeArg_Converter_0 =", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 方法类型参数没有线程化令牌时，typeof(T) 从「形参静态类型为 T」的实参运行时
    /// 类型推导（真实链 EnumConverter.convertToUInt64 的 typeof(T) 退化为 Enum.class）。
    /// </summary>
    [Fact]
    public void MethodTypeParameterToken_DerivesFromValueWhenBoundByClassConstraint()
    {
        var result = Convert("""
using System;

class C
{
    static long Convert<T>(T value) where T : Enum
    {
        return typeof(T) == typeof(int) ? 1L : 0L;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("value == null ? Enum.class : value.getClass()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// C# 允许把装箱枚举解箱成底层整型；Java 枚举常量不是 Integer，必须经
    /// EnumHelper 取值（真实链 EnumConverter.convertToUInt64 的 (int)value）。
    /// </summary>
    [Fact]
    public void ObjectToIntegralCast_UnboxesEnumUnderlyingValues()
    {
        var result = Convert("""
class C
{
    static int Read(object value) => (int)value;
    static long Read64(object value) => (long)value;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("EnumHelper.toInt32(value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("EnumHelper.toInt64(value)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "PrimitiveClassLiteralAndPatternTests.cs",
            Options = new ConversionOptions(),
        });
    }
}
