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
/// C23 interpolation-expansion Part A unit tests (design
/// docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md §4):
/// IE-FOLD (CS2J0503), IE-CONCAT (CS2J0501), handler five-way hard gate G0
/// and the Part A Format-preservation bridge (CS2J0502). All API shapes were
/// locked against Roslyn 5.0.0 by earlier probes; the load-bearing deviations
/// from the paper design are asserted here:
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
    // CS2J0502 — Part A Format/alignment preservation bridge.
    // ------------------------------------------------------------------

    [Fact]
    public void Bridge_FormatAlignmentAndNonWhitelistIFormattable_Preserved_0502_NoResidual()
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

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(InterpolationDiagnosticCodes.CS2J0502, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Info, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.Interpolations.FormatDeferredSites);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
    }

    [Fact]
    public void Bridge_SiblingConcatSiteStillRewrites_AndSecondRunIsStable()
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
        Assert.Contains("$\"{dt:u}\"", normalized); // bridge site kept verbatim
        var codes = result.Diagnostics.Select(d => d.Code).ToHashSet();
        Assert.Contains(InterpolationDiagnosticCodes.CS2J0501, codes);
        Assert.Contains(InterpolationDiagnosticCodes.CS2J0502, codes);
        Assert.Equal(1, result.Statistics.Interpolations.ConcatExpansions);
        Assert.Equal(1, result.Statistics.Interpolations.FormatDeferredSites);
        Assert.Equal(0, result.Statistics.Interpolations.ResidualInterpolationCount);
        AssertOutputCompiles(result.OutputCode);

        // Second run: only the preserved site remains -> dirty-no-rewrite,
        // byte-identical output, no residual 0516.
        var second = Preprocess(result.OutputCode, Enabled);
        Assert.False(second.Changed);
        Assert.Equal(result.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.Interpolations.ResidualInterpolationCount);
        Assert.Contains(
            InterpolationDiagnosticCodes.CS2J0502,
            second.Diagnostics.Select(d => d.Code));
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
}
