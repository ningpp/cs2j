using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# operators and expressions (binary, unary, compound assignment,
/// increment/decrement, ternary, null-coalescing) to Java. Most map identity; some become explicit forms.
/// </summary>
public class OperatorTests : ConversionTestBase
{
    public static IEnumerable<object[]> IntBinaryOps => new List<object[]>
    {
        new object[] { "a + b" }, new object[] { "a - b" }, new object[] { "a * b" },
        new object[] { "a / b" }, new object[] { "a % b" }, new object[] { "a & b" },
        new object[] { "a | b" }, new object[] { "a ^ b" }, new object[] { "a << b" },
        new object[] { "a >> b" }, new object[] { "a < b" }, new object[] { "a > b" },
        new object[] { "a <= b" }, new object[] { "a >= b" }, new object[] { "a == b" },
        new object[] { "a != b" }, new object[] { "a + 1" }, new object[] { "1 + a" },
        new object[] { "a - 1" }, new object[] { "a * 2" }, new object[] { "a % 2" },
        new object[] { "a & 1" }, new object[] { "a | 1" }, new object[] { "a ^ 1" },
    };

    public static IEnumerable<object[]> BoolBinaryOps => new List<object[]>
    {
        new object[] { "a && b" }, new object[] { "a || b" },
    };

    public static IEnumerable<object[]> UnaryOps => new List<object[]>
    {
        new object[] { "int", "-a" }, new object[] { "int", "~a" }, new object[] { "bool", "!a" },
    };

    public static IEnumerable<object[]> CompoundAssignOps => new List<object[]>
    {
        new object[] { "x += 2" }, new object[] { "x -= 2" }, new object[] { "x *= 3" },
        new object[] { "x /= 3" }, new object[] { "x %= 3" }, new object[] { "x &= 4" },
        new object[] { "x |= 4" }, new object[] { "x ^= 4" }, new object[] { "x <<= 1" },
        new object[] { "x >>= 1" },
    };

    [Theory]
    [MemberData(nameof(IntBinaryOps))]
    public void IntBinaryOperator_ConvertsIdentically(string expr)
    {
        var result = Convert($"class C {{ public int M(int a, int b) {{ return {expr}; }} }}");
        AssertConversion(result, $"return {expr};");
    }

    [Theory]
    [MemberData(nameof(BoolBinaryOps))]
    public void BoolBinaryOperator_ConvertsIdentically(string expr)
    {
        var result = Convert($"class C {{ public bool M(bool a, bool b) {{ return {expr}; }} }}");
        AssertConversion(result, $"return {expr};");
    }

    [Theory]
    [MemberData(nameof(UnaryOps))]
    public void UnaryOperator_ConvertsIdentically(string type, string expr)
    {
        var result = Convert($"class C {{ public {type} M({type} a) {{ return {expr}; }} }}");
        AssertConversion(result, $"return {expr};");
    }

    [Theory]
    [MemberData(nameof(CompoundAssignOps))]
    public void CompoundAssignment_ConvertsIdentically(string stmt)
    {
        var result = Convert($"class C {{ public void M() {{ int x = 1; {stmt}; }} }}");
        AssertConversion(result, stmt + ";");
    }

    [Fact]
    public void IncrementPostfix_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { int x = 1; x++; } }");
        AssertConversion(result, "x++;");
    }

    [Fact]
    public void IncrementPrefix_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { int x = 1; ++x; } }");
        AssertConversion(result, "++x;");
    }

    [Fact]
    public void DecrementPostfix_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { int x = 1; x--; } }");
        AssertConversion(result, "x--;");
    }

    [Fact]
    public void DecrementPrefix_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { int x = 1; --x; } }");
        AssertConversion(result, "--x;");
    }

    [Fact]
    public void SimpleAssignment_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { int x = 5; x = 10; } }");
        AssertConversion(result, "int x = 5;", "x = 10;");
    }

    [Fact]
    public void Ternary_ConvertsWithParens()
    {
        var result = Convert("class C { public int M(int a) => a > 0 ? a : -a; }");
        AssertConversion(result, "return (a > 0 ? a : -a);");
    }

    [Fact]
    public void NullCoalescing_ConvertsToTernary()
    {
        var result = Convert("class C { public string M(string a) => a ?? \"\"; }");
        AssertConversion(result, "return a != null ? a : \"\";");
    }

    [Fact]
    public void LogicalExpression_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { bool a = true; bool b = !a && false || true; } }");
        AssertConversion(result, "boolean b = !a && false || true;");
    }

    [Fact]
    public void OperatorOverload_ConvertsToNamedStaticMethod()
    {
        var result = Convert("class C { public static C operator +(C a, C b) { return a; } }");
        AssertConversion(result, "public static C add(C a, C b) {", "return a;");
    }

    [Fact]
    public void ParenthesizedExpression_ConvertsIdentically()
    {
        var result = Convert("class C { public int M(int a, int b) { return (a + b) * 2; } }");
        AssertConversion(result, "return (a + b) * 2;");
    }
}
