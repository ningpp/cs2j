using CSharpToJava.Core.TupleToStruct;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

public class TupleToStructRewriterUnitTests
{
    private readonly ITestOutputHelper _output;

    public TupleToStructRewriterUnitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static SemanticModel ParseWithSemantics(string src, params SyntaxTree[] extraTrees)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("tuple-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static (string Output, string StructFile, List<TupleToStructDiagnostic> Diagnostics) Rewrite(
        string src)
    {
        var model = ParseWithSemantics(src);
        var preprocessor = new TupleToStructPreprocessor();
        var stats = new TupleToStructStatistics();
        var diagnostics = new List<TupleToStructDiagnostic>();
        var result = preprocessor.RewriteFile(src, model, stats, diagnostics);
        var structFile = preprocessor.GenerateStructFile();
        return (result.OutputCode, structFile ?? "", diagnostics);
    }

    /// <summary>重写结果 + 生成的 struct 文件必须能一起编译。</summary>
    private static void AssertOutputCompiles(string output, string structFile)
    {
        var outputTree = CSharpSyntaxTree.ParseText(output);
        var structTree = CSharpSyntaxTree.ParseText(structFile);
        var compilation = CSharpCompilation.Create("tuple-verify",
            new[] { outputTree, structTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output +
            "\n--- struct file ---\n" + structFile);
    }

    private static string RewriteAndVerifyCompiles(string src)
    {
        var (output, structFile, _) = Rewrite(src);
        AssertOutputCompiles(output, structFile);
        return output;
    }

    private static void AssertNoTupleSyntax(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var residual = root.DescendantNodesAndSelf()
            .Where(n => n is TupleTypeSyntax or TupleExpressionSyntax
                || (n is GenericNameSyntax g && g.Identifier.ValueText == "ValueTuple"))
            .ToList();
        Assert.True(residual.Count == 0,
            "Residual tuple syntax:\n" + string.Join("\n", residual));
    }

    // ------------------------------------------------------------------

    [Fact]
    public void CleanFile_NoTupleSyntax_Unchanged()
    {
        var src = "public class C { public int Value; public int Add(int a, int b) => a + b; }";
        var (output, structFile, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal("", structFile);
    }

    [Fact]
    public void TupleLiteralAndReturnType_ReplacedWithStruct()
    {
        var src = """
            class C
            {
                (int, string) Get() => (1, "a");
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.Contains("global::Cs2jTuples.TupleInt32String Get()", output);
        Assert.Contains("global::Cs2jTuples.TupleInt32String(1, \"a\")", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void NamedElements_MemberAccess_MappedToItemN()
    {
        var src = """
            class C
            {
                int Sum((int x, int y) p) => p.x + p.y;
                int SumInferred()
                {
                    var t = (width: 3, height: 4);
                    return t.width * t.height;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.Contains("p.Item1 + p.Item2", output);
        Assert.Contains("t.Item1 * t.Item2", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void DeconstructionDeclaration_Expanded()
    {
        var src = """
            class C
            {
                (int, int) F() => (1, 2);
                int M()
                {
                    var (a, b) = F();
                    return a + b;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.Contains("var a = ", output);
        Assert.Contains(".Item1", output);
        Assert.Contains(".Item2", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void DeconstructionAssignment_Swap_UsesTemp()
    {
        var src = """
            class C
            {
                void M()
                {
                    int a = 1, b = 2;
                    (a, b) = (b, a);
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.DoesNotContain("(a, b) =", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void OutVarTuple_AndIsVarPattern_Handled()
    {
        // 注：C# 不存在 `out var (x, y)` 内联解构（CS8199），合法形态是 out 单个元组变量，
        // 或条件中的 `is var (a, b)`。
        var src = """
            using System.Collections.Generic;

            class C
            {
                int M(Dictionary<string, (int, int)> d)
                {
                    if (d.TryGetValue("k", out var pair) && pair.Item1 > pair.Item2)
                    {
                        return pair.Item1 - pair.Item2;
                    }

                    if (Compute() is var (x, y))
                    {
                        return x - y;
                    }

                    return 0;
                }

                (int, int) Compute() => (1, 2);
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.Contains("out var pair", output);
        Assert.DoesNotContain("is var (", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void ForeachDeconstruction_Expanded()
    {
        var src = """
            using System.Collections.Generic;

            class C
            {
                int M(List<(int, int)> pairs)
                {
                    int total = 0;
                    foreach (var (a, b) in pairs)
                    {
                        total += a * b;
                    }

                    return total;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.DoesNotContain("var (a, b)", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void NestedTuple_NestedStruct()
    {
        var src = """
            class C
            {
                ((int, int) a, int b) Get() => ((1, 2), 3);
                int M() => Get().a.Item2 + Get().b;
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void EightElementTuple_RestLayoutPreserved()
    {
        var src = """
            class C
            {
                (int, int, int, int, int, int, int, int) Get() => (1, 2, 3, 4, 5, 6, 7, 8);
                int M()
                {
                    var t = Get();
                    return t.Item1 + t.Item8;
                }
            }
            """;
        var (output, structFile, _) = Rewrite(src);
        _output.WriteLine(output);
        _output.WriteLine(structFile);
        AssertOutputCompiles(output, structFile);
        AssertNoTupleSyntax(output);
        Assert.Contains(".Rest.Item1", output);
    }

    [Fact]
    public void GenericTuple_ListOfTuples()
    {
        var src = """
            using System.Collections.Generic;

            class C
            {
                List<(int, string)> Get() => new List<(int, string)>();
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.Contains("List<global::Cs2jTuples.TupleInt32String>", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void ValueTuple_ExplicitGenericAndCreate_Rewritten()
    {
        var src = """
            class C
            {
                System.ValueTuple<int, string> A() => System.ValueTuple.Create(1, "a");
                (int, string) B() => new System.ValueTuple<int, string>(2, "b");
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.DoesNotContain("ValueTuple", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void OpenGenericTuple_GenericStructGenerated()
    {
        var src = """
            class C<T>
            {
                (T, int) Get(T value) => (value, 1);
                T First((T, int) pair) => pair.Item1;
            }
            """;
        var (output, structFile, _) = Rewrite(src);
        _output.WriteLine(output);
        _output.WriteLine(structFile);
        AssertOutputCompiles(output, structFile);
        AssertNoTupleSyntax(output);
        Assert.Contains("TupleT0Int32<T>", output);
        Assert.Contains("readonly struct TupleT0Int32<__P0>", structFile);
    }

    [Fact]
    public void DiscardElements_Supported()
    {
        var src = """
            class C
            {
                (int, int, int) F() => (1, 2, 3);
                int M()
                {
                    var (a, _, c) = F();
                    return a + c;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.DoesNotContain("var (a, _, c)", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void TupleInFieldPropertyEvent_Rewritten()
    {
        var src = """
            class C
            {
                private (int count, string name) _state = (1, "x");
                public (int count, string name) State => _state;
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void ImplicitConversion_NonTupleLiteral_Wrapped()
    {
        var src = """
            class C
            {
                (long, long) A() => (1, 2);
                (long, long) B()
                {
                    (int, int) small = (3, 4);
                    return small;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void ConditionalAccess_MemberBinding_Mapped()
    {
        var src = """
            class C
            {
                int M((int x, int y)? maybe)
                {
                    return maybe?.x ?? 0;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.Contains("?.Item1", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void ITupleUsage_WarnedAndLeft()
    {
        var src = """
            using System.Runtime.CompilerServices;

            class C
            {
                int M()
                {
                    var t = ((int, int))(object)(1, 2);
                    return ((ITuple)t).Length;
                }
            }
            """;
        var (output, _, diagnostics) = Rewrite(src);
        Assert.Contains(diagnostics, d => d.Message.Contains("ITuple"));
    }

    [Fact]
    public void GeneratedStructFile_HasNamespaceEquatableAndDeconstruct()
    {
        var (_, structFile, _) = Rewrite("class C { (int, string) F() => (1, \"a\"); }");
        Assert.Contains("namespace Cs2jTuples", structFile);
        Assert.Contains("public readonly struct TupleInt32String", structFile);
        Assert.Contains("global::System.IEquatable<global::Cs2jTuples.TupleInt32String>", structFile);
        Assert.Contains("public void Deconstruct(", structFile);
        Assert.Contains("public override global::System.Int32 GetHashCode()", structFile);
        Assert.Contains("public override global::System.String ToString()", structFile);
        Assert.DoesNotContain("operator ==", structFile);
    }

    [Fact]
    public void SameShapeAcrossMethods_DeduplicatedToSingleStruct()
    {
        var src = """
            class C
            {
                (int, string) A() => (1, "a");
                (int count, string name) B() => (2, "b");
                (int, string, int) C() => (1, "c", 3);
            }
            """;
        var (_, structFile, _) = Rewrite(src);
        Assert.Equal(1, CountOccurrences(structFile, "public readonly struct TupleInt32String(")
            + CountOccurrences(structFile, "public readonly struct TupleInt32String\n")
            + CountOccurrences(structFile, "public readonly struct TupleInt32String :"));
        Assert.Contains("TupleInt32StringInt32", structFile);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void TupleEquality_EqualsSemanticsPreserved()
    {
        var src = """
            class C
            {
                bool M((int, string) a, (int, string) b) => a.Equals(b);
                bool N((int, string) a) => a.GetHashCode() != 0;
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void TupleDeconstructionDeclaration_OfTupleElement_Expanded()
    {
        var src = """
            using System.Collections.Generic;

            class C
            {
                int M(List<(int a, (int b, int c) nested)> items)
                {
                    int total = 0;
                    foreach (var item in items)
                    {
                        var (a, nested) = item;
                        var (b, c) = nested;
                        total += a + b + c;
                    }

                    return total;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        AssertNoTupleSyntax(output);
        Assert.Contains(".Item2", output);
    }

    [Fact]
    public void NonTupleDeconstruction_KeyValuePair_LeftAlone()
    {
        // KeyValuePair.Deconstruct 不是元组解构，必须原样保留。
        var src = """
            using System.Collections.Generic;

            class C
            {
                int M(Dictionary<string, int> d)
                {
                    int total = 0;
                    foreach (var (k, v) in d)
                    {
                        total += v;
                    }

                    return total;
                }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("foreach (var (k, v) in d)", output);
    }

    [Fact]
    public void SwitchStatementOnTuple_Compiles()
    {
        var src = """
            class C
            {
                string M((int, string) t)
                {
                    switch (t)
                    {
                        case (1, "one"):
                            return "1";
                        default:
                            return "other";
                    }
                }
            }
            """;
        var (output, structFile, diagnostics) = Rewrite(src);
        _output.WriteLine(output);
        AssertOutputCompiles(output, structFile);
    }

    [Fact]
    public void TupleElementNamesAttribute_Removed()
    {
        var src = """
            using System.Runtime.CompilerServices;

            class C
            {
                [return: TupleElementNames(new[] { "a", "b" })]
                (int, int) F() => (1, 2);
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.DoesNotContain("TupleElementNames", output);
    }

    [Fact]
    public void VarDeconstructionInIfPattern_Expanded()
    {
        var src = """
            class C
            {
                (int, int) F() => (1, 2);
                int M()
                {
                    if (F() is var (a, b))
                    {
                        return a + b;
                    }

                    return 0;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.DoesNotContain("is var (", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void LambdaReturningTuple_Rewritten()
    {
        var src = """
            using System.Linq;
            using System.Collections.Generic;

            class C
            {
                object M(List<int> xs) => xs.Select(x => (x, x.ToString())).First();
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void NestedTupleDeconstruction_Recursive()
    {
        var src = """
            class C
            {
                ((int, int), int) F() => ((1, 2), 3);
                int M()
                {
                    var ((a, b), c) = F();
                    return a + b + c;
                }
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        Assert.DoesNotContain("var ((a, b), c)", output);
        AssertNoTupleSyntax(output);
    }

    [Fact]
    public void DefaultAndNullableTuple_Supported()
    {
        var src = """
            class C
            {
                (int, string) Default() => default;
                (int, string) Nullable((int, string)? maybe) => maybe ?? default((int, string));
                bool Compare((int, string) a, (int, string) b) => a == b;
            }
            """;
        var output = RewriteAndVerifyCompiles(src);
        AssertNoTupleSyntax(output);
    }
}
