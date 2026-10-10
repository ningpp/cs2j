using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Interpolations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C23 interpolation-expansion Part A + Part B unit tests (design
/// docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md §4):
/// IE-FOLD (CS2J0503), IE-CONCAT (CS2J0501), IE-FORMAT (CS2J0502 — composite
/// escaping, alignment/format passthrough, non-whitelist IFormattable upgrade,
/// CS2J0514), handler five-way hard gate G0 and the de-DE/custom-provider
/// runtime oracle (native interpolation vs preprocessed product emitted and
/// invoked via reflection). All API shapes were locked against Roslyn 5.0.0 by
/// earlier probes; the load-bearing deviations from the paper design are
/// asserted here:
/// (a) handler/FormattableString binding gates run BEFORE the constant
/// short-circuit (GetConstantValue lies for handler-bound text-only sites);
/// (b) only string-constant holes fold — <c>$"{1}"</c> is CS0133-as-constant
/// in the current compiler and must take the Concat path.
/// </summary>
public class InterpolationExpandUnitTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(
            Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Linq.dll")),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Span<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.FormattableString).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(System.Runtime.CompilerServices.RuntimeFeature).Assembly.Location),
        MetadataReference.CreateFromFile(
            Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    };

    private static string Normalize(string code)
        => Regex.Replace(code, @"\s+", " ");

    private static (CSharpCompilation compilation, SemanticModel model) BuildCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c23-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(
            output, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c23-out-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            "preprocessed output does not compile: " +
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    private static ExpressionDesugarResult Preprocess(
        string source, InterpolationOptions? interpolation = null)
    {
        var (_, model) = BuildCompilation(source);
        var options = new ExpressionDesugarOptions
        {
            Interpolations = interpolation ?? new InterpolationOptions { Enabled = true },
        };
        return new ExpressionDesugarPreprocessor().Preprocess(source, model, options);
    }

    private static string RunStaticMethod(string source, string typeName, string methodName = "Run")
    {
        var (compilation, _) = BuildCompilation(source);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(
            emit.Success,
            string.Join("; ", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Seek(0, SeekOrigin.Begin);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
        var type = assembly.GetType(typeName);
        Assert.NotNull(type);
        var method = type!.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    /// <summary>Strict default options for a source expected to rewrite.</summary>
    private static readonly InterpolationOptions Enabled =
        new() { Enabled = true };

    // ------------------------------------------------------------------
    // IE-FOLD (CS2J0503) — only string-constant holes fold.
    // ------------------------------------------------------------------

    [Fact]
    public void Fold_EmptyInterpolation_BecomesBareLiteral()
    {
        const string source = """
            public static class C { public static string M() => $""; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains("=> \"\"", Normalize(result.OutputCode));
        Assert.DoesNotContain("$\"", result.OutputCode);
        Assert.Equal(1, result.Statistics.Interpolations.ConstantsFolded);
        Assert.Equal(0, result.Statistics.Interpolations.ConcatExpansions);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Fold_PureText_BecomesBareLiteral()
    {
        const string source = """
            public static class C { public static string M() => $"abc"; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("=> \"abc\"", Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.ConstantsFolded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Fold_StringConstantHoles_AndConstLocal_FoldWholeSite()
    {
        const string source = """
            public static class C
            {
                public static string M()
                {
                    const string w = "W";
                    return $"a{"b"}-{w}";
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("\"ab-W\"", result.OutputCode);
        Assert.Equal(1, result.Statistics.Interpolations.ConstantsFolded);
        Assert.Equal(0, result.Statistics.Interpolations.ConcatExpansions);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Fold_NumericHole_IsNotConstant_TakesConcatPath()
    {
        // Probe-locked: the current compiler refuses $"{42}" as a compile-time
        // constant (CS0133); GetConstantValue.HasValue is false, so IE-CONCAT
        // must handle it (no IE-FOLD).
        const string source = """
            public static class C { public static string M() => $"{1}"; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("string.Concat(1)", Normalize(result.OutputCode));
        Assert.Equal(0, result.Statistics.Interpolations.ConstantsFolded);
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FoldDisabled_ConstantSite_TakesConcatRendering()
    {
        const string source = """
            public static class C { public static string M() => $"abc"; }
            """;
        var options = new InterpolationOptions
        {
            Enabled = true,
            FoldConstants = false,
        };

        var result = Preprocess(source, options);

        Assert.True(result.Changed);
        Assert.Contains("=> \"abc\"", Normalize(result.OutputCode));
        Assert.Equal(0, result.Statistics.Interpolations.ConstantsFolded);
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        AssertOutputCompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // IE-CONCAT (CS2J0501) — type matrix, wrapping, empty segments.
    // ------------------------------------------------------------------

    [Fact]
    public void Concat_IntStringBoolDoubleEnum_AllPassThroughUnwrapped()
    {
        const string source = """
            public enum Color { Red, Green }
            public static class C
            {
                public static string M(int i, string s, bool b, double d, Color e)
                    => $"{i}|{s}|{b}|{d}|{e}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains(
            "string.Concat(i, \"|\", s, \"|\", b, \"|\", d, \"|\", e)",
            Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        Assert.Equal(0, result.Statistics.Interpolations.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Concat_NullStringHole_HasNoNullCoalescing()
    {
        const string source = """
            public static class C { public static string M() => $"a{(string)null}b"; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("string.Concat(\"a\", (string)null, \"b\")", normalized);
        Assert.DoesNotContain("??", normalized);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("ab", RunStaticMethod(result.OutputCode, "C", "M"));
    }

    [Fact]
    public void Concat_UnsignedHoles_WrapPerUnsignedContract()
    {
        const string source = """
            public static class C
            {
                public static string M(byte b, ushort us, uint u, ulong ul)
                    => $"{b}-{us}-{u}-{ul}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("((uint)b).ToString()", normalized);
        Assert.Contains("((uint)us).ToString()", normalized);
        Assert.Contains("u.ToString()", normalized);
        Assert.Contains("ul.ToString()", normalized);
        Assert.Equal(4, result.Statistics.Interpolations.UnsignedHoleWraps);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Concat_ReadOnlySpanChar_WithOwnToString_EmitsToString()
    {
        const string source = """
            using System;
            public static class C
            {
                public static string M(ReadOnlySpan<char> sp) => $"{sp}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains("sp.ToString()", Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.RefStructToStringWraps);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Concat_ConsecutiveHoles_DropEmptyTextSegments()
    {
        const string source = """
            public static class C { public static string M(int a, int b) => $"{a}{b}"; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("string.Concat(a, b)", Normalize(result.OutputCode));
        Assert.DoesNotContain("\"\"", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Concat_HoleExpressions_EvaluatedOnceEach_InSourceOrder()
    {
        const string source = """
            public static class Probe
            {
                public static int Calls;
                public static int Next() { Calls++; return 7; }
                public static string Run()
                {
                    Calls = 0;
                    var s = $"{Next()}-{Next()}";
                    return s + "|" + Calls;
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("string.Concat(Next(), \"-\", Next())", normalized);
        // Three textual occurrences total: the method declaration plus the
        // two call sites — a temp-introducing duplication would make four.
        Assert.Equal(3, Regex.Matches(normalized, "Next\\(\\)").Count);
        Assert.Equal(0, result.Statistics.Interpolations.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("7-7|2", RunStaticMethod(source, "Probe"));
        Assert.Equal("7-7|2", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void Concat_NestedInterpolation_InnerExpandsFirst_OuterWrapsIt()
    {
        const string source = """
            public static class Probe
            {
                public static string Run() => $"a{$"b{1}"}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains(
            "string.Concat(\"a\", string.Concat(\"b\", 1))",
            Normalize(result.OutputCode));
        Assert.Equal(2, result.Statistics.Interpolations.InterpolationSites);
        Assert.Equal(1, result.Statistics.Interpolations.NestedInterpolationSites);
        Assert.Equal(2, result.Statistics.Interpolations.ConcatExpansions);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("ab1", RunStaticMethod(source, "Probe"));
        Assert.Equal("ab1", RunStaticMethod(result.OutputCode, "Probe"));
    }

    // ------------------------------------------------------------------
    // CS2J0513 — non-materializable ref struct / pointer holes.
    // ------------------------------------------------------------------

    [Fact]
    public void Gate_PointerHole_Strict0513_AtomicPreserve()
    {
        const string source = """
            public static class C
            {
                public static unsafe string M(int* p) => $"v={p}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = result.Diagnostics
            .Single(d => d.Code == InterpolationDiagnosticCodes.CS2J0513);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.Interpolations.RefStructOrPointerRejections);
    }

    [Fact]
    public void Gate_UnknownRefStructHole_Strict0513_AtomicPreserve()
    {
        const string source = """
            public ref struct Box { public int X; }
            public static class C
            {
                public static string M() => $"v={new Box()}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = result.Diagnostics
            .Single(d => d.Code == InterpolationDiagnosticCodes.CS2J0513);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Contains("Box", diagnostic.Message);
        Assert.Equal(1, result.Statistics.Interpolations.RefStructOrPointerRejections);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT (CS2J0502) — real string.Format emission (Part B).
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="methodName"/> on the ORIGINAL source and on the
    /// preprocessed output (both emitted and invoked via reflection), and
    /// asserts the two results are string-equal — the .NET-side culture/
    /// alignment/format/null oracle (design §7.3). Returns the output code for
    /// further textual assertions.
    /// </summary>
    private static string AssertNativeEqualsPreprocessed(
        string source,
        string typeName,
        string methodName = "Run",
        string? expected = null)
    {
        var native = RunStaticMethod(source, typeName, methodName);
        var result = Preprocess(source, Enabled);
        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var preprocessed = RunStaticMethod(result.OutputCode, typeName, methodName);
        Assert.Equal(native, preprocessed);
        if (expected != null)
            Assert.Equal(expected, preprocessed);
        return result.OutputCode;
    }

    [Fact]
    public void Format_MixedTriggers_OneStringFormat_AllThreeSubBuckets()
    {
        const string source = """
            using System;
            public static class C
            {
                public static string M(int x, DateTime dt)
                    => $"{x:D2}|{x,5}|{dt:yyyy-MM-dd}|{dt}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains(
            "string.Format(\"{0:D2}|{1,5}|{2:yyyy-MM-dd}|{3}\", x, x, dt, dt)",
            Normalize(result.OutputCode));
        var stats = result.Statistics.Interpolations;
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.FormatByAlignment);
        Assert.Equal(1, stats.FormatByFormatClause);
        Assert.Equal(1, stats.FormatByIFormattableUpgrade);
        Assert.Equal(0, stats.ConcatExpansions);
        Assert.Equal(0, stats.TempsLifted);
        var diagnostic = result.Diagnostics.Single(d =>
            d.Code == InterpolationDiagnosticCodes.CS2J0502);
        Assert.Equal(ExpressionDesugarSeverity.Info, diagnostic.Severity);
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain("InvariantCulture", result.OutputCode);
    }

    [Fact]
    public void Format_SiblingConcatSiteRewrites_AndSecondRunIsZeroDiff()
    {
        const string source = """
            using System;
            public static class C
            {
                public static string M(int x, DateTime dt)
                {
                    var a = $"n{x}";
                    var b = $"{dt:u}";
                    return a + b;
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("string.Concat(\"n\", x)", normalized);
        Assert.Contains("string.Format(\"{0:u}\", dt)", normalized);
        Assert.DoesNotContain("$\"", normalized);
        var stats = result.Statistics.Interpolations;
        Assert.Equal(1, stats.ConcatExpansions);
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.FormatByFormatClause);
        Assert.Equal(0, stats.ResidualInterpolationCount);
        AssertOutputCompiles(result.OutputCode);

        // Second run: no interpolation nodes remain -> clean skip, byte
        // identical output, zero diagnostics.
        var second = Preprocess(result.OutputCode, Enabled);
        Assert.False(second.Changed);
        Assert.Equal(result.OutputCode, second.OutputCode);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT selection — clause triggers and the IFormattable upgrade.
    // ------------------------------------------------------------------

    [Fact]
    public void Format_FormatClauseOnly_RoutesToFormat_FormatClauseBucket()
    {
        const string source = """
            public static class C { public static string M(int x) => $"{x:D2}"; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("string.Format(\"{0:D2}\", x)", Normalize(result.OutputCode));
        var stats = result.Statistics.Interpolations;
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.FormatByFormatClause);
        Assert.Equal(0, stats.FormatByAlignment);
        Assert.Equal(0, stats.FormatByIFormattableUpgrade);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Format_AlignmentOnly_RoutesToFormat_AlignmentBucket()
    {
        const string source = """
            public static class C { public static string M(int x) => $"{x,5}"; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("string.Format(\"{0,5}\", x)", Normalize(result.OutputCode));
        var stats = result.Statistics.Interpolations;
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.FormatByAlignment);
        Assert.Equal(0, stats.FormatByFormatClause);
        Assert.Equal(0, stats.FormatByIFormattableUpgrade);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Format_AlignmentAndFormat_BothBuckets_SingleExpansion()
    {
        const string source = """
            public static class C { public static string M(int x) => $"{x,-8:X}"; }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("string.Format(\"{0,-8:X}\", x)", Normalize(result.OutputCode));
        var stats = result.Statistics.Interpolations;
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.FormatByAlignment);
        Assert.Equal(1, stats.FormatByFormatClause);
        Assert.Equal(0, stats.FormatByIFormattableUpgrade);
        Assert.Equal(0, stats.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Format_ExplicitIFormattable_UpgradesWholeSite_AndDispatchesViaProvider()
    {
        // Explicit interface implementation: parameterless ToString() and
        // IFormattable.ToString(null, provider) return DIFFERENT strings. The
        // default handler (and string.Format) dispatch via the provider;
        // string.Concat would call the parameterless one. Whitelist siblings
        // in the same site still become Format arguments.
        const string source = """
            using System;
            public sealed class ProviderProbe : IFormattable
            {
                public override string ToString() => "PLAIN";
                string IFormattable.ToString(string? format, IFormatProvider? provider)
                    => format == null ? "PROVIDED" : "PROVIDED:" + format;
            }
            public static class P
            {
                public static string Run() => $"p={new ProviderProbe()}|n={1}";
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "p=PROVIDED|n=1");
        Assert.Contains(
            "string.Format(\"p={0}|n={1}\", new ProviderProbe(), 1)",
            Normalize(output));
        var stats = Preprocess(source, Enabled).Statistics.Interpolations;
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.FormatByIFormattableUpgrade);
        Assert.Equal(0, stats.FormatByFormatClause);
        Assert.Equal(0, stats.FormatByAlignment);
    }

    [Fact]
    public void Format_DateTimeGuidTimeSpan_Upgrade_CharAndPrimitivesStayConcat()
    {
        const string source = """
            using System;
            public static class C
            {
                public static string Fmt(DateTime dt, Guid g, TimeSpan ts)
                    => $"{dt}|{g}|{ts}";
                public static string Ch(char c, bool b, double d) => $"{c}-{b}-{d}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("string.Format(\"{0}|{1}|{2}\", dt, g, ts)", normalized);
        Assert.Contains("string.Concat(c, \"-\", b, \"-\", d)", normalized);
        var stats = result.Statistics.Interpolations;
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.FormatByIFormattableUpgrade);
        Assert.Equal(1, stats.ConcatExpansions);
        AssertOutputCompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT composite escaping — braces, percent, quotes, escapes.
    // ------------------------------------------------------------------

    [Fact]
    public void Concat_LiteralBraces_AreUnescaped_RuntimeEqual()
    {
        // Shared text decoding: on regular interpolations ValueText keeps the
        // lexical brace doubling, so the Concat literals must unescape it or
        // the runtime string gains an extra pair of braces.
        const string source = """
            public static class P
            {
                public static string Run() => $"{{a}}-{42}-}}{{";
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "{a}-42-}{");
        Assert.Contains("string.Concat(\"{a}-\", 42, \"-}{\")", Normalize(output));
        Assert.Equal(1, Preprocess(source, Enabled).Statistics.Interpolations.ConcatExpansions);
    }

    [Fact]
    public void Format_LiteralBraces_AreDoubled_PercentStaysUntouched()
    {
        const string source = """
            public static class P
            {
                public static string Run(int x, int p) => $"{{not}}: {x:D2} ({p}%)";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains(
            "string.Format(\"{{not}}: {0:D2} ({1}%)\", x, p)",
            Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.FormatExpansions);
        AssertOutputCompiles(result.OutputCode);

        var runnable = """
            public static class P
            {
                public static string Run() => $"{{not}}: {7:D2} ({50}%)";
            }
            """;
        AssertNativeEqualsPreprocessed(runnable, "P", expected: "{not}: 07 (50%)");
    }

    [Fact]
    public void Format_QuoteBackslashNewlineText_FactoryEscapes_RuntimeEqual()
    {
        const string source = """
            public static class P
            {
                public static string Run() => $"a\"b\\c\n{7:D2}";
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "a\"b\\c\n07");
        // SyntaxFactory re-escaped the composite literal (no hand-rolled
        // escaping): quote, backslash and newline all appear as escapes.
        Assert.Contains(@"a\""b\\c\n{0:D2}", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void Format_MultipleHoles_SequentialIndices_RuntimeEqual()
    {
        const string source = """
            public static class P
            {
                public static string Run() => $"{1}-{2}-{3:00}";
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "1-2-03");
        Assert.Contains(
            "string.Format(\"{0}-{1}-{2:00}\", 1, 2, 3)",
            Normalize(output));
    }

    // ------------------------------------------------------------------
    // IE-FORMAT alignment — directions, zero, folded constant width; CS2J0514.
    // ------------------------------------------------------------------

    [Fact]
    public void Format_Alignment_PositiveNegativeZero_AndFoldedWidth_RuntimeEqual()
    {
        const string source = """
            public static class P
            {
                public static string Run()
                    => $"[{42,5}][{42,-5}][{123,1}][{1,0}][{1,2 + 3}]|";
            }
            """;

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "[   42][42   ][123][1][    1]|");
        Assert.Contains("{0,5}", output);
        Assert.Contains("{1,-5}", output);
        Assert.Contains("{2,1}", output);
        Assert.Contains("{3,0}", output);
        // The compile-time constant 2 + 3 folds to the width 5 (no runtime
        // expression is emitted into the composite).
        Assert.Contains("{4,5}", output);
        Assert.Equal(0, Preprocess(source, Enabled).Statistics.Interpolations.TempsLifted);
    }

    [Fact]
    public void Format_NonConstantAlignment_Strict0514_AtomicPreserve()
    {
        const string source = """
            public static class C
            {
                public static string M(int w) => $"{1,w}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = result.Diagnostics
            .Single(d => d.Code == InterpolationDiagnosticCodes.CS2J0514);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.Interpolations.SkippedByReason[
            InterpolationDiagnosticCodes.CS2J0514]);
        Assert.Equal(0, result.Statistics.Interpolations.FormatExpansions);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
    }

    [Fact]
    public void Format_NonConstantAlignment_NonStrict_Warns_SiblingSiteStillRewrites()
    {
        const string source = """
            public static class C
            {
                public static string M(int w)
                {
                    var bad = $"{1,w}";
                    var ok = $"n{1}";
                    return bad + ok;
                }
            }
            """;
        var options = new InterpolationOptions { Enabled = true, Strict = false };

        var result = Preprocess(source, options);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("string.Concat(\"n\", 1)", normalized);
        Assert.Contains("$\"{1,w}\"", normalized); // baseline-illegal site kept
        var diagnostic = result.Diagnostics
            .Single(d => d.Code == InterpolationDiagnosticCodes.CS2J0514);
        Assert.Equal(ExpressionDesugarSeverity.Warning, diagnostic.Severity);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT specifier passthrough + unknown specifiers (no TODO/printf).
    // ------------------------------------------------------------------

    [Fact]
    public void Format_AllSpecifierFamilies_PassThroughCompositeVerbatim_EnUsOracle()
    {
        const string source = """
            using System;
            using System.Globalization;
            public static class P
            {
                public static string Run()
                {
                    var old = CultureInfo.CurrentCulture;
                    try
                    {
                        CultureInfo.CurrentCulture = new CultureInfo("en-US");
                        int x = 42;
                        double d = 1234.567;
                        decimal m = 1234.5m;
                        var dt = new DateTime(2026, 3, 4, 13, 5, 7, DateTimeKind.Utc);
                        return $"{x:D4}|{d:F2}|{d:E2}|{d:e2}|{x:X8}|{m:N2}|{d:G4}|{0.36:P0}|{dt:MM/dd/yyyy}|{dt:R}|{dt:M}";
                    }
                    finally { CultureInfo.CurrentCulture = old; }
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P");
        foreach (var placeholder in new[]
                 {
                     "{0:D4}", "{1:F2}", "{2:E2}", "{3:e2}", "{4:X8}",
                     "{5:N2}", "{6:G4}", "{7:P0}", "{8:MM/dd/yyyy}",
                     "{9:R}", "{10:M}",
                 })
        {
            Assert.Contains(placeholder, output);
        }
        Assert.DoesNotContain("TODO", output);
        Assert.DoesNotContain("InvariantCulture", output);
    }

    [Fact]
    public void Format_UnknownSpecifier_Q2_ProbeAndNumeric_PassThrough_NoTodo()
    {
        // The probe distinguishes the parameterless ToString() (what Concat
        // would call) from the IFormattable interface path the default
        // handler and string.Format both take (the handler may pass a null
        // provider meaning "current culture"; the format text is what proves
        // passthrough). The numeric 'qq' custom specifier is made of literal
        // characters, so the BCL echoes it for both native and preprocessed.
        const string source = """
            using System;
            public sealed class ProviderProbe : IFormattable
            {
                public override string ToString() => "PLAIN";
                string IFormattable.ToString(string? format, IFormatProvider? provider)
                    => "VIA:" + (format ?? "<null>");
            }
            public static class P
            {
                public static string Run()
                    => $"{new ProviderProbe():Q2}|{1.5:qq}";
            }
            """;

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "VIA:Q2|qq");
        Assert.Contains("{0:Q2}", output);
        Assert.Contains("{1:qq}", output);
        Assert.DoesNotContain("TODO", output);
        Assert.DoesNotContain("%s", output);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT culture — de-DE fixed culture; never invariant.
    // ------------------------------------------------------------------

    [Fact]
    public void Format_Culture_DeDe_NativeEqualsPreprocessed_NoInvariant()
    {
        const string source = """
            using System;
            using System.Globalization;
            public static class Cul
            {
                public static string Run()
                {
                    var old = CultureInfo.CurrentCulture;
                    try
                    {
                        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                        var dt = new DateTime(2026, 3, 4, 13, 5, 7);
                        return $"{1.5}|{dt}|{1.5:F2}|{1234.567:N2}|{0.36:P0}|{42,6}|{true}";
                    }
                    finally { CultureInfo.CurrentCulture = old; }
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "Cul");
        // The culture really switched (decimal comma, German date/time,
        // thousands dot, percent spacing) AND the product names no invariant.
        var value = RunStaticMethod(output, "Cul");
        Assert.StartsWith("1,5|04.03.2026 13:05:07|1,50|1.234,57|36 %|    42|True", value);
        Assert.DoesNotContain("InvariantCulture", output);
        Assert.DoesNotContain("FormattableString.Invariant", output);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT nesting — inner expands first; no Format flattening.
    // ------------------------------------------------------------------

    [Fact]
    public void Format_Nested_InnerFormat_OuterConcat_RuntimeEqual()
    {
        const string source = """
            using System;
            using System.Globalization;
            public static class P
            {
                public static string Run()
                {
                    var old = CultureInfo.CurrentCulture;
                    try
                    {
                        CultureInfo.CurrentCulture = new CultureInfo("en-US");
                        return $"a{$"{1.5:f2}"}b";
                    }
                    finally { CultureInfo.CurrentCulture = old; }
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "a1.50b");
        Assert.Contains(
            "string.Concat(\"a\", string.Format(\"{0:f2}\", 1.5), \"b\")",
            Normalize(output));
        var stats = Preprocess(source, Enabled).Statistics.Interpolations;
        Assert.Equal(1, stats.FormatExpansions);
        Assert.Equal(1, stats.ConcatExpansions);
        Assert.Equal(1, stats.NestedInterpolationSites);
    }

    [Fact]
    public void Format_Nested_ThreeLevels_AndConcatInsideFormat_RuntimeEqual()
    {
        const string source = """
            public static class P
            {
                public static string Run()
                    => $"{ $"n{1}" ,6}|" + $"[{$"({$"{1:000}"})"}]";
            }
            """;

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "    n1|[(001)]");
        // No Format flattening: the outer Format keeps an inner Concat call,
        // and the three-level chain stays nested.
        Assert.Contains("string.Format(\"{0,6}|\", string.Concat(\"n\", 1))",
            Normalize(output));
        Assert.Contains("{0:000}", output);
        var stats = Preprocess(source, Enabled).Statistics.Interpolations;
        Assert.Equal(5, stats.InterpolationSites);
        Assert.Equal(2, stats.FormatExpansions);
        Assert.Equal(3, stats.ConcatExpansions);
    }

    [Fact]
    public void Format_Nested_SideEffects_InnerOuter_EachHoleOnce_InOrder()
    {
        const string source = """
            public static class Probe
            {
                public static int Calls;
                public static int Next() { Calls++; return Calls; }
                public static string Run()
                {
                    Calls = 0;
                    var s = $"{Next():D2}-{$"{Next()}-{Next()}"}";
                    return s + "|" + Calls;
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(
            source, "Probe", expected: "01-2-3|3");
        // Method declaration + three hole call sites = three arguments, each
        // evaluated exactly once (a temp/duplication would show 4+).
        Assert.Equal(4, Regex.Matches(Normalize(output), "Next\\(\\)").Count);
        Assert.Equal(0, Preprocess(source, Enabled).Statistics.Interpolations.TempsLifted);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT side effects — assignment/postfix/ternary/conditional access.
    // ------------------------------------------------------------------

    [Fact]
    public void Format_SideEffects_ArgsOnce_ZeroTemps_AssignmentPostfixTernaryConditional()
    {
        const string source = """
            public static class Probe
            {
                public static int Calls;
                public static int Next() { Calls++; return 7; }
                public static string Run()
                {
                    Calls = 0;
                    int x = 0;
                    string? s = null;
                    bool b = true;
                    var r = $"{Next():D2}|{(x = 9):D2}|{x++:D2}|{(b ? 1 : 2):D2}|{(s?.Length),5}|{Next(),3}";
                    return r + "|" + Calls + "|" + x;
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(
            source, "Probe", expected: "07|09|09|01|     |  7|2|10");
        var normalized = Normalize(output);
        Assert.Contains(
            "string.Format(\"{0:D2}|{1:D2}|{2:D2}|{3:D2}|{4,5}|{5,3}\", " +
            "Next(), (x = 9), x++, (b ? 1 : 2), (s?.Length), Next())",
            normalized);
        // Method declaration + two Next() holes only.
        Assert.Equal(3, Regex.Matches(normalized, "Next\\(\\)").Count);
        Assert.Equal(0, Preprocess(source, Enabled).Statistics.Interpolations.TempsLifted);
        Assert.DoesNotContain("__ed_", output);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT null semantics — BCL supplies "", no ?? / ToString wraps.
    // ------------------------------------------------------------------

    [Fact]
    public void Format_NullHoles_StringObjectNullableInt_RuntimeEqual_NoCoalescing()
    {
        const string source = """
            public static class P
            {
                public static string Run()
                {
                    int? ni = null;
                    object? o = null;
                    return $"{1:D2}|{(string)null}|{o}|{ni}|{ni,3}";
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "01||||   ");
        var normalized = Normalize(output);
        Assert.DoesNotContain("??", normalized);
        Assert.DoesNotContain("ToString", normalized);
        Assert.Contains(
            "string.Format(\"{0:D2}|{1}|{2}|{3}|{4,3}\", 1, (string)null, o, ni, ni)",
            normalized);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT unsigned holes stay raw (no ToString wrapping).
    // ------------------------------------------------------------------

    [Fact]
    public void Format_UnsignedHoles_WithFormat_StayRaw_RuntimeEqual()
    {
        const string source = """
            public static class P
            {
                public static string Run()
                {
                    byte b = 255;
                    ushort us = 65535;
                    uint u = 4294967295u;
                    ulong ul = 18446744073709551615ul;
                    return $"{b:X2}-{us:X4}-{u:X8}-{ul:X16}";
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "FF-FFFF-FFFFFFFF-FFFFFFFFFFFFFFFF");
        var normalized = Normalize(output);
        Assert.Contains(
            "string.Format(\"{0:X2}-{1:X4}-{2:X8}-{3:X16}\", b, us, u, ul)",
            normalized);
        Assert.DoesNotContain("ToString", normalized);
        Assert.Equal(0, Preprocess(source, Enabled).Statistics.Interpolations.UnsignedHoleWraps);
    }

    // ------------------------------------------------------------------
    // IE-FORMAT idempotency + Enabled=false zero-regression.
    // ------------------------------------------------------------------

    [Fact]
    public void Format_Output_Recompiles_AndSecondRunIsZeroDiff()
    {
        const string source = """
            public static class C { public static string M(int x) => $"{x:D2}|{x,5:X}"; }
            """;

        var result = Preprocess(source, Enabled);
        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);

        var second = Preprocess(result.OutputCode, Enabled);
        Assert.False(second.Changed);
        Assert.Equal(result.OutputCode, second.OutputCode);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void Format_RefStructSafeToStringHole_OnFormatPath_WrapsToString()
    {
        // Format site (the int hole carries a format clause) that also has a
        // proven-safe ref-struct hole: the span cannot be boxed, so it is the
        // single wrapping the Format emitter permits (x.ToString()).
        const string source = """
            using System;
            public static class C
            {
                public static string M(ReadOnlySpan<char> sp) => $"{sp}|{1:D2}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains(
            "string.Format(\"{0}|{1:D2}\", sp.ToString(), 1)",
            Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.FormatExpansions);
        Assert.Equal(1, result.Statistics.Interpolations.RefStructToStringWraps);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Disabled_FormatInterpolation_IsInert_BytesIdentical_SectionZero()
    {
        const string source = """
            public static class C { public static string M(int x) => $"{x:D2}"; }
            """;
        var (_, model) = BuildCompilation(source);

        var result = new ExpressionDesugarPreprocessor()
            .Preprocess(source, model, new ExpressionDesugarOptions());

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.Interpolations.InterpolationSites);
        Assert.Equal(0, result.Statistics.Interpolations.FormatExpansions);
    }

    [Fact]
    public void Concat_BoolEnumDouble_WithoutClauses_StaysConcat_NotFormat()
    {
        const string source = """
            public enum Color { Red, Green }
            public static class C
            {
                public static string M(bool b, Color e, double d) => $"{b}-{e}-{d}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains(
            "string.Concat(b, \"-\", e, \"-\", d)",
            Normalize(result.OutputCode));
        var stats = result.Statistics.Interpolations;
        Assert.Equal(1, stats.ConcatExpansions);
        Assert.Equal(0, stats.FormatExpansions);
        AssertOutputCompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // CS2J0510 — custom [InterpolatedStringHandler] hard gate (Error/Keep).
    // ------------------------------------------------------------------

    private const string HandlerFixture = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Text;

        public static class Logger
        {
            // No InterpolatedStringHandlerArgument on a static method: "" would
            // denote a (nonexistent) 'this' receiver (CS8944); the handler
            // pattern is recognized from the parameter's [InterpolatedStringHandler]
            // type and the (int,int,out bool) ctor is picked automatically.
            public static void Log(ref MyHandler handler)
                => Last = handler.ToString();
            public static string? Last;
        }

        [InterpolatedStringHandler]
        public ref struct MyHandler
        {
            private readonly StringBuilder _b;
            public MyHandler(int literalLength, int formattedCount, out bool append)
            {
                _b = new StringBuilder(literalLength);
                append = true;
            }
            public void AppendLiteral(string s) => _b.Append(s);
            public void AppendFormatted<T>(T value) => _b.Append(value);
            public override string ToString() => _b.ToString();
        }

        public static class C
        {
            public static void M() { Logger.Log($"x{42}"); }
        }
        """;

    [Fact]
    public void Gate_CustomHandler_Strict0510_AtomicPreserve_AndNamesThreeDifferences()
    {
        var result = Preprocess(HandlerFixture, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(HandlerFixture, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(InterpolationDiagnosticCodes.CS2J0510, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Contains("MyHandler", diagnostic.Message);
        // The three observable differences (Q1): append semantics, evaluation
        // count, conditional construction.
        Assert.Contains("append semantics", diagnostic.Message);
        Assert.Contains("evaluation count", diagnostic.Message);
        Assert.Contains("conditional construction", diagnostic.Message);
        Assert.Equal(1, result.Statistics.Interpolations.CustomHandlerSites);
    }

    [Fact]
    public void Gate_CustomHandler_KeepPolicy_WarnsAndPreserves()
    {
        var options = new InterpolationOptions
        {
            Enabled = true,
            HandlerPolicy = HandlerUnsupportedPolicy.Keep,
        };

        var result = Preprocess(HandlerFixture, options);

        Assert.False(result.Changed);
        Assert.Equal(HandlerFixture, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(InterpolationDiagnosticCodes.CS2J0510, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.Interpolations.CustomHandlerSites);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
    }

    [Fact]
    public void Gate_CustomHandler_NonStrict_WarnsButSiblingConcatSiteRewrites()
    {
        const string source = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Text;

            public static class Logger
            {
                public static void Log(ref MyHandler handler) { }
            }

            [InterpolatedStringHandler]
            public ref struct MyHandler
            {
                public MyHandler(int literalLength, int formattedCount, out bool append)
                {
                    append = true;
                }
                public void AppendLiteral(string s) { }
                public void AppendFormatted<T>(T value) { }
            }

            public static class C
            {
                public static void M()
                {
                    Logger.Log($"h{42}");
                    var s = $"n{1}";
                }
            }
            """;
        var options = new InterpolationOptions
        {
            Enabled = true,
            Strict = false,
        };

        var result = Preprocess(source, options);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("string.Concat(\"n\", 1)", normalized);
        Assert.Contains("$\"h{42}\"", normalized);
        var handlerDiagnostic = result.Diagnostics
            .Single(d => d.Code == InterpolationDiagnosticCodes.CS2J0510);
        Assert.Equal(ExpressionDesugarSeverity.Warning, handlerDiagnostic.Severity);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
        AssertOutputCompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // Boxed string sites (natural string -> System.Object): the ordinary
    // interpolation->string conversion plus implicit boxing; no handler can
    // bind through an object slot. M4 sdk profile found all 28 CS2J0512
    // sites in exactly these shapes (object[] elements / params object[]).
    // ------------------------------------------------------------------

    [Fact]
    public void BoxedString_ObjectArrayElement_ExpandsViaConcat()
    {
        const string source = """
            public static class C
            {
                public static object[] M(int x) => new object[] { $"a{x}", "b" };
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains("string.Concat(\"a\", x)", Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        Assert.Equal(0, result.Statistics.Interpolations.SkippedByReason
            .GetValueOrDefault(InterpolationDiagnosticCodes.CS2J0512));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == InterpolationDiagnosticCodes.CS2J0512);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void BoxedString_ParamsObjectArgument_ExpandsViaConcat()
    {
        // The interpolation lands in a params object[] slot (boxing). It has no
        // alignment/format clause, so the boxed-string binding expands it via
        // string.Concat (a format clause would instead route to string.Format).
        const string source = """
            public static class C
            {
                public static string Format(string template, params object[] args) => template;
                public static string M(int code) => Format("err: {0}", $"0x{code}");
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains("string.Concat(", Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == InterpolationDiagnosticCodes.CS2J0512);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void BoxedString_ObjectLocal_ExpandsViaConcat()
    {
        const string source = """
            public static class C
            {
                public static object M(int x)
                {
                    object o = $"v{x}";
                    return o;
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains("string.Concat(\"v\", x)", Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void DynamicHole_ExpandsViaConcat()
    {
        // A dynamic hole does not make the site unbindable: the interpolation's
        // converted type is still System.String, so it expands to string.Concat
        // (the hole keeps dynamic dispatch, mirroring the C# lowering). No
        // CS2J0512 residual, output compiles.
        const string source = """
            public static class C
            {
                public static string M(dynamic d) => $"a{d}";
            }
            """;
        var options = new InterpolationOptions { Enabled = true, Strict = false };

        var result = Preprocess(source, options);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains("string.Concat(\"a\", d)", Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == InterpolationDiagnosticCodes.CS2J0512);
        AssertOutputCompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // CS2J0511 / CS2J0515 — explicit runtime shapes.
    // ------------------------------------------------------------------

    [Fact]
    public void Gate_FormattableString_VariableAndInvariant_BothPreserved_0511()
    {
        const string source = """
            using System;
            public static class C
            {
                public static string M()
                {
                    FormattableString fs = $"x={1}";
                    return FormattableString.Invariant($"y={2}");
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostics = result.Diagnostics
            .Where(d => d.Code == InterpolationDiagnosticCodes.CS2J0511)
            .ToList();
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal(
            ExpressionDesugarSeverity.Warning, d.Severity));
        Assert.Equal(
            2, result.Statistics.Interpolations.KeptFormattableStringSites);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
    }

    [Fact]
    public void Gate_InterpolationInsideExpressionLambda_Preserved_0515()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            public static class C
            {
                public static Expression<Func<string>> M() => () => $"n={1}";
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(InterpolationDiagnosticCodes.CS2J0515, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Info, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.Interpolations.ExpressionTreeSitesSkipped);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
    }

    // ------------------------------------------------------------------
    // Opt-in gate + untouched ordinary literals + C36 coexistence.
    // ------------------------------------------------------------------

    [Fact]
    public void Disabled_RuleIsInert_BytesIdentical_SectionZero()
    {
        const string source = """
            public static class C { public static string M(int x) => $"n{x}"; }
            """;
        var (_, model) = BuildCompilation(source);

        var result = new ExpressionDesugarPreprocessor()
            .Preprocess(source, model, new ExpressionDesugarOptions());

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.Interpolations.FilesScanned);
        Assert.Equal(0, result.Statistics.Interpolations.InterpolationSites);
        Assert.Equal(0, result.Statistics.Interpolations.ConcatExpansions);
    }

    [Fact]
    public void Disabled_C36StillRewrites_AndInterpolationStaysUntouched()
    {
        const string source = """
            public class C
            {
                void M()
                {
                    int[] a = [1, 2];
                    string s = $"n{1}";
                }
            }
            """;
        var (_, model) = BuildCompilation(source);

        var result = new ExpressionDesugarPreprocessor()
            .Preprocess(source, model, new ExpressionDesugarOptions());

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("new int[] { 1, 2 }", normalized);
        Assert.Contains("$\"n{1}\"", normalized);
        Assert.Equal(0, result.Statistics.Interpolations.InterpolationSites);
        Assert.Equal(1, result.Statistics.CollectionExpr.CollectionSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Enabled_OrdinaryLiteralsWithNameofBracesPercent_AreCleanSkipped()
    {
        const string source = """
            public static class C
            {
                public static string M()
                {
                    var a = "nameof(C)";
                    var b = "{";
                    var c = "100%";
                    return a + b + c;
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.Interpolations.InterpolationSites);
    }

    // ==================================================================
    // Part C — verbatim ($@ / @$) matrix with the reflection runtime oracle.
    // Products are REGULAR literals: no '@' delimiter survives; embedded ""
    // escapes and physical line breaks become ordinary factory escapes.
    // ==================================================================

    [Fact]
    public void Verbatim_EmbeddedDoubledQuotes_Concat_RegularLiteral_RuntimeEqual()
    {
        // $@"x=""y""-{1}" — the doubled quotes decode to one quote each.
        var source = "public static class P { public static string Run() " +
                     "=> $@\"x=\"\"y\"\"-{1}\"; }";

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "x=\"y\"-1");
        Assert.DoesNotContain("@\"", output);
        // The quote now lives in a regular literal escape.
        Assert.Contains("\\\"y\\\"", output);
    }

    [Fact]
    public void Verbatim_AtDollarPrefixOrder_PureText_FoldsToRegularLiteral()
    {
        // @$"" prefix order is the lexical twin of $@""; a text-only site folds.
        var source = "public static class P { public static string Run() " +
                     "=> @$\"hello\"; }";

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("=> \"hello\"", Normalize(result.OutputCode));
        Assert.DoesNotContain("@$\"", result.OutputCode);
        Assert.Equal(1, result.Statistics.Interpolations.ConstantsFolded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Verbatim_Multiline_NewlineAndQuotes_BecomeRegularEscapes_RuntimeEqual()
    {
        // A physical line break and an embedded "" inside a verbatim
        // interpolation; the product must carry \n and \" in a regular
        // literal and keep runtime equality.
        var source =
            "public static class P\n" +
            "{\n" +
            "    public static string Run()\n" +
            "    {\n" +
            "        var x = 7;\n" +
            "        return $@\"line1-{x}\n" +
            "line2 \"\"q\"\"\";\n" +
            "    }\n" +
            "}\n";

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "line1-7\nline2 \"q\"");
        Assert.DoesNotContain("@\"", output);
        Assert.Contains(@"\n", output);
        Assert.Contains("\\\"q\\\"", output);
    }

    [Fact]
    public void Verbatim_DoubledBraces_AreUnescaped_RuntimeEqual()
    {
        // Verbatim keeps the regular brace-doubling grammar (raw does not).
        var source = "public static class P { public static string Run() " +
                     "=> $@\"{{a}}-{42}\"; }";

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "{a}-42");
        Assert.DoesNotContain("@\"", output);
    }

    // ==================================================================
    // Part C — raw interpolations: single/multi-line, multi-dollar holes,
    // indentation stripping, quotes/newlines/literal braces. Runtime oracle
    // throughout; products carry no raw lexicon (""" delimiters).
    // ==================================================================

    [Fact]
    public void Raw_SingleLine_QuotesAndHole_ProductHasNoRawLexicon_RuntimeEqual()
    {
        // $"""a"{1}b""" — quotes need no escaping inside the raw literal.
        var source = "public static class P { public static string Run() " +
                     "=> $\"\"\"a\"{1}b\"\"\"; }";

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "a\"1b");
        Assert.DoesNotContain("\"\"\"", output);
        Assert.DoesNotContain("$\"", output);
        Assert.Contains("string.Concat", Normalize(output));
    }

    [Fact]
    public void Raw_PureText_FoldsToBareRegularLiteral()
    {
        var source = "public static class P { public static string Run() " +
                     "=> $\"\"\"hello\"\"\"; }";

        var result = Preprocess(source, Enabled);

        Assert.True(result.Changed);
        Assert.Contains("=> \"hello\"", Normalize(result.OutputCode));
        Assert.DoesNotContain("\"\"\"", result.OutputCode);
        Assert.Equal(1, result.Statistics.Interpolations.ConstantsFolded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Raw_MultiLine_IndentationStripped_NewlinesKept_RuntimeEqual()
    {
        // Closing-delimiter column decides the common leading whitespace,
        // which is stripped from every content line. The newline immediately
        // after the opening delimiter and the newline immediately before the
        // closing line are BOTH removed; interior newlines survive.
        var source =
            "public static class P\n" +
            "{\n" +
            "    public static string Run()\n" +
            "    {\n" +
            "        var x = 7;\n" +
            "        return $\"\"\"\n" +
            "            line1 {x}\n" +
            "            line2\n" +
            "            \"\"\";\n" +
            "    }\n" +
            "}\n";

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "line1 7\nline2");
        Assert.DoesNotContain("\"\"\"", output);
        Assert.Contains(@"\n", output);
    }

    [Fact]
    public void Raw_DoubleDollar_HolesNeedDoubleBraces_SingleBracesStayLiteral_RuntimeEqual()
    {
        // $$: {{x}} is the hole; lone { and } are literal text verbatim.
        var source =
            "public static class P\n" +
            "{\n" +
            "    public static string Run()\n" +
            "    {\n" +
            "        var x = 7;\n" +
            "        return $$\"\"\"\n" +
            "            {a={{x}}} {\n" +
            "            \"\"\";\n" +
            "    }\n" +
            "}\n";

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "{a=7} {");
        Assert.DoesNotContain("\"\"\"", output);
        // Literal braces reach the Concat text segments un-doubled.
        Assert.Contains("{a=", output);
    }

    [Fact]
    public void Raw_TripleDollar_HoleLayersThreeBraces_RuntimeEqual()
    {
        // $$$: holes need {{{x}}}; runs below three braces stay literal.
        var source = "public static class P { public static string Run() " +
                     "=> $$$\"\"\"v={{{7}}}\"\"\"; }";

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "v=7");
        Assert.DoesNotContain("\"\"\"", output);
        Assert.Contains("string.Concat", Normalize(output));
    }

    [Fact]
    public void Raw_DoubleDollar_FormatClause_LiteralBracesDoubledInComposite_RuntimeEqual()
    {
        // {{7:X2}} routes to Format; literal { } text must be doubled in the
        // composite ({ -> {{) while the raw braces themselves stayed verbatim.
        var source =
            "public static class P\n" +
            "{\n" +
            "    public static string Run()\n" +
            "    {\n" +
            "        return $$\"\"\"\n" +
            "            { {{7:X2}} }\n" +
            "            \"\"\";\n" +
            "    }\n" +
            "}\n";

        var output = AssertNativeEqualsPreprocessed(
            source, "P", expected: "{ 07 }");
        Assert.Contains("{{ {0:X2} }}", output);
        Assert.Equal(1, Preprocess(source, Enabled).Statistics.Interpolations.FormatExpansions);
    }

    // ==================================================================
    // Part C — C43 boundary: non-interpolated raw/u8/verbatim literals are
    // owned by the lexical rules; IsPotentiallyDirty keys off
    // InterpolatedStringExpressionSyntax alone, so the C23 rule must never
    // touch them.
    // ==================================================================

    [Fact]
    public void Boundary_NonInterpolatedRawLiteral_IsNotTouched()
    {
        // Raw literal WITHOUT the leading $: braces are ordinary characters.
        var source =
            "public static class C\n" +
            "{\n" +
            "    public static void M()\n" +
            "    {\n" +
            "        var s = \"\"\"{not a hole}\"\"\";\n" +
            "        System.Console.Write(s);\n" +
            "    }\n" +
            "}\n";

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.Interpolations.InterpolationSites);
    }

    [Fact]
    public void Boundary_Utf8Literal_IsNotTouched()
    {
        var source =
            "public static class C\n" +
            "{\n" +
            "    public static void M()\n" +
            "    {\n" +
            "        var b = \"abc\"u8;\n" +
            "        System.Console.Write(b.Length);\n" +
            "    }\n" +
            "}\n";

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.Interpolations.InterpolationSites);
    }

    [Fact]
    public void Boundary_PlainVerbatimLiteral_IsNotTouched()
    {
        var source =
            "public static class C\n" +
            "{\n" +
            "    public static void M()\n" +
            "    {\n" +
            "        var v = @\"C:\\x{{\";\n" +
            "        System.Console.Write(v);\n" +
            "    }\n" +
            "}\n";

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.Interpolations.InterpolationSites);
    }

    // ==================================================================
    // Part C — expression trees vs ordinary LINQ. M4/Q4: canonical
    // expression-tree lowering is deferred this milestone, so
    // RewriteInExpressionTrees=true still preserves (never emits wrong code).
    // ==================================================================

    [Fact]
    public void ExprTree_ObjectReturn_Interpolated_Preserved_0515_No0516_AndRuns()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            public static class C
            {
                public static string Run()
                {
                    Expression<Func<object>> e = () => $"n={1}";
                    return (string)e.Compile()()!;
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(InterpolationDiagnosticCodes.CS2J0515, diagnostic.Code);
        Assert.DoesNotContain(
            InterpolationDiagnosticCodes.CS2J0516,
            result.Diagnostics.Select(d => d.Code));
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("n=1", RunStaticMethod(source, "C"));
        Assert.Equal("n=1", RunStaticMethod(result.OutputCode, "C"));
    }

    [Fact]
    public void ExprTree_RewriteOptionTrue_StillPreserved_M4_Q4_Deferred()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            public static class C
            {
                public static Expression<Func<string>> M() => () => $"n={1}";
            }
            """;
        var options = new InterpolationOptions
        {
            Enabled = true,
            RewriteInExpressionTrees = true,
        };

        var result = Preprocess(source, options);

        // M4/Q4: the option is accepted but compiler-canonical lowering is
        // not implemented, so the site is preserved exactly as with the
        // default false — no semantically wrong emission can escape.
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(InterpolationDiagnosticCodes.CS2J0515, diagnostic.Code);
        Assert.Equal(1, result.Statistics.Interpolations.ExpressionTreeSitesSkipped);
        Assert.Equal(0, result.Statistics.Interpolations.ConcatExpansions);
    }

    [Fact]
    public void Linq_MethodChain_DelegateLambda_InterpolationExpands_RuntimeEqual()
    {
        const string source = """
            using System.Linq;
            public static class P
            {
                public static string Run()
                    => string.Join(",", new[] { 1, 2 }.Select(x => $"n={x}"));
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "n=1,n=2");
        Assert.Contains("string.Concat(\"n=\", x)", Normalize(output));
        Assert.Equal(0, Preprocess(source, Enabled).Statistics.Interpolations.ExpressionTreeSitesSkipped);
    }

    [Fact]
    public void Linq_QueryComprehension_DelegateLambda_InterpolationExpands_RuntimeEqual()
    {
        const string source = """
            using System.Linq;
            public static class P
            {
                public static string Run()
                {
                    var q = from x in new[] { 1, 2 }
                            select $"[{x}]";
                    return string.Join(",", q);
                }
            }
            """;

        var output = AssertNativeEqualsPreprocessed(source, "P", expected: "[1],[2]");
        Assert.Contains("string.Concat(\"[\", x, \"]\")", Normalize(output));
    }

    // ==================================================================
    // Part C — FormattableString at a parameter position; second run stable.
    // ==================================================================

    [Fact]
    public void Gate_FormattableString_ParameterPosition_Preserved_SecondRunStable()
    {
        const string source = """
            using System;
            public static class C
            {
                public static string Take(FormattableString fs) => fs.Format;
                public static string Run() => Take($"y={2}");
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(
            InterpolationDiagnosticCodes.CS2J0511,
            Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, result.Statistics.Interpolations.KeptFormattableStringSites);

        var second = Preprocess(result.OutputCode, Enabled);
        Assert.False(second.Changed);
        Assert.Equal(source, second.OutputCode);
        Assert.Equal(
            InterpolationDiagnosticCodes.CS2J0511,
            Assert.Single(second.Diagnostics).Code);
        Assert.Equal("y={0}", RunStaticMethod(source, "C"));
    }

    // ==================================================================
    // Part C — nested nodes carried by preserved outers pair in BOTH gates.
    // ==================================================================

    private const string NestedHandlerFixture = """
        using System;
        using System.Runtime.CompilerServices;

        public static class Logger
        {
            // A by-value handler parameter (the standard handler argument
            // shape); no [InterpolatedStringHandlerArgument] is needed because
            // the handler ctor takes only (int, int, out bool). A 'ref'
            // parameter would reject the interpolation outright (CS1620).
            public static void Log(MyHandler handler) { }
        }

        [InterpolatedStringHandler]
        public ref struct MyHandler
        {
            public MyHandler(int literalLength, int formattedCount, out bool append)
            {
                append = true;
            }
            public void AppendLiteral(string s) { }
            public void AppendFormatted<T>(T value) { }
        }

        public static class C
        {
            public static void M() { Logger.Log($"h{$"x{1}"}"); }
        }
        """;

    [Fact]
    public void Keep_HandlerOuter_CarriesNestedNode_BothPairViaAccounting()
    {
        var options = new InterpolationOptions
        {
            Enabled = true,
            HandlerPolicy = HandlerUnsupportedPolicy.Keep,
        };

        var result = Preprocess(NestedHandlerFixture, options);

        Assert.False(result.Changed);
        Assert.Equal(NestedHandlerFixture, result.OutputCode);
        var stats = result.Statistics.Interpolations;
        // Only the outer disposition site is counted: the inner site inherits
        // the preserved disposition silently (no duplicate diagnostics), but
        // it is carried as NestedPreservedSites for tree-gate pairing.
        Assert.Equal(1, stats.InterpolationSites);
        Assert.Equal(1, stats.CustomHandlerSites);
        Assert.Equal(1, stats.NestedPreservedSites);
        Assert.Equal(0, stats.ResidualInterpolationCount);
        // Outer (0510 keep) + one nested node carried verbatim.
        Assert.Equal(2, stats.CountPairedNodes());
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FormattableString_Outer_CarriesNestedNode_BothPairViaAccounting()
    {
        const string source = """
            using System;
            public static class C
            {
                public static string M()
                {
                    FormattableString fs = $"a{$"b{1}"}";
                    return fs.Format;
                }
            }
            """;

        var result = Preprocess(source, Enabled);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var stats = result.Statistics.Interpolations;
        // Outer is the only disposition site; the inner interpolation is
        // carried verbatim by the preserved FormattableString subtree and
        // counted solely as a nested preserved site.
        Assert.Equal(1, stats.InterpolationSites);
        Assert.Equal(1, stats.KeptFormattableStringSites);
        Assert.Equal(1, stats.NestedPreservedSites);
        Assert.Equal(0, stats.ResidualInterpolationCount);
        Assert.Equal(2, stats.CountPairedNodes());
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void CountPairedNodes_SumsPreserveCodes_AndNestedCarry()
    {
        var stats = new InterpolationStatistics();
        stats.AddSkip(InterpolationDiagnosticCodes.CS2J0511);
        stats.AddSkip(InterpolationDiagnosticCodes.CS2J0515);
        stats.AddSkip(InterpolationDiagnosticCodes.CS2J0510);
        stats.NestedPreservedSites = 2;

        Assert.Equal(5, stats.CountPairedNodes());

        // Expansion codes never pair a residual node.
        var expanded = new InterpolationStatistics();
        expanded.AddSkip(InterpolationDiagnosticCodes.CS2J0501);
        expanded.AddSkip(InterpolationDiagnosticCodes.CS2J0502);
        expanded.AddSkip(InterpolationDiagnosticCodes.CS2J0503);
        Assert.Equal(0, expanded.CountPairedNodes());
    }
}
