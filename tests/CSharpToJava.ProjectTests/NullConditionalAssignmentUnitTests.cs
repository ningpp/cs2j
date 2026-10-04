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

    // ------------------------------------------------------------------
    // NA-3: compound assignments (+= -= *= /= %= &= |= ^= <<= >>=) and
    // event +/-. Compound assignment is never decomposed; the original
    // assignment node moves inside the guard intact.
    // ------------------------------------------------------------------

    private const string CompoundSource = """
        namespace C51Fixture;

        public sealed class Num
        {
            public int A, B, C, D, E, F, G, H, I, J;
            public static int RhsCalls;
            public static int Rhs() { RhsCalls++; return 2; }
        }

        public static class Ops
        {
            public static void All(Num? n, int x)
            {
                n?.A += x;
                n?.B -= x;
                n?.C *= x;
                n?.D /= x;
                n?.E %= x;
                n?.F &= x;
                n?.G |= x;
                n?.H ^= x;
                n?.I <<= x;
                n?.J >>= x;
            }

            public static void RhsGuarded(Num? n) { n?.A += Num.Rhs(); }
        }
        """;

    [Fact]
    public void NA3_AllCompoundOperators_KeptInsideGuard_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(CompoundSource);

        var fieldLetters = new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };
        var compoundOps = new[] { "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=" };
        for (var i = 0; i < compoundOps.Length; i++)
            Assert.Contains($"n.{fieldLetters[i]} {compoundOps[i]} x;", result.OutputCode);

        var stats = result.Statistics.NullConditional;
        // All() has ten compounds; RhsGuarded() is an eleventh '+='.
        Assert.Equal(11, stats.CompoundAssignmentSites);
        Assert.Equal(2, stats.AssignmentByOperator["+"]);
        foreach (var label in new[] { "-", "*", "/", "%", "&", "|", "^", "<<", ">>" })
            Assert.Equal(1, stats.AssignmentByOperator[label]);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-compound");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        var nativeAssembly = EmitAndLoad(native);
        var loweredAssembly = EmitAndLoad(loweredCompilation);
        foreach (var assembly in new[] { nativeAssembly, loweredAssembly })
        {
            var n = NewOf(assembly, "Num");
            InvokeOn(assembly, "Ops", "All", n, 2);
            var numType = n!.GetType();
            Assert.Equal(2, numType.GetField("A")!.GetValue(n));
            Assert.Equal(-2, numType.GetField("B")!.GetValue(n));
            Assert.Equal(2, numType.GetField("G")!.GetValue(n));
            Assert.Equal(0, numType.GetField("C")!.GetValue(n));
        }

        // Null receiver: guarded RHS must not be evaluated.
        var loweredNull = EmitAndLoad(loweredCompilation);
        loweredNull.GetType("C51Fixture.Num")!.GetField("RhsCalls")!.SetValue(null, 0);
        InvokeOn(loweredNull, "Ops", "RhsGuarded", new object?[] { null });
        Assert.Equal(0, (int)loweredNull.GetType("C51Fixture.Num")!.GetField("RhsCalls")!.GetValue(null)!);
    }

    private const string EventSource = """
        namespace C51Fixture;

        using System;

        public sealed class Host
        {
            public int Adds;
            public int Removes;
            public event Action? E
            {
                add { Adds++; }
                remove { Removes++; }
            }
        }

        public static class Ev
        {
            public static void On(Host? h, Action a) { h?.E += a; }
            public static void Off(Host? h, Action a) { h?.E -= a; }
        }
        """;

    [Fact]
    public void NA3_EventCompound_CustomAccessors_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(EventSource);

        Assert.Contains("h.E += a;", result.OutputCode);
        Assert.Contains("h.E -= a;", result.OutputCode);
        Assert.Equal(2, result.Statistics.NullConditional.EventCompoundSites);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-event");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            InvokeOn(assembly, "Ev", "On", null, new Action(() => { }));
            var h = NewOf(assembly, "Host");
            InvokeOn(assembly, "Ev", "On", h, new Action(() => { }));
            InvokeOn(assembly, "Ev", "On", h, new Action(() => { }));
            InvokeOn(assembly, "Ev", "Off", h, new Action(() => { }));
            var hostType = h!.GetType();
            Assert.Equal(2, hostType.GetField("Adds")!.GetValue(h));
            Assert.Equal(1, hostType.GetField("Removes")!.GetValue(h));
        }
    }

    private const string PropertyCompoundSource = """
        namespace C51Fixture;

        public sealed class Prop
        {
            public int Gets;
            public int Sets;
            private int _p = 10;
            public int P
            {
                get { Gets++; return _p; }
                set { Sets++; _p = value; }
            }
        }

        public static class PropSites
        {
            public static void Add(Prop? p, int x) { p?.P += x; }
        }
        """;

    [Fact]
    public void NA3_PropertyCompound_GetAndSetEachOnce_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(PropertyCompoundSource);

        Assert.Contains("p.P += x;", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-prop");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            InvokeOn(assembly, "PropSites", "Add", null, 5);

            var p = NewOf(assembly, "Prop");
            InvokeOn(assembly, "PropSites", "Add", p, 5);
            var propType = p!.GetType();
            Assert.Equal(1, propType.GetField("Gets")!.GetValue(p));
            Assert.Equal(1, propType.GetField("Sets")!.GetValue(p));
            Assert.Equal(15, propType.GetProperty("P")!.GetValue(p));
        }
    }

    // ------------------------------------------------------------------
    // NA-4: chains. M7 single '?.' with ordinary member tail; M8 multiple
    // segments: statement -> nested ifs, value -> nested ternaries; plus an
    // ElementBinding chained receiver.
    // ------------------------------------------------------------------

    private const string ChainSource = """
        namespace C51Fixture;

        public sealed class Node
        {
            public int Val;
            public Node? Next;
        }

        public static class ChainSites
        {
            // M7: one '?.' then an ordinary member tail.
            public static void M7(Node? a, int d) { a?.Next.Val = d; }

            // M8 statement form.
            public static void M8(Node? a, int d) { a?.Next?.Val = d; }

            // M8 value form.
            public static int? M8Value(Node? a, int d) { return a?.Next?.Val = d; }
        }
        """;

    [Fact]
    public void NA4_M7_OrdinaryTail_GuardedIf_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(ChainSource);

        Assert.Contains("if (a != null)", result.OutputCode);
        Assert.Contains("a.Next.Val = d;", result.OutputCode);
        // Fixture carries three chain sites total (M7/M8/M8Value).
        Assert.Equal(3, result.Statistics.NullConditional.AssignmentChainSites);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-chain");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            InvokeOn(assembly, "ChainSites", "M7", null, 4);

            var target = NewNodeChain(assembly);
            var a = target.head;
            InvokeOn(assembly, "ChainSites", "M7", a, 4);
            Assert.Equal(4, ((NodeLike)target.tail).Val);
        }
    }

    [Fact]
    public void NA4_M8_MultiStatement_NestedIfs_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(ChainSource);

        Assert.Contains("var __ed_s0 = a;", result.OutputCode);
        Assert.Contains("C51Fixture.Node? __ed_s1 = null;", result.OutputCode);
        Assert.Contains("__ed_s1 = __ed_s0.Next;", result.OutputCode);
        Assert.Contains("if (__ed_s0 != null)", result.OutputCode);
        Assert.Contains("if (__ed_s1 != null)", result.OutputCode);
        Assert.Contains("__ed_s1.Val = d;", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-chain2");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        // Three disks: outer null, inner null, both non-null.
        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            InvokeOn(assembly, "ChainSites", "M8", null, 9);

            var outerOnly = NewOf(assembly, "Node");
            InvokeOn(assembly, "ChainSites", "M8", outerOnly, 9);

            var pair = NewNodeChain(assembly);
            InvokeOn(assembly, "ChainSites", "M8", pair.head, 9);
            Assert.Equal(9, ((NodeLike)pair.tail).Val);
        }
    }

    [Fact]
    public void NA4_M8_MultiValue_NestedTernaries_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(ChainSource);

        Assert.Contains("C51Fixture.Node? __ed_s3 = null;", result.OutputCode);
        Assert.Contains("__ed_s3 = __ed_s2.Next;", result.OutputCode);
        Assert.Contains("(int?)(__ed_s3.Val = d)", result.OutputCode);
        Assert.Contains("__ed_s3 != null", result.OutputCode);
        Assert.Contains("__ed_s2 != null", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-chain3");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            Assert.Null(InvokeOn(assembly, "ChainSites", "M8Value", null, 7));

            var outerOnly = NewOf(assembly, "Node");
            Assert.Null(InvokeOn(assembly, "ChainSites", "M8Value", outerOnly, 7));

            var pair = NewNodeChain(assembly);
            Assert.Equal(7, InvokeOn(assembly, "ChainSites", "M8Value", pair.head, 7));
            Assert.Equal(7, ((NodeLike)pair.tail).Val);
        }
    }

    private const string IndexChainSource = """
        namespace C51Fixture;

        public sealed class Cell { public int Val; }

        public static class IdxChain
        {
            public static void CellValPlus(Cell?[]? arr, int d) { arr?[0]?.Val += d; }
        }
        """;

    [Fact]
    public void NA4_IndexChain_Compound_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(IndexChainSource);

        Assert.Contains("var __ed_s0 = arr;", result.OutputCode);
        Assert.Contains("C51Fixture.Cell? __ed_s1 = null;", result.OutputCode);
        Assert.Contains("__ed_s1 = __ed_s0[0];", result.OutputCode);
        Assert.Contains("__ed_s1.Val += d;", result.OutputCode);
        Assert.Equal(1, result.Statistics.NullConditional.AssignmentByOperator["+"]);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-idxchain");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            InvokeOn(assembly, "IdxChain", "CellValPlus", null, 3);

            var nulls = (object?)Array.CreateInstance(
                assembly.GetType("C51Fixture.Cell", throwOnError: true)!, 1);
            InvokeOn(assembly, "IdxChain", "CellValPlus", nulls, 3);

            var arr = Array.CreateInstance(assembly.GetType("C51Fixture.Cell", throwOnError: true)!, 1);
            var cell = NewOf(assembly, "Cell")!;
            arr.SetValue(cell, 0);
            InvokeOn(assembly, "IdxChain", "CellValPlus", arr, 3);
            Assert.Equal(3, cell.GetType().GetField("Val")!.GetValue(cell));
        }
    }

    // ------------------------------------------------------------------
    // Local reflection helpers for the NA-3/NA-4 fixtures.
    // ------------------------------------------------------------------

    private static object? NewOf(Assembly assembly, string typeName)
        => Activator.CreateInstance(
            assembly.GetType("C51Fixture." + typeName, throwOnError: true)!);

    private static object? InvokeOn(
        Assembly assembly, string typeName, string methodName, params object?[] args)
        => assembly.GetType("C51Fixture." + typeName, throwOnError: true)!
            .GetMethod(methodName)!
            .Invoke(null, args);

    private sealed class NodeLike
    {
        public required object Raw;
        public int Val => (int)Raw.GetType().GetField("Val")!.GetValue(Raw)!;
    }

    private static (object head, NodeLike tail) NewNodeChain(Assembly assembly)
    {
        var head = NewOf(assembly, "Node")!;
        var tail = new NodeLike { Raw = NewOf(assembly, "Node")! };
        head.GetType().GetField("Next")!.SetValue(head, tail.Raw);
        return (head, tail);
    }

    // ------------------------------------------------------------------
    // NA-6: ??= inside a conditional access (design §3.5).
    // ------------------------------------------------------------------

    private const string CoalesceSource = """
        namespace C51Fixture;

        public sealed class Holder
        {
            public string? S;
            public int? N;
            public string? this[int i] { get => S; set => S = value; }

            public static int RhsCalls;
            public static string Make() { RhsCalls++; return "made"; }
        }

        public static class CoalesceSites
        {
            public static void Member(Holder? h) { h?.S ??= Holder.Make(); }
            public static void Indexer(Holder? h) { h?[0] ??= Holder.Make(); }
            public static string? Value(Holder? h) { return h?.S ??= Holder.Make(); }
            public static int? ValueN(Holder? h, int c) { return h?.N ??= c; }
        }
        """;

    [Fact]
    public void NA6_CoalesceMember_Statement_ReadOnce_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(CoalesceSource);

        Assert.Contains("var __ed_n0 = h.S;", result.OutputCode);
        Assert.Contains("if (__ed_n0 == null)", result.OutputCode);
        Assert.Contains("h.S = Holder.Make();", result.OutputCode);
        Assert.DoesNotContain("??=", result.OutputCode);
        Assert.Equal(4, result.Statistics.NullConditional.CoalesceAssignmentSites);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-nullcoalesce");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            var holderType = assembly.GetType("C51Fixture.Holder", throwOnError: true)!;

            InvokeOn(assembly, "CoalesceSites", "Member", new object?[] { null });

            holderType.GetField("RhsCalls")!.SetValue(null, 0);
            var empty = Activator.CreateInstance(holderType)!;
            InvokeOn(assembly, "CoalesceSites", "Member", empty);
            Assert.Equal("made", holderType.GetField("S")!.GetValue(empty));
            Assert.Equal(1, holderType.GetField("RhsCalls")!.GetValue(null));

            holderType.GetField("RhsCalls")!.SetValue(null, 0);
            var already = Activator.CreateInstance(holderType)!;
            holderType.GetField("S")!.SetValue(already, "kept");
            InvokeOn(assembly, "CoalesceSites", "Member", already);
            Assert.Equal("kept", holderType.GetField("S")!.GetValue(already));
            Assert.Equal(0, holderType.GetField("RhsCalls")!.GetValue(null));
        }
    }

    [Fact]
    public void NA6_CoalesceIndexer_Statement_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(CoalesceSource);

        Assert.Contains("var __ed_n1 = h[0];", result.OutputCode);
        Assert.Contains("h[0] = Holder.Make();", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-nullcoalesce2");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            var holderType = assembly.GetType("C51Fixture.Holder", throwOnError: true)!;
            holderType.GetField("RhsCalls")!.SetValue(null, 0);

            InvokeOn(assembly, "CoalesceSites", "Indexer", new object?[] { null });
            Assert.Equal(0, holderType.GetField("RhsCalls")!.GetValue(null));

            var holder = Activator.CreateInstance(holderType)!;
            InvokeOn(assembly, "CoalesceSites", "Indexer", holder);
            Assert.Equal("made", holderType.GetField("S")!.GetValue(holder));
        }
    }

    [Fact]
    public void NA6_CoalesceValue_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(CoalesceSource);

        Assert.Contains("string? __ed_n2 = null;", result.OutputCode);
        Assert.Contains("int? __ed_n3 = null;", result.OutputCode);
        Assert.Contains("__ed_n2 == null ? __ed_s0.S = Holder.Make() : __ed_n2", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-nullcoalesce3");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            var holderType = assembly.GetType("C51Fixture.Holder", throwOnError: true)!;

            Assert.Null(InvokeOn(assembly, "CoalesceSites", "Value", new object?[] { null }));

            var empty = Activator.CreateInstance(holderType)!;
            holderType.GetField("RhsCalls")!.SetValue(null, 0);
            Assert.Equal("made", InvokeOn(assembly, "CoalesceSites", "Value", empty));
            Assert.Equal("made", holderType.GetField("S")!.GetValue(empty));
            Assert.Equal(1, holderType.GetField("RhsCalls")!.GetValue(null));

            var already = Activator.CreateInstance(holderType)!;
            holderType.GetField("S")!.SetValue(already, "kept");
            holderType.GetField("RhsCalls")!.SetValue(null, 0);
            Assert.Equal("kept", InvokeOn(assembly, "CoalesceSites", "Value", already));
            Assert.Equal(0, holderType.GetField("RhsCalls")!.GetValue(null));

            var number = Activator.CreateInstance(holderType)!;
            Assert.Equal(5, InvokeOn(assembly, "CoalesceSites", "ValueN", number, 5));
            Assert.Equal(5, holderType.GetField("N")!.GetValue(number));

            var numberSet = Activator.CreateInstance(holderType)!;
            holderType.GetField("N")!.SetValue(numberSet, 9);
            Assert.Equal(9, InvokeOn(assembly, "CoalesceSites", "ValueN", numberSet, 5));
        }
    }

    [Fact]
    public void NA6_CoalesceMultiSegment_StatementAndValue_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(ComposedSource);

        Assert.Contains("var __ed_n0 = __ed_s1.S;", result.OutputCode);
        Assert.Contains("string? __ed_n1 = null;", result.OutputCode);
        Assert.Contains("__ed_n1 == null ? __ed_s3.S = c : __ed_n1", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-nullcoalesce-chain");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            InvokeOn(assembly, "ComposedSites", "CoalesceChain", new object?[] { null, "c" });
            Assert.Null(InvokeOn(assembly, "ComposedSites", "CoalesceChainValue", new object?[] { null, "c" }));

            // Outer non-null but inner null: no assignment, null result.
            var outerOnly = NewOf(assembly, "Node");
            InvokeOn(assembly, "ComposedSites", "CoalesceChain", outerOnly, "c");
            Assert.Null(InvokeOn(assembly, "ComposedSites", "CoalesceChainValue", outerOnly, "c"));

            // Both non-null, member null: set, value returned.
            var pair = NewNodeChain(assembly);
            InvokeOn(assembly, "ComposedSites", "CoalesceChain", pair.head, "c");
            Assert.Equal("c", ((NodeLike)pair.tail).Raw.GetType().GetField("S")!.GetValue(((NodeLike)pair.tail).Raw));

            var pair2 = NewNodeChain(assembly);
            Assert.Equal("c2", InvokeOn(assembly, "ComposedSites", "CoalesceChainValue", pair2.head, "c2"));

            // Member already set: kept.
            var pair3 = NewNodeChain(assembly);
            ((NodeLike)pair3.tail).Raw.GetType().GetField("S")!.SetValue(((NodeLike)pair3.tail).Raw, "old");
            Assert.Equal("old", InvokeOn(assembly, "ComposedSites", "CoalesceChainValue", pair3.head, "c3"));
        }
    }

    // ------------------------------------------------------------------
    // NA-7: RHS/inner compositions closed in the same bottom-up pass.
    // ------------------------------------------------------------------

    private const string ComposedSource = """
        namespace C51Fixture;

        public sealed class Node
        {
            public string? S;
            public Node? Next;
        }

        public sealed class Other
        {
            public string? D;
            public static int Calls;
            public static string V() { Calls++; return "v"; }
        }

        public static class ComposedSites
        {
            public static void CoalesceChain(Node? a, string c) { a?.Next?.S ??= c; }
            public static string? CoalesceChainValue(Node? a, string c) { return a?.Next?.S ??= c; }
            public static void RhsRead(Node? a, Other? o) { a?.S = o?.D; }
            public static void RhsAssign(Node? a, Other? o) { a?.S = o?.D = "x"; }
            public static void ThreeLayers(Node? a, Other? o) { a?.S = o?.D = Other.V(); }
        }
        """;

    [Fact]
    public void NA7_RhsReadComposition_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(ComposedSource);

        Assert.Contains("a.S = (o != null ? o.D : null);", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-composed");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            var nodeType = assembly.GetType("C51Fixture.Node", throwOnError: true)!;
            var otherType = assembly.GetType("C51Fixture.Other", throwOnError: true)!;

            // Outer null: nothing happens.
            InvokeOn(assembly, "ComposedSites", "RhsRead", new object?[] { null, null });

            // Outer set, inner null: outer receives null.
            var node1 = Activator.CreateInstance(nodeType)!;
            InvokeOn(assembly, "ComposedSites", "RhsRead", node1, null);
            Assert.Null(nodeType.GetField("S")!.GetValue(node1));

            // Both set: outer receives inner's D.
            var node2 = Activator.CreateInstance(nodeType)!;
            var other = Activator.CreateInstance(otherType)!;
            otherType.GetField("D")!.SetValue(other, "dval");
            InvokeOn(assembly, "ComposedSites", "RhsRead", node2, other);
            Assert.Equal("dval", nodeType.GetField("S")!.GetValue(node2));
        }
    }

    [Fact]
    public void NA7_RhsInnerAssignment_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(ComposedSource);

        Assert.Contains("a.S = (o != null ? o.D = \"x\" : null);", result.OutputCode);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-composed2");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            var nodeType = assembly.GetType("C51Fixture.Node", throwOnError: true)!;
            var otherType = assembly.GetType("C51Fixture.Other", throwOnError: true)!;

            // Both non-null: inner assignment executes, outer gets "x".
            var node = Activator.CreateInstance(nodeType)!;
            var other = Activator.CreateInstance(otherType)!;
            InvokeOn(assembly, "ComposedSites", "RhsAssign", node, other);
            Assert.Equal("x", nodeType.GetField("S")!.GetValue(node));
            Assert.Equal("x", otherType.GetField("D")!.GetValue(other));

            // Inner null: outer gets null, nothing assigned anywhere.
            var node2 = Activator.CreateInstance(nodeType)!;
            InvokeOn(assembly, "ComposedSites", "RhsAssign", node2, null);
            Assert.Null(nodeType.GetField("S")!.GetValue(node2));
        }
    }

    [Fact]
    public void NA7_ThreeLayers_SideEffectOrder_BehavesIdentically()
    {
        var (result, native) = PreprocessSoleFile(ComposedSource);

        Assert.Contains("a.S = (o != null ? o.D = Other.V() : null);", result.OutputCode);
        Assert.Equal(3, result.Statistics.NullConditional.AssignmentRhsConditionalSites);

        var loweredCompilation = BuildCompilation(result.OutputCode, "c51-composed3");
        Assert.Empty(loweredCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var assembly in new[] { EmitAndLoad(native), EmitAndLoad(loweredCompilation) })
        {
            var nodeType = assembly.GetType("C51Fixture.Node", throwOnError: true)!;
            var otherType = assembly.GetType("C51Fixture.Other", throwOnError: true)!;

            // Outer null: V never called.
            otherType.GetField("Calls")!.SetValue(null, 0);
            InvokeOn(assembly, "ComposedSites", "ThreeLayers", new object?[] { null, null });
            Assert.Equal(0, otherType.GetField("Calls")!.GetValue(null));

            // Outer set, inner null: V never called.
            var nodeOuter = Activator.CreateInstance(nodeType)!;
            InvokeOn(assembly, "ComposedSites", "ThreeLayers", nodeOuter, null);
            Assert.Equal(0, otherType.GetField("Calls")!.GetValue(null));
            Assert.Null(nodeType.GetField("S")!.GetValue(nodeOuter));

            // Both set: V once, assignment chain result propagates.
            var node = Activator.CreateInstance(nodeType)!;
            var other = Activator.CreateInstance(otherType)!;
            InvokeOn(assembly, "ComposedSites", "ThreeLayers", node, other);
            Assert.Equal(1, otherType.GetField("Calls")!.GetValue(null));
            Assert.Equal("v", otherType.GetField("D")!.GetValue(other));
            Assert.Equal("v", nodeType.GetField("S")!.GetValue(node));
        }
    }

    // ------------------------------------------------------------------
    // NA-8: residual gate explicit assignment-terminal check (CS2J0566).
    // ------------------------------------------------------------------

    [Fact]
    public void NA8_UnpairedAssignmentTerminalResidual_TriggersCS2J0566()
    {
        // A standalone assignment-terminal CA with no plan and no explained
        // annotation (synthetic: syntax-only, no semantic model).
        var root = CSharpSyntaxTree.ParseText(
                "class C { void M() { a?.b = c; } }", ParseOptions)
            .GetCompilationUnitRoot();

        var context = new RuleAnalysisContext
        {
            Root = root,
            SemanticModel = null,
            Options = new ExpressionDesugarOptions
            {
                NullConditional = new NullConditionalOptions(),
            },
            Statistics = new ExpressionDesugarStatistics(),
            Diagnostics = new List<ExpressionDesugarDiagnostic>(),
        };

        var unpaired = NullConditionalResidualGate.Check(context, root);

        // CA + its orphan MemberBinding are two unpaired residual nodes.
        Assert.Equal(2, unpaired);
        Assert.Equal(1, context.Statistics.NullConditional.AssignmentResidualNodeCount);
        Assert.Contains(context.Diagnostics,
            d => d.Code == NullConditionalDiagnosticCodes.CS2J0566);
    }

    [Fact]
    public void NA8_ExplainedAssignmentResidual_IsPaired_NoGate()
    {
        // Same shape but carrying the explained annotation: gate stays zero.
        var parsed = CSharpSyntaxTree.ParseText(
                "class C { void M() { a?.b = c; } }", ParseOptions)
            .GetCompilationUnitRoot();
        var ca = parsed.DescendantNodes()
            .OfType<ConditionalAccessExpressionSyntax>().Single();
        var root = (CompilationUnitSyntax)parsed
            .ReplaceNode(ca, ca.WithAdditionalAnnotations(
                ExpressionDesugarSyntaxFacts.ExplainedAnnotation));

        var context = new RuleAnalysisContext
        {
            Root = root,
            SemanticModel = null,
            Options = new ExpressionDesugarOptions
            {
                NullConditional = new NullConditionalOptions(),
            },
            Statistics = new ExpressionDesugarStatistics(),
            Diagnostics = new List<ExpressionDesugarDiagnostic>(),
        };

        Assert.Equal(0, NullConditionalResidualGate.Check(context, root));
        Assert.Empty(context.Diagnostics);
    }
}
