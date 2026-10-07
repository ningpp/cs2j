using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// P2-4 数值预言机半区转换器回归：这些发射形态在 SampleNumericBoundarySolution
/// 的 javac/JVM 真身下暴露并修复，单测把最小 C# 形态钉死，防止 javac 清零后
/// 回退。覆盖面：方法组实参→::方法引用、Java 调用上下文实参整数窄化、
/// uint 字面量回绕、decimal 极值 compat 名、Type.IsInstanceOfType→isInstance、
/// bool? 三元分支装箱、Nullable<T>.Value 守卫拆箱、new decimal[]→Decimal.array、
/// (E)null 恒等转换。
/// </summary>
public class NumericOracleJavaEmitTests
{
    [Fact]
    public void MethodGroupArgument_EmitsMethodReference()
    {
        var result = Convert("""
            class Sample
            {
                static int Twice(int x) => x * 2;
                static int Run(System.Func<int, int> f) => f(3);
                static int Call() => Run(Twice);
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("Sample::twice", result.GeneratedCode);
    }

    [Fact]
    public void IntegralArguments_NarrowToShortSbyteCharParameters()
    {
        // C# 常量表达式隐式转换允许 int 字面量绑定 short/sbyte 形参，但 Java
        // 调用上下文不做常量窄化 —— 必须显式 (short)/(byte)；显式 (char)
        // 窄化同样保留。
        var result = Convert("""
            class Sample
            {
                static void Take(short s, sbyte sb, char c) { }
                static void Call() { Take(1, 1, (char)1); }
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("take((short)(1), (byte)(1), (char)(1))", result.GeneratedCode);
    }

    [Fact]
    public void UInt32LiteralArgumentToMappedIntParameter_WrappedWithCast()
    {
        // C# uint maps to Java int; 0xFFFFFFFFu carries an L suffix in Java and
        // must be cast back to int so the method reference matches.
        var result = Convert("""
            class Sample
            {
                static int Wrap(uint u) => unchecked((int)u);
                static int Call() => Wrap(0xFFFFFFFFu);
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("(int)(0xFFFFFFFFL)", result.GeneratedCode);
    }

    [Fact]
    public void DecimalExtremeValues_UseCompatNames()
    {
        var result = Convert("""
            class Sample
            {
                static decimal Max() => decimal.MaxValue;
                static decimal Min() => decimal.MinValue;
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("Decimal.MAX_VALUE", result.GeneratedCode);
        Assert.Contains("Decimal.MIN_VALUE", result.GeneratedCode);
    }

    [Fact]
    public void TypeIsInstanceOfType_TranslatesToClassIsInstance()
    {
        var result = Convert("""
            class Sample
            {
                static bool Check(System.Type type, object ex) => type.IsInstanceOfType(ex) != null;
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("type.isInstance(ex)", result.GeneratedCode);
        Assert.DoesNotContain("isInstanceOfType", result.GeneratedCode);
    }

    [Fact]
    public void NullableBooleanTernary_PrimitiveBranchIsBoxed()
    {
        // Without the (Boolean) box Java unboxes the null branch to boolean and
        // throws NPE on the true path.
        var result = Convert("""
            class Sample
            {
                static bool? Guarded(bool? a, bool? b)
                    => a.HasValue && b.HasValue ? true : (bool?)null;
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("(Boolean)(true)", result.GeneratedCode);
        Assert.Contains("(Boolean)(null)", result.GeneratedCode);
    }

    [Fact]
    public void NullableValue_EmitsGuardHelper_NotBareUnbox()
    {
        var result = Convert("""
            class Sample
            {
                static int Unwrap(int? a) => a.Value;
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("Nullable.value(a)", result.GeneratedCode);
    }

    [Fact]
    public void DecimalArray_EmitsCompatFactory_NotNullFilledNewArray()
    {
        var result = Convert("""
            class Sample
            {
                static decimal[] Make() => new decimal[4];
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.Contains("Decimal.array(4)", result.GeneratedCode);
    }

    [Fact]
    public void CastNullToNullableEnum_IsIdentityNotCastExpression()
    {
        var result = Convert("""
            enum Color { Red, Green, Blue }
            class Sample
            {
                static object CastNull() => (Color?)null;
            }
            """);

        Assert.True(result.Success, Diag(result));
        Assert.DoesNotContain("(Color)(null)", result.GeneratedCode);
    }

    private static string Diag(ConversionResult result)
        => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
