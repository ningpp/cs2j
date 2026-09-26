using CSharpToJava.Core.ForeachDeconstruction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class ForeachDeconstructionTests
{
    // ===================================================================
    // Model tests
    // ===================================================================

    [Fact]
    public void Options_Default_NotStrict_ReportInfo()
    {
        var o = new ForeachDeconstructionOptions();
        Assert.False(o.Strict);
        Assert.True(o.ReportInfo);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new ForeachDeconstructionStatistics();
        Assert.Equal(0, s.FilesScanned);
        Assert.Equal(0, s.ForeachDeconstructionsLowered);
    }

    // ===================================================================
    // 1. Basic KV: foreach ((int x, int y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_BasicKv_TupleExpressionWithTypes()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int x, string y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("__entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("__entry.Value", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("int x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("string y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 2. var form: foreach (var (x, y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_VarDesignation_Form()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach (var (x, y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 3. Tuple-expr var: foreach ((var x, var y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_TupleExpressionVar_Form()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((var x, var y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 4. Inferred form: foreach ((x, y) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_InferredForm_UsesVar()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((x, y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var y = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 5. Discard: foreach ((int x, _) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_Discard_OnlyKeyDeclared()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int x, _) in d) {
                        Console.WriteLine(x);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("__entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 6. Whole-binding: foreach ((int k, var whole) in dict)
    // ===================================================================

    [Fact]
    public void Lowers_WholeBinding_ValueAssignedToVariable()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int k, var whole) in d) {
                        Console.WriteLine(k + whole);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int k = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var whole = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 7. await foreach: preserve await keyword
    // ===================================================================

    [Fact]
    public void Lowers_AwaitForeach_PreservesAwait()
    {
        var src = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C {
                async Task M(IAsyncEnumerable<KeyValuePair<int, string>> d) {
                    await foreach (var (x, y) in d) {
                        Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("await foreach", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 8. Deconstruct family: skipped, Changed=false
    // ===================================================================

    [Fact]
    public void Skips_DeconstructFamily_EmitsInfo()
    {
        var src = """
            class Point {
                public int X, Y;
                public void Deconstruct(out int x, out int y) { x = X; y = Y; }
            }
            class C {
                void M(System.Collections.Generic.List<Point> pts) {
                    foreach (var (x, y) in pts) {
                        System.Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == ForeachDeconstructionSeverity.Info);
    }

    // ===================================================================
    // 9. Non-deconstruction foreach: Changed=false
    // ===================================================================

    [Fact]
    public void NonDeconstructionForeach_NotChanged()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(List<int> list) {
                    foreach (var item in list) {
                        Console.WriteLine(item);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    // ===================================================================
    // 10. No deconstruction in file: byte-identical output
    // ===================================================================

    [Fact]
    public void NoDeconstruction_FileOutputEqualsInput()
    {
        var src = "class C { void M() { System.Console.WriteLine(42); } }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    // ===================================================================
    // 11. Body is single statement (non-block): wrapped to block
    // ===================================================================

    [Fact]
    public void Lowers_NonBlockBody_WrapsInBlock()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    foreach ((int x, string y) in d)
                        System.Console.WriteLine(x + y);
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 12. __entry already in scope: unique suffix
    // ===================================================================

    [Fact]
    public void Lowers_WhenEntryNameInScope_GeneratesUniqueSuffix()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d) {
                    int __entry = 0;
                    foreach ((int x, string y) in d) {
                        System.Console.WriteLine(x + y + __entry);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Should use __entry1 (or higher) to avoid collision with existing __entry.
        Assert.Contains("__entry1.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 13. Nested deconstruction foreach in body: independent names
    // ===================================================================

    [Fact]
    public void Lowers_NestedDeconstruction_IndependentEntryNames()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<int, string> d1, Dictionary<int, string> d2) {
                    foreach ((int x, string y) in d1) {
                        foreach ((int a, string b) in d2) {
                            System.Console.WriteLine(x + y + a + b);
                        }
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Both should have __entry (or __entry1) — the outer and inner get independent names.
        Assert.Contains("__entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 14. IEnumerable<KeyValuePair<K,V>> source: KV lowering
    // ===================================================================

    [Fact]
    public void Lowers_IEnumerableKvSource_KvLowering()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(IEnumerable<KeyValuePair<int, string>> items) {
                    foreach ((int k, string v) in items) {
                        System.Console.WriteLine(k + v);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("int k = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("string v = __entry.Value", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 15. SortedDictionary / ConcurrentDictionary: KV lowering
    // ===================================================================

    [Fact]
    public void Lowers_SortedDictionary_KvLowering()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(SortedDictionary<int, string> d) {
                    foreach (var (x, y) in d) {
                        System.Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Lowers_ConcurrentDictionary_KvLowering()
    {
        var src = """
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            class C {
                void M(ConcurrentDictionary<int, string> d) {
                    foreach (var (x, y) in d) {
                        System.Console.WriteLine(x + y);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("var x = __entry.Key", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 16. Non-deconstruction ref usage: not rewritten
    // ===================================================================

    [Fact]
    public void Skips_NonDeconstructionRef_NotRewritten()
    {
        // No ForEachVariableStatementSyntax at all → Changed=false.
        var src = """
            using System;
            using System.Collections.Generic;
            class C {
                void M(List<int> d) {
                    foreach (var item in d) {
                        Console.WriteLine(item);
                    }
                }
            }
            """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static ForeachDeconstructionResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "foreach-deconstruction-test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ForeachDeconstructionPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static void AssertNoSyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            $"Syntax errors in output:\n{string.Join("\n", errors.Select(e => e.ToString()))}\n--- Code ---\n{code}");
    }
}
