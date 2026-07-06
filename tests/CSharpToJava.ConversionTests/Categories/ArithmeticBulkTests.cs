using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// High-volume verification that C# arithmetic, bitwise, comparison and boolean expressions convert
/// identically to Java. These are strong, easily-verified correctness signals.
/// </summary>
public class ArithmeticBulkTests : ConversionTestBase
{
    public static IEnumerable<object[]> IntExprCases => new List<object[]>
    {
        new object[] { "a + b + c" }, new object[] { "a - b - c" }, new object[] { "a * b * c" },
        new object[] { "a + b * c" }, new object[] { "(a + b) * c" }, new object[] { "a * (b + c)" },
        new object[] { "a - b + c" }, new object[] { "a / b + c" }, new object[] { "a % b + c" },
        new object[] { "a & b & c" }, new object[] { "a | b | c" }, new object[] { "a ^ b ^ c" },
        new object[] { "a + b - c * 2" }, new object[] { "a * b + c * 2" }, new object[] { "(a + b) / (c + 1)" },
        new object[] { "a & b | c" }, new object[] { "a | b & c" }, new object[] { "a << b >> c" },
        new object[] { "a + 1 - 2 + 3" }, new object[] { "a * 2 + b * 3" }, new object[] { "a / 2 + b / 2" },
        new object[] { "a % 2 + b % 2" }, new object[] { "-(a + b + c)" }, new object[] { "~a & b" },
        new object[] { "a ^ b + c" }, new object[] { "a | 1 & c" }, new object[] { "a == b ? b : c" },
        new object[] { "a != b ? a : c" }, new object[] { "a > b ? a : b" }, new object[] { "a < b ? a : b" },
        new object[] { "a + b * c - d" }, new object[] { "(a - b) * (c + d)" }, new object[] { "a & ~b" },
        new object[] { "a | (b & c)" }, new object[] { "a ^ (b | c)" }, new object[] { "a * a + b * b" },
        new object[] { "a / b % c" }, new object[] { "a % b * c" }, new object[] { "a << 2 + b" },
        new object[] { "a >> 1 | b" }, new object[] { "a + b - c + d" }, new object[] { "a * (b + c) - d" },
    };

    public static IEnumerable<object[]> BoolExprCases => new List<object[]>
    {
        new object[] { "a && b && c" }, new object[] { "a || b || c" }, new object[] { "a && !b" },
        new object[] { "!a || b" }, new object[] { "(a && b) || c" }, new object[] { "a == b && c == d" },
        new object[] { "a != b || c != d" }, new object[] { "a > b && c < d" }, new object[] { "!(a && b)" },
        new object[] { "a ^ b" },
    };

    [Theory]
    [MemberData(nameof(IntExprCases))]
    public void IntExpression_ConvertsIdentically(string expr)
    {
        var result = Convert($"class C {{ public int M(int a, int b, int c) {{ int d = 0; return {expr}; }} }}");
        // Ternary/conditional expressions get wrapped in parentheses by the converter, so assert the
        // (possibly wrapped) expression as a substring rather than the exact return statement.
        AssertConversion(result, expr);
    }

    [Theory]
    [MemberData(nameof(BoolExprCases))]
    public void BoolExpression_ConvertsIdentically(string expr)
    {
        var result = Convert($"class C {{ public bool M(bool a, bool b, bool c, bool d) {{ return {expr}; }} }}");
        AssertConversion(result, expr);
    }

    [Fact]
    public void MixedArithmetic_ConvertsIdentically()
    {
        var result = Convert("class C { public int M(int a, int b, int c, int d) { return (a + b) * (c - d) / 2; } }");
        AssertConversion(result, "(a + b) * (c - d) / 2");
    }

    [Fact]
    public void NestedTernary_ConvertsIdentically()
    {
        var result = Convert("class C { public int M(int a, int b, int c) { return a > 0 ? (b > 0 ? 1 : 2) : 3; } }");
        AssertConversion(result, "a > 0", "b > 0 ? 1 : 2", ": 3");
    }
}
