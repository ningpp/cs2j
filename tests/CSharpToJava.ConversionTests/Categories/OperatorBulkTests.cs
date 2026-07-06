using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// High-volume verification that C# expressions (binary, unary, ternary, compound assignment) convert
/// identically to Java. Most operators map 1:1, which is a strong, easily-verified correctness signal.
/// </summary>
public class OperatorBulkTests : ConversionTestBase
{
    public static IEnumerable<object[]> IntExprCases => new List<object[]>
    {
        new object[] { "a + b" }, new object[] { "a - b" }, new object[] { "a * b" },
        new object[] { "a / b" }, new object[] { "a % b" }, new object[] { "a & b" },
        new object[] { "a | b" }, new object[] { "a ^ b" }, new object[] { "a << b" },
        new object[] { "a >> b" }, new object[] { "a < b" }, new object[] { "a > b" },
        new object[] { "a <= b" }, new object[] { "a >= b" }, new object[] { "a == b" },
        new object[] { "a != b" }, new object[] { "a + 1" }, new object[] { "1 + a" },
        new object[] { "a - 1" }, new object[] { "a * 2" }, new object[] { "a / 2" },
        new object[] { "a % 2" }, new object[] { "a & 1" }, new object[] { "a | 1" },
        new object[] { "a ^ 1" }, new object[] { "a << 1" }, new object[] { "a >> 1" },
        new object[] { "a < 0" }, new object[] { "a > 0" }, new object[] { "a <= 0" },
        new object[] { "a >= 0" }, new object[] { "a == 0" }, new object[] { "a != 0" },
        new object[] { "a + b + 1" }, new object[] { "a * b + 2" }, new object[] { "(a + b) * 2" },
        new object[] { "a & b | 1" }, new object[] { "a ^ b & 1" }, new object[] { "a - b * 2" },
        new object[] { "a / b + 1" }, new object[] { "a % b + 3" }, new object[] { "a | b & 1" },
        new object[] { "a + b - 1" }, new object[] { "a * (b + 1)" }, new object[] { "(a - b) * (b + a)" },
    };

    public static IEnumerable<object[]> BoolExprCases => new List<object[]>
    {
        new object[] { "a && b" }, new object[] { "a || b" }, new object[] { "!a" },
        new object[] { "!a && !b" }, new object[] { "a || !b" }, new object[] { "a && !b" },
        new object[] { "!(a && b)" }, new object[] { "a == b" }, new object[] { "a != b" },
    };

    public static IEnumerable<object[]> CompoundCases => new List<object[]>
    {
        new object[] { "x += a" }, new object[] { "x -= a" }, new object[] { "x *= a" },
        new object[] { "x /= a" }, new object[] { "x %= a" }, new object[] { "x &= a" },
        new object[] { "x |= a" }, new object[] { "x ^= a" }, new object[] { "x <<= a" },
        new object[] { "x >>= a" },
    };

    [Theory]
    [MemberData(nameof(IntExprCases))]
    public void IntExpression_ConvertsIdentically(string expr)
    {
        var result = Convert($"class C {{ public int M(int a, int b) {{ return {expr}; }} }}");
        AssertConversion(result, $"return {expr};");
    }

    [Theory]
    [MemberData(nameof(BoolExprCases))]
    public void BoolExpression_ConvertsIdentically(string expr)
    {
        var result = Convert($"class C {{ public bool M(bool a, bool b) {{ return {expr}; }} }}");
        AssertConversion(result, $"return {expr};");
    }

    [Theory]
    [MemberData(nameof(CompoundCases))]
    public void CompoundAssignment_ConvertsIdentically(string stmt)
    {
        var result = Convert($"class C {{ public void M(int x, int a) {{ {stmt}; }} }}");
        AssertConversion(result, stmt + ";");
    }

    [Fact]
    public void NestedArithmetic_ConvertsIdentically()
    {
        var result = Convert("class C { public int M(int a, int b, int c) { return (a + b) * c - (a - b) / c; } }");
        AssertConversion(result, "return (a + b) * c - (a - b) / c;");
    }

    [Fact]
    public void TernaryInArithmetic_ConvertsIdentically()
    {
        var result = Convert("class C { public int M(int a) { return a > 0 ? a * 2 : -a; } }");
        AssertConversion(result, "return (a > 0 ? a * 2 : -a);");
    }
}
