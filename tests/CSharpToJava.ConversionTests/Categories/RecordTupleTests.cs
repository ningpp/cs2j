using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# records (positional, with, inheritance, record struct) and tuples
/// (return, named, deconstruction, assignment, parameters) to Java constructs.
/// </summary>
public class RecordTupleTests : ConversionTestBase
{
    [Fact]
    public void PositionalRecord_ConvertsToJavaRecord()
    {
        var result = Convert("public record R(int X);");
        AssertConversion(result, "public record R(int x) {");
    }

    [Fact]
    public void TwoFieldRecord_ConvertsToJavaRecord()
    {
        var result = Convert("public record R(int X, string Y);");
        AssertConversion(result, "public record R(int x, String y) {");
    }

    [Fact]
    public void RecordWithMembers_ConvertsToJavaRecord()
    {
        var result = Convert("public record R(int X) { public string Name = \"\"; }");
        AssertConversion(result, "public record R(int x) {");
    }

    [Fact]
    public void RecordWithExpression_ConvertsToNew()
    {
        var result = Convert("public record R(int X); class C { public R M(R r) => r with { X = 2 }; }");
        AssertConversion(result, "public record R(int x) {", "public R m(R r) { return new R(); }");
        AssertJavaDoesNotContain(result, "with", "C# 'with' expression must be rewritten");
    }

    [Fact]
    public void RecordInheritance_ConvertsToExtends()
    {
        var result = Convert("public record R(int X); public record Sub(int Y) : R(1);");
        AssertConversion(result, "public record Sub(int y) extends R {");
    }

    [Fact]
    public void RecordStruct_ConvertsToPlainClass()
    {
        var result = Convert("public record struct R(int X);");
        AssertConversion(result,
            "public class R {",
            "public int x;",
            "public R(int x) {",
            "this.x = x;",
            "public R() {",
            "this.x = 0;");
    }

    [Fact]
    public void TupleReturn_ConvertsToVavrTuple()
    {
        var result = Convert("class C { public (int, string) M() => (1, \"a\"); }");
        AssertConversion(result,
            "import io.vavr.Tuple2;",
            "import io.vavr.Tuple;",
            "public Tuple2<Integer, String> m() { return Tuple.of(1, \"a\"); }");
    }

    [Fact]
    public void NamedTupleReturn_ConvertsToVavrTuple()
    {
        var result = Convert("class C { public (int A, string B) M() => (1, \"a\"); }");
        AssertConversion(result, "public Tuple2<Integer, String> m() { return Tuple.of(1, \"a\"); }");
    }

    [Fact]
    public void Deconstruction_ConvertsToTupleAccess()
    {
        var result = Convert("class C { public void M() { var (a, b) = (1, 2); } }");
        AssertConversion(result,
            "var _t = Tuple.of(1, 2);",
            "var a = _t._1();",
            "var b = _t._2();");
    }

    [Fact]
    public void TupleAssignment_ConvertsToCloneAndAccess()
    {
        var result = Convert("class C { public void M() { var t = (1, \"a\"); int x = t.Item1; } }");
        AssertConversion(result, "var t = ((Tuple.of(1, \"a\"))).clone();", "int x = t._1();");
    }

    [Fact]
    public void TupleAsParameter_ConvertsToTuple2()
    {
        var result = Convert("class C { public void M((int, int) t) { var x = t.Item1; } }");
        AssertConversion(result,
            "import io.vavr.Tuple2;",
            "public void m(Tuple2<Integer, Integer> t) {",
            "var x = t._1();");
    }

    [Fact]
    public void TupleNoCSharpResidue()
    {
        var result = Convert("class C { public (int, string) M() => (1, \"a\"); }");
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "Item1", "C# tuple 'Item1' must be rewritten to Vavr '_1()'");
    }
}
