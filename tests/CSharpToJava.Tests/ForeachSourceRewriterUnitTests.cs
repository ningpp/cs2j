using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C16 foreach-source Core unit tests (design §7.1 cases 1-5, M1 surface):
/// FS-1 string wrapping, zero collateral damage, idempotence, FS-4 guards
/// and ruling 6 (explicit non-char element conversion).
/// </summary>
public class ForeachSourceRewriterUnitTests
{
    // ===================================================================
    // §7.1-1 FS-1: foreach over string in all source/element shapes
    // ===================================================================

    [Fact]
    public void FS1_wraps_param_field_property_methodreturn_literal_concat_sources()
    {
        var src = """
            using System.Collections.Generic;
            class C {
                private readonly string _field = "f";
                private string Prop => "p";
                void M(string param) {
                    foreach (var c in param) { Use(c); }
                    foreach (var c in _field) { Use(c); }
                    foreach (var c in Prop) { Use(c); }
                    foreach (var c in GetText()) { Use(c); }
                    foreach (var c in "literal") { Use(c); }
                    foreach (var c in "a" + param) { Use(c); }
                }
                string GetText() => "";
                void Use<T>(T x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(6, result.Statistics.StringForeachWrapped);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Equal(0, result.Statistics.SitesPassedThrough);
        Assert.Contains("param.ToCharArray()", result.OutputCode);
        Assert.Contains("_field.ToCharArray()", result.OutputCode);
        Assert.Contains("Prop.ToCharArray()", result.OutputCode);
        Assert.Contains("GetText().ToCharArray()", result.OutputCode);
        Assert.Contains("\"literal\".ToCharArray()", result.OutputCode);
        Assert.Contains("(\"a\" + param).ToCharArray()", result.OutputCode);
        AssertNoErrors(result);
    }

    [Theory]
    [InlineData("var c")]
    [InlineData("char c")]
    [InlineData("int c")]
    [InlineData("object c")]
    [InlineData("System.IComparable c")]
    public void FS1_element_shapes_compile_and_keep_single_evaluation(string head)
    {
        var src = $$"""
            class C {
                string GetText() => "abc";
                void M() {
                    foreach ({{head}} in GetText()) { Use(c); }
                }
                void Use<T>(T x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("GetText().ToCharArray()", result.OutputCode);
        // FS-1 must not introduce a temporary for the source expression:
        // GetText() stays evaluated exactly once (inside the in-clause).
        Assert.DoesNotContain("__cs2jFsEntry", result.OutputCode);
        AssertNoErrors(result);
    }

    // ===================================================================
    // §7.1-2 zero collateral damage
    // ===================================================================

    [Fact]
    public void FS1_leaves_nonstring_sources_unchanged()
    {
        var src = """
            using System.Collections;
            using System.Collections.Generic;

            class DuckEnumerator : IEnumerator {
                public object Current => new object();
                public bool MoveNext() => false;
                public void Reset() { }
            }
            class Duck { public DuckEnumerator GetEnumerator() => new DuckEnumerator(); }
            static class DuckExt {
                public static DuckEnumerator GetEnumerator(this int i) => new DuckEnumerator();
            }
            class C {
                IEnumerable GetNonGeneric() => new int[0];
                void M(List<char> list, char[] arr, string[] strs, IEnumerable<char> genericChars, Duck duck, int ext) {
                    foreach (var c in genericChars) { }
                    foreach (var c in arr) { }
                    foreach (var c in list) { }
                    foreach (var s in strs) { }
                    foreach (var c in GetNonGeneric()) { }
                    foreach (var c in duck) { }
                    foreach (var c in ext) { }
                }
            }
            """;
        var result = Preprocess(src);

        // C40: the duck instance site and the extension GetEnumerator site are
        // now FA-1/FA-2 lowered (5 collection sites still pass through;
        // arrays/List/IEnumerable stay with the converter path).
        Assert.True(result.Changed);
        Assert.DoesNotContain("ToCharArray()", result.OutputCode);
        Assert.Equal(5, result.Statistics.SitesPassedThrough);
        Assert.Equal(0, result.Statistics.StringForeachWrapped);
        Assert.Equal(2, result.Statistics.DuckEnumeratorSitesLowered);
        Assert.Equal(1, result.Statistics.ExtensionGetEnumeratorSites);
        // DuckEnumerator implements only the non-generic IEnumerator (which on
        // net10 does not list IDisposable) and has no Dispose() ⇒ bare while.
        Assert.Equal(2, result.Statistics.NoDisposeSites);
        Assert.DoesNotContain("finally", result.OutputCode);
        AssertNoErrors(result);
    }

    // ===================================================================
    // §7.1-3 idempotence and verbatim clean files
    // ===================================================================

    [Fact]
    public void FS1_second_pass_is_a_noop()
    {
        var src = """
            class C {
                string GetText() => "abc";
                void M(string s) {
                    foreach (var a in s) { Use(a); }
                    foreach (int b in GetText()) { Use(b); }
                }
                void Use<T>(T x) { }
            }
            """;
        var first = Preprocess(src);
        Assert.True(first.Changed);

        var second = Preprocess(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.StringForeachWrapped);
        AssertNoErrors(first);
    }

    [Fact]
    public void File_without_foreach_is_returned_byte_identical()
    {
        var src = "class C { int M() => 42; }\r\n";
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(src, result.OutputCode);
    }

    // ===================================================================
    // §7.1-4 FS-4 guards (await; ref sites moved to the M3 FR chain —
    // CS2J5480/5481 replace the old CS2J5441 counted-untouched behavior)
    // ===================================================================

    [Fact]
    public void FS4_ref_foreach_is_lowered_to_index_loop_under_5480()
    {
        var src = """
            using System;
            class C {
                void M(Span<int> s) {
                    foreach (ref var x in s) { x = 1; }
                }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RefIndexLoopSites);
        Assert.Equal(0, result.Statistics.RefForeachSeen);
        Assert.Contains("__cs2jFrSrc", result.OutputCode);
        Assert.Contains("ref int x = ref __cs2jFrSrc", result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceRefIndexLoop);
        AssertNoErrors(result);
    }

    [Fact]
    public void FS4_ref_readonly_foreach_is_lowered_to_index_loop_under_5481()
    {
        var src = """
            using System;
            class C {
                void M(Span<int> s) {
                    foreach (ref readonly var x in s) { Use(x); }
                }
                void Use<T>(T x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RefReadonlySites);
        Assert.Equal(0, result.Statistics.RefForeachSeen);
        Assert.Contains("ref readonly int x = ref __cs2jFrSrc", result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceRefReadonlyIndexLoop);
        AssertNoErrors(result);
    }

    [Fact]
    public void FS4_await_foreach_is_counted_and_left_untouched()
    {
        // Parsed against the real async-streams surface; semantic binding is
        // intentionally not required for the syntactic await guard.
        var src = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C {
                async Task M(IAsyncEnumerable<int> e) {
                    await foreach (var x in e) { Use(x); }
                }
                void Use<T>(T x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.AwaitForeachSeen);
        Assert.Equal(0, result.Statistics.StringForeachWrapped);
        Assert.Contains(result.Diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.ForeachSourceAwaitForeach);
    }

    [Fact]
    public void Null_model_never_throws_and_leaves_source_unchanged()
    {
        var src = """
            class C { void M(string s) { foreach (var c in s) { } } }
            """;
        var preprocessor = new ForeachSourcePreprocessor();
        var result = preprocessor.Preprocess(src, semanticModel: null);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Empty(result.Diagnostics);
    }

    // ===================================================================
    // §7.1-5 ruling 6: explicit non-char element conversion
    // ===================================================================

    [Theory]
    [InlineData("int", "(int)")]
    [InlineData("object", "(object)")]
    [InlineData("System.IComparable", "(System.IComparable)")]
    public void Ruling6_standard_conversion_targets_get_char_temp_loop_with_cast(string typeText, string cast)
    {
        var src = $$"""
            class C {
                void M(string s) {
                    foreach ({{typeText}} c in s) { Use(c); }
                }
                void Use<T>(T x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("foreach (char __cs2jFsCh in s.ToCharArray())", result.OutputCode);
        Assert.Contains($"{typeText} c = {cast}__cs2jFsCh;", result.OutputCode);
        // Original loop variable must not be re-declared by the generated loop.
        Assert.DoesNotContain($"foreach ({typeText} c in", result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void Ruling6_char_temp_name_is_freshened_on_collision()
    {
        var src = """
            class C {
                void M(string s) {
                    foreach (int c in s) {
                        char __cs2jFsCh = 'z';
                        Use(c + __cs2jFsCh);
                    }
                }
                void Use<T>(T x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("foreach (char __cs2jFsCh1 in s.ToCharArray())", result.OutputCode);
        Assert.Contains("int c = (int)__cs2jFsCh1;", result.OutputCode);
        AssertNoErrors(result);
    }

    [Fact]
    public void Ruling6_target_without_standard_conversion_keeps_baseline_compile_error_via_plain_wrap()
    {
        // char -> FsNoWay has no conversion at all (verified against the
        // Roslyn conversion API): the baseline source fails to compile, and
        // FS-1 must neither introduce a cast nor remove that error. Plain
        // wrapping still happens (Changed=true, StringForeachWrapped=1).
        var src = """
            sealed class FsNoWay { }
            class C {
                void M(string s) {
                    foreach (FsNoWay c in s) { Use(c); }
                }
                void Use<T>(T x) { }
            }
            """;
        var sourceErrors = CompileErrors(src);
        Assert.NotEmpty(sourceErrors);

        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.StringForeachWrapped);
        Assert.Contains("in s.ToCharArray()", result.OutputCode);
        Assert.DoesNotContain("(FsNoWay)", result.OutputCode);
        Assert.DoesNotContain("FsNoWay c =", result.OutputCode);

        var outputErrors = CompileErrors(result.OutputCode);
        Assert.NotEmpty(outputErrors);
    }

    [Fact]
    public void Ruling6_enum_target_has_explicit_enum_conversion_and_cast_is_inserted()
    {
        // Documents actual Roslyn behavior: char -> enum is a standard
        // explicit enumeration conversion (the design doc's StringComparison
        // example does compile in a foreach), so the cast form is used.
        var src = """
            using System;
            class C {
                void M(string s) {
                    foreach (StringComparison c in s) { Use(c); }
                }
                void Use<T>(T x) { }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("foreach (char __cs2jFsCh in s.ToCharArray())", result.OutputCode);
        Assert.Contains("StringComparison c = (StringComparison)__cs2jFsCh;", result.OutputCode);
        AssertNoErrors(result);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static ForeachSourceResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "foreach-source-test",
            new[] { tree },
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new ForeachSourcePreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static MetadataReference[] RuntimeReferences() => new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(SortedDictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.ITuple).Assembly.Location),
    };

    private static void AssertNoErrors(ForeachSourceResult result)
    {
        var errors = CompileErrors(result.OutputCode);
        Assert.True(errors.Count == 0,
            $"Compilation errors in output:\n{string.Join("\n", errors)}\n--- Code ---\n{result.OutputCode}");
    }

    private static List<string> CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "foreach-source-verify",
            new[] { tree },
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
