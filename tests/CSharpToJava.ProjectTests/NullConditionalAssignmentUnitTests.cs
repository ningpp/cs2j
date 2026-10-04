using System.Reflection;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C51 null-conditional assignment core gate (design §7.1, NA-1/NA-2/NA-5
/// first slice). Every fixture is an in-memory source: compile natively with
/// nullable enabled, preprocess through the Core
/// <see cref="ExpressionDesugarPreprocessor"/> with the
/// <see cref="NullConditionalOptions"/> subsection, then assert diagnostics,
/// lowered shape, conditional evaluation counters, recompilability,
/// idempotency and equal observable behavior on native/lowered assemblies.
/// </summary>
public class NullConditionalAssignmentUnitTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview);

    private static readonly IReadOnlyList<MetadataReference> References = LoadReferences();

    private static readonly ExpressionDesugarOptions C51Enabled = new()
    {
        NullConditional = new NullConditionalOptions(),
    };

    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return tpa.Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    }

    private const string SimpleSitesSource = """
        namespace C51Fixture;

        public sealed class Box
        {
            public int B;
            public int P { get; set; }
            public int this[int i] { get => B + i; set => B = value; }

            public static int NextCalls;
            public static int Next() { NextCalls++; return NextCalls; }

            public static Box? Make(int seed) => seed < 0 ? null : new Box();
        }

        public static class Sites
        {
            public static void AssignField(Box? a, int c) { a?.B = c; }
            public static void AssignProp(Box? a, int c) { a?.P = c; }
            public static void AssignIndex(Box? a, int i, int v) { a?[i] = v; }
            public static int? ValueReturn(Box? a, int c) { return a?.B = c; }
            public static int? ValueLocal(Box? a, int c) { var x = a?.B = c; return x; }
            public static void ConditionalRhs(Box? a) { a?.B = Box.Next(); }
            public static void IndexArgs(Box? a) { a?[Box.Next()] = Box.Next(); }
            public static void FactoryReceiver(int seed, int c) { Box.Make(seed)?.B = c; }
        }
        """;

    private static CSharpCompilation BuildCompilation(string source, string name = "c51-fixture")
        => CSharpCompilation.Create(
            name,
            new[] { CSharpSyntaxTree.ParseText(source, ParseOptions, path: name + ".cs") },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

    private static Assembly EmitAndLoad(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(
            emit.Success,
            "compilation failed to emit: " +
            string.Join("; ", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())));
        return Assembly.Load(stream.ToArray());
    }

    private static (ExpressionDesugarResult Result, CSharpCompilation Native) PreprocessSoleFile(string source)
    {
        var native = BuildCompilation(source);
        Assert.Empty(native.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        var tree = native.SyntaxTrees[0];
        var model = native.GetSemanticModel(tree);
        var result = new ExpressionDesugarPreprocessor().Preprocess(source, model, C51Enabled);
        return (result, native);
    }

    private static object? BoxType(Assembly assembly)
        => assembly.GetType("C51Fixture.Box", throwOnError: true);

    private static object? NewBox(Assembly assembly)
        => Activator.CreateInstance((Type)BoxType(assembly)!);

    private static object? InvokeSites(Assembly assembly, string method, params object?[] args)
        => assembly.GetType("C51Fixture.Sites", throwOnError: true)!
            .GetMethod(method)!
            .Invoke(null, args);

    // ------------------------------------------------------------------
    // NA-1: member simple assignment, statement form.
    // ------------------------------------------------------------------

    [Fact]
    public void NA1_WhitelistReceiver_GuardedIf_ZeroTemp_BehavesIdentically()
    {
        const string source = """
            namespace C51Fixture;

            public sealed class Box { public int B; }

            public static class Sites { public static void AssignField(Box? a, int c) { a?.B = c; } }
            """;

        var (result, native) = PreprocessSoleFile(source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);
        Assert.Contains("if (a != null)", result.OutputCode);
        Assert.Contains("a.B = c", result.OutputCode);
        Assert.DoesNotContain("__ed_", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode);
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        var stats = result.Statistics.NullConditional;
        Assert.Equal(1, stats.AssignmentMemberSites);
        Assert.Equal(0, stats.AssignmentReceiverTempsLifted);
        Assert.Contains(result.Diagnostics, d => d.Code == NullConditionalDiagnosticCodes.CS2J0555);

        foreach (var assembly in new[]
                 {
                     EmitAndLoad(native),
                     EmitAndLoad(loweredCompilation),
                 })
        {
            // Null receiver: no-op.
            InvokeSites(assembly, "AssignField", null, 9);

            var box = NewBox(assembly);
            InvokeSites(assembly, "AssignField", box, 9);
            Assert.Equal(9, ((Type)BoxType(assembly)!).GetField("B")!.GetValue(box));
        }
    }

    [Fact]
    public void NA1_NonWhitelistReceiver_SegmentTemp_EvaluatedOnce()
    {
        var (result, _) = PreprocessSoleFile(SimpleSitesSource);

        Assert.Contains("var __ed_s0 = Box.Make(seed);", result.OutputCode);
        Assert.Contains("if (__ed_s0 != null)", result.OutputCode);
        Assert.Contains("__ed_s0.B = c", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode);
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(1, result.Statistics.NullConditional.AssignmentReceiverTempsLifted);

        foreach (var assembly in new[]
                 {
                     EmitAndLoad(BuildCompilation(SimpleSitesSource, "native-factory")),
                     EmitAndLoad(loweredCompilation),
                 })
        {
            // Null factory result: assignment skipped, no throw.
            InvokeSites(assembly, "FactoryReceiver", -1, 3);
            // Non-null factory: assignment executes once (no counter to read,
            // just parity of behavior — no throw, box discarded).
            InvokeSites(assembly, "FactoryReceiver", 1, 3);
        }
    }

    // ------------------------------------------------------------------
    // NA-2: index assignment; arguments live inside the guard.
    // ------------------------------------------------------------------

    [Fact]
    public void NA2_IndexAssignment_GuardedIf_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(SimpleSitesSource);

        Assert.Contains("if (a != null)", result.OutputCode);
        Assert.Contains("a[i] = v", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode);
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        // AssignIndex and IndexArgs both lower as index targets.
        Assert.Equal(2, result.Statistics.NullConditional.AssignmentIndexSites);

        foreach (var assembly in new[]
                 {
                     EmitAndLoad(native),
                     EmitAndLoad(loweredCompilation),
                 })
        {
            InvokeSites(assembly, "AssignIndex", null, 0, 5);

            var box = NewBox(assembly);
            InvokeSites(assembly, "AssignIndex", box, 0, 5);
            // indexer set: B = 5; reading [0] = B + 0 = 5.
            Assert.Equal(5, InvokeIndexer(assembly, box, 0));
        }
    }

    private static object? InvokeIndexer(Assembly assembly, object box, int i)
    {
        var prop = ((Type)BoxType(assembly)!).GetProperties()
            .Single(p => p.GetIndexParameters().Length == 1);
        return prop.GetValue(box, new object[] { i });
    }

    // ------------------------------------------------------------------
    // ★RHS / index-argument conditional evaluation (highest-risk rule).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // null receiver: RHS zero evaluation
    [InlineData(true)]  // non-null: exactly one
    public void NA1_Rhs_OnlyEvaluatedOnNonNullReceiver(bool nonNull)
    {
        foreach (var assembly in new[]
                 {
                     EmitAndLoad(BuildCompilation(SimpleSitesSource, "native-rhs")),
                     EmitAndLoad(BuildCompilation(
                         PreprocessSoleFile(SimpleSitesSource).Result.OutputCode,
                         "lowered-rhs")),
                 })
        {
            var boxType = (Type)BoxType(assembly)!;
            boxType.GetField("NextCalls")!.SetValue(null, 0);

            var box = nonNull ? NewBox(assembly) : null;
            InvokeSites(assembly, "ConditionalRhs", box);

            var calls = (int)boxType.GetField("NextCalls")!.GetValue(null)!;
            Assert.Equal(nonNull ? 1 : 0, calls);
            if (nonNull)
                Assert.Equal(1, boxType.GetField("B")!.GetValue(box));
        }
    }

    [Theory]
    [InlineData(false)] // null receiver: index arg AND value zero evaluation
    [InlineData(true)]  // non-null: each exactly once
    public void NA2_IndexArguments_OnlyEvaluatedOnNonNullReceiver(bool nonNull)
    {
        foreach (var assembly in new[]
                 {
                     EmitAndLoad(BuildCompilation(SimpleSitesSource, "native-ia")),
                     EmitAndLoad(BuildCompilation(
                         PreprocessSoleFile(SimpleSitesSource).Result.OutputCode,
                         "lowered-ia")),
                 })
        {
            var boxType = (Type)BoxType(assembly)!;
            boxType.GetField("NextCalls")!.SetValue(null, 0);

            var box = nonNull ? NewBox(assembly) : null;
            InvokeSites(assembly, "IndexArgs", box);

            var calls = (int)boxType.GetField("NextCalls")!.GetValue(null)!;
            Assert.Equal(nonNull ? 2 : 0, calls);
        }
    }

    // ------------------------------------------------------------------
    // NA-5: value forms (return / local initializer).
    // ------------------------------------------------------------------

    [Fact]
    public void NA5_ValueForms_NullTernary_LiftedResult_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(SimpleSitesSource);

        Assert.Contains("return (a != null ? (int?)(a.B = c) : (int?)null);", result.OutputCode);
        Assert.Contains("var x = (a != null ? (int?)(a.B = c) : (int?)null);", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode);
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(2, result.Statistics.NullConditional.ValueFormAssignmentSites);

        foreach (var assembly in new[]
                 {
                     EmitAndLoad(native),
                     EmitAndLoad(loweredCompilation),
                 })
        {
            // Null receiver → null value.
            Assert.Null(InvokeSites(assembly, "ValueReturn", null, 7));
            Assert.Null(InvokeSites(assembly, "ValueLocal", null, 7));

            // Non-null → assignment result value.
            var box1 = NewBox(assembly);
            Assert.Equal(7, InvokeSites(assembly, "ValueReturn", box1, 7));
            Assert.Equal(7, ((Type)BoxType(assembly)!).GetField("B")!.GetValue(box1));

            var box2 = NewBox(assembly);
            Assert.Equal(8, InvokeSites(assembly, "ValueLocal", box2, 8));
            Assert.Equal(8, ((Type)BoxType(assembly)!).GetField("B")!.GetValue(box2));
        }
    }

    /// <summary>NA-5 non-whitelist receiver: segment temp + value ternary.</summary>
    [Fact]
    public void NA5_NonWhitelistReceiver_ValueForm_SegmentTemp()
    {
        const string source = """
            namespace C51Fixture;

            public sealed class Box { public int B; public static Box? Make() => null; }

            public static class Sites { public static int? Go(int c) { return Box.Make()?.B = c; } }
            """;

        var (result, _) = PreprocessSoleFile(source);

        Assert.Contains("var __ed_s0 = Box.Make();", result.OutputCode);
        Assert.Contains("(int?)(__ed_s0.B = c)", result.OutputCode);
        Assert.Contains("__ed_s0 != null", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode);
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ------------------------------------------------------------------
    // Idempotency.
    // ------------------------------------------------------------------

    [Fact]
    public void Rewrite_IsIdempotent_SecondPassNoChangeNoAssignmentCA()
    {
        var (first, _) = PreprocessSoleFile(SimpleSitesSource);
        Assert.True(first.Changed);

        var secondCompilation = BuildCompilation(first.OutputCode, "c51-idem");
        var secondModel = secondCompilation.GetSemanticModel(secondCompilation.SyntaxTrees[0]);
        var second = new ExpressionDesugarPreprocessor()
            .Preprocess(first.OutputCode, secondModel, C51Enabled);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);

        var root = CSharpSyntaxTree.ParseText(first.OutputCode, ParseOptions)
            .GetCompilationUnitRoot();
        Assert.Empty(root.DescendantNodes()
            .OfType<ConditionalAccessExpressionSyntax>());
    }
}
