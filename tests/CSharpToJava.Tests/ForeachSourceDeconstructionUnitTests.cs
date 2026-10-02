using CSharpToJava.Core.ForeachDeconstruction;
using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C16 foreach-source M2 unit tests (design §7.1-6~8, rulings 1/2/4):
/// instance-Deconstruct residual lowering (incl. nesting/discard/annotations),
/// CS2J5420/5421/5422 residual diagnostics with strict escalation, and the
/// E07→C16 chain where tuple/KVP sites have already been lowered.
/// </summary>
public class ForeachSourceDeconstructionUnitTests
{
    private const string TypesPreamble = """
        using System.Collections.Generic;

        class Point {
            public int X;
            public string Y = "";
            public void Deconstruct(out int x, out string y) { x = X; y = Y; }
        }
        class Inner {
            public int A;
            public int B;
            public void Deconstruct(out int a, out int b) { a = A; b = B; }
        }
        class Outer {
            public int X;
            public Inner I = new Inner();
            public void Deconstruct(out int x, out Inner i) { x = X; i = I; }
        }
        class Box { public int A; public string B = ""; }
        static class BoxExt {
            public static void Deconstruct(this Box b, out int a, out string s) { a = b.A; s = b.B; }
        }
        """;

    // ===================================================================
    // Ruling 1: instance Deconstruct residual lowering
    // ===================================================================

    [Fact]
    public void M2_var_deconstruction_lowered_to_entry_Deconstruct()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Point> points) {
                    foreach (var (x, y) in points) { Use(x + y); }
                }
                void Use<T>(T v) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DeconstructResidualLowered);
        Assert.Contains("foreach (var __cs2jFsEntry in points)", result.OutputCode);
        Assert.Contains("__cs2jFsEntry.Deconstruct(out var x, out var y);", result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_mixed_annotations_are_copied_verbatim()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Point> points) {
                    foreach ((int x, var y) in points) { Use(x + y); }
                }
                void Use<T>(T v) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("__cs2jFsEntry.Deconstruct(out int x, out var y);", result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_discards_render_as_out_discard_in_both_forms()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Point> points) {
                    foreach ((int x, _) in points) { Use(x); }
                    foreach (var (x2, _) in points) { Use(x2); }
                }
                void Use<T>(T v) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.DeconstructResidualLowered);
        Assert.Contains("Deconstruct(out int x, out _);", result.OutputCode);
        Assert.Contains("Deconstruct(out var x2, out _);", result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_nested_var_designation_flattens_to_followup_Deconstruct()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Outer> outers) {
                    foreach (var (x, (a, b)) in outers) { Use(x + a + b); }
                }
                void Use<T>(T v) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains(
            "__cs2jFsEntry.Deconstruct(out var x, out var __cs2jFsInner);",
            result.OutputCode);
        Assert.Contains(
            "__cs2jFsInner.Deconstruct(out var a, out var b);",
            result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_nested_tuple_form_with_annotations_compiles()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Outer> outers) {
                    foreach ((int x, (int a, int b)) in outers) { Use(x + a + b); }
                }
                void Use<T>(T v) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("Deconstruct(out int x, out var __cs2jFsInner);", result.OutputCode);
        Assert.Contains("__cs2jFsInner.Deconstruct(out int a, out int b);", result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void M2_idempotent_after_lowering()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Point> points) {
                    foreach (var (x, y) in points) { Use(x + y); }
                }
                void Use<T>(T v) { }
            }
            """;
        var first = Preprocess(src);
        Assert.True(first.Changed);
        var second = Preprocess(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(0, second.Statistics.DeconstructResidualLowered);
    }

    // ===================================================================
    // Ruling 2: tuple residual CS2J5420 Error, node unchanged
    // ===================================================================

    [Fact]
    public void M2_tuple_residual_is_CS2J5420_Error_and_unchanged()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(List<(int x, string y)> rows) {
                    foreach (var (x, y) in rows) { Use(x + y); }
                }
                void Use<T>(T v) { }
            }
            """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.TupleResidualSeen);
        Assert.Equal(1, result.Statistics.ResidualDeconstructionSites);
        Assert.Equal(0, result.Statistics.DeconstructResidualLowered);
        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceTupleResidual);
        Assert.Equal(ForeachSourceSeverity.Error, diagnostic.Severity);
        Assert.Contains("foreach (var (x, y) in rows)", result.OutputCode);
    }

    // ===================================================================
    // Ruling 4: extension-Deconstruct residual CS2J5421 (strict escalates)
    // ===================================================================

    [Fact]
    public void M2_extension_deconstruct_residual_is_CS2J5421_Warning_unless_strict()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Box> boxes) {
                    foreach (var (a, s) in boxes) { Use(a + s); }
                }
                void Use<T>(T v) { }
            }
            """;

        var relaxed = Preprocess(src, new ForeachSourceOptions());
        Assert.False(relaxed.Changed);
        Assert.Equal(1, relaxed.Statistics.OtherResidualDiagnosed);
        Assert.Equal(1, relaxed.Statistics.ResidualDeconstructionSites);
        var warning = Assert.Single(relaxed.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceOtherResidual);
        Assert.Equal(ForeachSourceSeverity.Warning, warning.Severity);

        var strict = Preprocess(src, new ForeachSourceOptions { Strict = true });
        var error = Assert.Single(strict.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceOtherResidual);
        Assert.Equal(ForeachSourceSeverity.Error, error.Severity);
    }

    // ===================================================================
    // FS-3b: KVP residual CS2J5422, gate-exempt
    // ===================================================================

    [Fact]
    public void M2_kvp_residual_is_CS2J5422_and_exempt_from_gate_counter()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<string, int> d) {
                    foreach (var (k, v) in d) { Use(k + v); }
                }
                void Use<T>(T v) { }
            }
            """;

        var relaxed = Preprocess(src, new ForeachSourceOptions());
        Assert.False(relaxed.Changed);
        Assert.Equal(1, relaxed.Statistics.KvResidualSeen);
        Assert.Equal(0, relaxed.Statistics.ResidualDeconstructionSites);
        var warning = Assert.Single(relaxed.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceKvResidual);
        Assert.Equal(ForeachSourceSeverity.Warning, warning.Severity);

        var strict = Preprocess(src, new ForeachSourceOptions { Strict = true });
        var error = Assert.Single(strict.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceKvResidual);
        Assert.Equal(ForeachSourceSeverity.Error, error.Severity);
    }

    [Fact]
    public void M2_CS2J5423_is_never_emitted()
    {
        var src = TypesPreamble + """

            class C {
                void M(List<Point> a, List<(int x, int y)> b) {
                    foreach (var (x, y) in a) { }
                    foreach (var (p, q) in b) { }
                }
            }
            """;
        var result = Preprocess(src);

        Assert.DoesNotContain(result.Diagnostics.Select(d => d.Code),
            c => c == "CS2J5423");
    }

    // ===================================================================
    // §7.1-8: E07 runs first — tuple/KVP sites are already gone by C16
    // ===================================================================

    [Fact]
    public void M2_after_E07_no_kvp_residuals_reach_C16()
    {
        // E07 lowers KVP foreach deconstruction; bare ValueTuple
        // deconstruction is the E06+E07 chain's responsibility (covered by
        // the CS2J5420 residual test when C16 runs alone).
        var src = """
            using System.Collections.Generic;
            class C {
                void M(Dictionary<string, int> d) {
                    foreach (var pair in d) { Use(pair.Key); }
                    foreach (var (k, v) in d) { Use(k + v); }
                }
                void Use<T>(T v) { }
            }
            """;

        var e07 = PreprocessWithE07(src);
        // E07 lowers the deconstruction foreach site into an indexer loop.
        Assert.DoesNotContain("foreach (var (", e07);

        var c16 = Preprocess(e07);
        Assert.False(c16.Changed);
        Assert.Equal(0, c16.Statistics.TupleResidualSeen);
        Assert.Equal(0, c16.Statistics.KvResidualSeen);
        Assert.Equal(0, c16.Statistics.OtherResidualDiagnosed);
        Assert.Equal(0, c16.Statistics.ResidualDeconstructionSites);
        AssertNoErrors(c16);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static string PreprocessWithE07(string source)
    {
        var (tree, compilation) = Build(source);
        var e07 = new ForeachDeconstructionPreprocessor()
            .Preprocess(source, compilation.GetSemanticModel(tree));
        return e07.OutputCode;
    }

    private static ForeachSourceResult Preprocess(
        string source, ForeachSourceOptions? options = null)
    {
        var (tree, compilation) = Build(source);
        return new ForeachSourcePreprocessor()
            .Preprocess(source, compilation.GetSemanticModel(tree), options);
    }

    private static (SyntaxTree tree, CSharpCompilation compilation) Build(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(SortedDictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.ITuple).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "foreach-source-m2-test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree, compilation);
    }

    private static void AssertNoErrors(ForeachSourceResult result)
    {
        var (tree, compilation) = Build(result.OutputCode);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0,
            $"Compilation errors in output:\n{string.Join("\n", errors)}\n--- Code ---\n{result.OutputCode}");
    }
}
