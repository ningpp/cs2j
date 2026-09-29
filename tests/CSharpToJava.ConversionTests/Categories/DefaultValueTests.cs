using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

public class DefaultValueTests : ConversionTestBase
{
    public static IEnumerable<object[]> PrimitiveDefaults => new List<object[]>
    {
        // csharpType, javaTypeMarker
        new object[] { "int", "0" },
        new object[] { "long", "0L" },
        new object[] { "short", "(short)0" },
        new object[] { "byte", "0" },
        new object[] { "float", "0.0f" },
        new object[] { "double", "0.0" },
        new object[] { "bool", "false" },
        new object[] { "char", "'\\0'" },
        new object[] { "decimal", "Decimal.ZERO" },
        new object[] { "string", "null" },
    };

    [Theory]
    [MemberData(nameof(PrimitiveDefaults))]
    public void LocalVariable_PrimitiveDefault_Literal(string csharpType, string javaMarker)
    {
        var result = Convert($"class C {{ public void M() {{ {csharpType} x = default; }} }}");
        AssertConversion(result, javaMarker);
    }

    [Theory]
    [MemberData(nameof(PrimitiveDefaults))]
    public void LocalVariable_PrimitiveDefault_Explicit(string csharpType, string javaMarker)
    {
        var result = Convert($"class C {{ public void M() {{ {csharpType} x = default({csharpType}); }} }}");
        AssertConversion(result, javaMarker);
    }

    [Fact]
    public void StructDefault_EmitsNewT()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M() { Point p = default; } }");
        AssertConversion(result, "new Point()");
    }

    [Fact]
    public void GenericClassConstraint_EmitsNull()
    {
        var result = Convert("class C<T> where T : class { public T V = default; }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void TupleDefault_Explicit_EmitsTupleOf()
    {
        var result = Convert("class C { public void M() { var t = default((int, string)); } }");
        AssertConversion(result, "Tuple.of(0, null)");
        AssertJavaContains(result, "import io.vavr.Tuple;");
    }

    [Fact]
    public void NullableValueTypeDefault_EmitsNull()
    {
        var result = Convert("class C { public void M() { int? n = default; } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void NullableValueTypeDefault_Explicit_EmitsNull()
    {
        var result = Convert("class C { public void M() { int? n = default(int?); } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void NestedGenericDefault_EmitsNull()
    {
        var result = Convert("using System.Collections.Generic; class C { public void M() { Dictionary<int, string> d = default; } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void ArrayDefault_EmitsNull()
    {
        var result = Convert("class C { public void M() { int[] a = default; } }");
        AssertConversion(result, "null");
    }

    [Fact]
    public void DefaultInReturnPosition_EmitsMarker()
    {
        var result = Convert("class C { public int M() { return default; } }");
        AssertConversion(result, "return 0;");
    }

    [Fact]
    public void DefaultAsMethodArgument_EmitsMarker()
    {
        var result = Convert("class C { void Callee(int x) {} void Caller() { Callee(default); } }");
        AssertConversion(result, "callee(0)");
    }

    [Fact]
    public void DefaultInFieldInitializer_EmitsMarker()
    {
        var result = Convert("class C { private int _x = default; }");
        AssertConversion(result, "private int _x = 0;");
    }
}
