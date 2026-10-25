using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TupleSupportTests
{
    [Fact]
    public void TupleExpression_Binary_ProducesTupleOf()
    {
        var result = Convert(@"
class C {
    object Test() => (1, ""hello"");
}");
        Assert.True(result.Success);
        Assert.Contains("Tuple.of(1, \"hello\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.vavr.Tuple;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleExpression_Ternary_ProducesTupleOf()
    {
        var result = Convert(@"
class C {
    object Test() => (1, ""hello"", true);
}");
        Assert.True(result.Success);
        Assert.Contains("Tuple.of(1, \"hello\", true)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleReturnType_Binary_MapsToTuple2()
    {
        var result = Convert(@"
class C {
    (int, string) GetPair() => (1, ""hello"");
}");
        Assert.True(result.Success);
        Assert.Contains("Tuple2<Integer, String>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.vavr.Tuple2;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleReturnType_Ternary_MapsToTuple3()
    {
        var result = Convert(@"
class C {
    (int, string, bool) GetTriple() => (1, ""hello"", true);
}");
        Assert.True(result.Success);
        Assert.Contains("Tuple3<Integer, String, Boolean>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.vavr.Tuple3;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleDeconstruction_ProducesElementAccessors()
    {
        var result = Convert(@"
class C {
    (int, string) GetPair() => (1, ""hello"");
    void Test() {
        var (a, b) = GetPair();
        System.Console.WriteLine(a);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("_t._1()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_t._2()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleParameter_MapsToTupleType()
    {
        var result = Convert(@"
class C {
    void Accept((int, string) pair) {
        System.Console.WriteLine(pair);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("Tuple2<Integer, String>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleField_MapsToTupleType()
    {
        var result = Convert(@"
class C {
    (int, string) _pair;
}");
        Assert.True(result.Success);
        Assert.Contains("Tuple2<Integer, String>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleDefaultExpression_ProducesTupleOfElementDefaults()
    {
        var result = Convert(@"
class C {
    (int, string, bool) GetDefault() => default((int, string, bool));
}");
        Assert.True(result.Success);
        Assert.Contains("Tuple.of(0, null, false)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.vavr.Tuple;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Tuple3<Integer, String, Boolean>()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleExpression_NoTodoComment()
    {
        var result = Convert(@"
class C {
    object Test() => (1, 2);
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("TODO: tuple", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Object[]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemTuple_Binary_MapsToMapEntryWithoutListWrapping()
    {
        var result = Convert(@"
class C {
    System.Tuple<int, bool> GetPair() => System.Tuple.Create(1, true);
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Map.Entry<Integer, Boolean>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Map.Entry<Integer, List<Boolean>>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemTuple_InterfaceReturn_MapsToMapEntryWithoutListWrapping()
    {
        var result = Convert(@"
interface ITest {
    System.Tuple<int, bool> GetPair();
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Map.Entry<Integer, Boolean>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Map.Entry<Integer, List<Boolean>>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleLocalDeclaration_DoesNotAddClone()
    {
        var result = Convert(@"
class C {
    string Test(System.Func<string, string> evaluator) {
        var state = (segments: new System.Text.StringBuilder(), prevat: 0);
        return state.segments.ToString();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Tuple.of(new StringBuilder(), 0)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("clone()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// System.Tuple`2 maps to java.util.Map.Entry (declarations render Map.Entry,
    /// instantiations render AbstractMap.SimpleImmutableEntry), so item accessors
    /// must render getKey()/getValue() - the vavr _N() spelling only fits the
    /// vavr-mapped tuple shapes (tuple syntax / System.Tuple`3+)
    /// (real chain: DtdParser/XsdValidatingReader async out-param chains).
    /// </summary>
    [Fact]
    public void SystemTuple2_ItemAccess_UsesEntryAccessors()
    {
        var result = Convert(@"
using System;

class C
{
    int PickItem1(Tuple<int, bool> t)
    {
        return t.Item1;
    }

    bool PickItem2(Tuple<int, bool> t)
    {
        return t.Item2;
    }
}
");
        Assert.True(result.Success);
        Assert.DoesNotContain("_1()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_2()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getKey()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getValue()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tuple-syntax tuples keep mapping to io.vavr.TupleN, so their ItemN
    /// accessors stay on the vavr _N() spelling.
    /// </summary>
    [Fact]
    public void ValueTuple_ItemAccess_KeepsVavrAccessors()
    {
        var result = Convert(@"
class C
{
    int PickItem1((int, bool) t)
    {
        return t.Item1;
    }
}
");
        Assert.True(result.Success);
        Assert.Contains("_1()", result.GeneratedCode, StringComparison.Ordinal);
    }

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
