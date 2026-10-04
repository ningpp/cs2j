using System.Reflection;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C35 null-conditional read-side unit gate (design §7, NC-1 first slice).
/// Every fixture is an in-memory source: compile natively with nullable
/// enabled, preprocess through the Core <see cref="ExpressionDesugarPreprocessor"/>
/// with the <see cref="NullConditionalOptions"/> subsection, then assert
/// diagnostics, lowered shape, recompilability, idempotency and equal
/// observable behavior on both assemblies. The rollout gate (subsection
/// absent = byte-identical) is pinned here too.
/// </summary>
public class NullConditionalDesugarUnitTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview);

    private static readonly IReadOnlyList<MetadataReference> References = LoadReferences();

    private static readonly ExpressionDesugarOptions C35Enabled = new()
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

    private const string CoreTypesAndSitesSource = """
        namespace C35Fixture;

        public sealed class C
        {
            public int B;
            public C(int b) { B = b; }
            public static C? Make(int seed) => seed < 0 ? null : new C(seed);
        }

        public sealed class Holder
        {
            public int B;
            public Holder(int b) { B = b; }
            public int? ViaThis() => this?.B ?? -5;
        }

        public static class Sites
        {
            private static C? _field;

            public static int FromLocal(C? a)
            {
                var x = a?.B ?? -1;
                return x;
            }

            public static int FromParam(C? a) => a?.B ?? -2;

            public static int FromField(C? value)
            {
                _field = value;
                return _field?.B ?? -3;
            }

            public static int FromThis(int value) => new Holder(value).ViaThis() ?? -6;
        }
        """;

    /// <summary>Standalone fixture for the non-whitelist invocation receiver (TempTernary).</summary>
    private const string FactoryCallSource = """
        namespace C35Fixture;

        public sealed class C2
        {
            public int B;
            public C2(int b) { B = b; }
            public static C2? Make(int seed) => seed < 0 ? null : new C2(seed);
        }

        public static class FactorySites
        {
            public static int FromFactoryCall(int seed) => C2.Make(seed)?.B ?? -9;
        }
        """;

    private static CSharpCompilation BuildCompilation(string source, string name = "c35-fixture")
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
        var result = new ExpressionDesugarPreprocessor().Preprocess(source, model, C35Enabled);
        return (result, native);
    }

    [Fact]
    public void NC1_WhitelistReceivers_InlineTernary_NoResidual_AndRecompiles()
    {
        var (result, _) = PreprocessSoleFile(CoreTypesAndSitesSource);

        Assert.True(result.Changed, "whitelist sites should be rewritten");
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);

        var outputRoot = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions)
            .GetCompilationUnitRoot();
        Assert.Empty(outputRoot.DescendantNodes()
            .OfType<ConditionalAccessExpressionSyntax>());
        Assert.Empty(outputRoot.DescendantNodes()
            .OfType<MemberBindingExpressionSyntax>());
        Assert.Contains("!= null", result.OutputCode);

        // Inline = zero segment temps.
        Assert.DoesNotContain("__ed_s", result.OutputCode);

        var lowered = BuildCompilation(result.OutputCode);
        Assert.Empty(lowered.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void NC1_NonWhitelistReceiver_UsesSegmentTemp_AndRecompiles()
    {
        // Invocation receiver C2.Make(seed) is not on the inline whitelist.
        var (result, _) = PreprocessSoleFile(FactoryCallSource);

        Assert.Contains("__ed_s0", result.OutputCode);
        Assert.Contains("__ed_s0 != null", result.OutputCode);
        Assert.Equal(1, result.Statistics.NullConditional.SegmentTempsLifted);

        var lowered = BuildCompilation(result.OutputCode);
        Assert.Empty(lowered.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData(-7, -9)]   // null receiver on the call site
    [InlineData(42, 42)]   // non-null receiver
    public void NC1_LoweredOutput_BehavesIdentically_NullAndNonNull(int seed, int expected)
    {
        var (result, native) = PreprocessSoleFile(FactoryCallSource);
        var nativeAssembly = EmitAndLoad(native);
        var loweredAssembly = EmitAndLoad(BuildCompilation(result.OutputCode));

        foreach (var assembly in new[] { nativeAssembly, loweredAssembly })
        {
            var sites = assembly.GetType("C35Fixture.FactorySites", throwOnError: true)!;
            var value = (int)sites.GetMethod(
                "FromFactoryCall",
                BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { seed })!;
            Assert.Equal(expected, value);
        }
    }

    [Theory]
    [InlineData(null, -1)]
    [InlineData(42, 42)]
    public void NC1_AllSiteShapes_BehaveIdentically_AcrossAssemblies(int? bValue, int expectedFromLocal)
    {
        var (result, native) = PreprocessSoleFile(CoreTypesAndSitesSource);
        var nativeAssembly = EmitAndLoad(native);
        var loweredAssembly = EmitAndLoad(BuildCompilation(result.OutputCode));

        foreach (var assembly in new[] { nativeAssembly, loweredAssembly })
        {
            var sites = assembly.GetType("C35Fixture.Sites", throwOnError: true)!;
            var cType = assembly.GetType("C35Fixture.C", throwOnError: true)!;
            object? cArg = bValue == null
                ? null
                : Activator.CreateInstance(cType, bValue.Value);

            Assert.Equal(expectedFromLocal, Invoke(sites, "FromLocal", cArg));
            Assert.Equal(bValue == null ? -2 : bValue, Invoke(sites, "FromParam", cArg));
            Assert.Equal(bValue == null ? -3 : bValue, Invoke(sites, "FromField", cArg));
            // this is never null, so ViaThis yields B (the seed) directly.
            Assert.Equal(bValue ?? 0, Invoke(sites, "FromThis", bValue ?? 0));
        }
    }

    private static int Invoke(Type sites, string method, params object?[] args)
        => (int)sites.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, args)!;

    [Fact]
    public void NC1_AnchorFreeFieldInitializer_WhitelistReceiver_Inlined()
    {
        const string source = """
            namespace C35Fixture;

            public sealed class C { public int B; }

            public sealed class AnchorFree
            {
                internal static C? Source;
                public int? Value = Source?.B;
            }
            """;

        var (result, _) = PreprocessSoleFile(source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);
        Assert.Contains("Source != null", result.OutputCode);
        Assert.DoesNotContain("__ed_s", result.OutputCode);

        var lowered = BuildCompilation(result.OutputCode);
        Assert.Empty(lowered.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        // Behavior: null source -> null Value; non-null -> B.
        var assembly = EmitAndLoad(lowered);
        var anchorType = assembly.GetType("C35Fixture.AnchorFree", throwOnError: true)!;
        var cType = assembly.GetType("C35Fixture.C", throwOnError: true)!;
        var sourceField = anchorType.GetField(
            "Source", BindingFlags.Static | BindingFlags.NonPublic)!;

        sourceField.SetValue(null, null);
        Assert.Null(anchorType.GetField("Value")!.GetValue(Activator.CreateInstance(anchorType)));

        var c = Activator.CreateInstance(cType)!;
        cType.GetField("B")!.SetValue(c, 77);
        sourceField.SetValue(null, c);
        Assert.Equal(77, anchorType.GetField("Value")!.GetValue(Activator.CreateInstance(anchorType)));
    }

    [Fact]
    public void NC1_AnchorFreeFieldInitializer_NonWhitelistReceiver_CS2J0557_AtomicNoRewrite()
    {
        const string source = """
            namespace C35Fixture;

            public sealed class C { public int B; public static C? Source() => null; }

            public sealed class AnchorFreeBad
            {
                public int? Value = C.Source()?.B;
            }
            """;

        var (result, _) = PreprocessSoleFile(source);

        Assert.Contains(
            result.Diagnostics, d => d.Code == NullConditionalDiagnosticCodes.CS2J0557);
        Assert.Contains(
            result.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void NC1_Rewrite_IsIdempotent()
    {
        var (first, _) = PreprocessSoleFile(CoreTypesAndSitesSource);
        Assert.True(first.Changed);

        var secondCompilation = BuildCompilation(first.OutputCode, "c35-idem");
        var secondModel = secondCompilation.GetSemanticModel(secondCompilation.SyntaxTrees[0]);
        var second = new ExpressionDesugarPreprocessor()
            .Preprocess(first.OutputCode, secondModel, C35Enabled);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void Gate_SubsectionAbsent_OutputByteIdenticalToInput()
    {
        var native = BuildCompilation(CoreTypesAndSitesSource);
        var model = native.GetSemanticModel(native.SyntaxTrees[0]);

        var result = new ExpressionDesugarPreprocessor()
            .Preprocess(CoreTypesAndSitesSource, model, ExpressionDesugarOptions.Default);

        Assert.False(result.Changed);
        Assert.Equal(CoreTypesAndSitesSource, result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => true);
    }

    // ==================================================================
    // Milestone 2: NC-3 calls, NC-4 index, NC-5 Invoke, NC-2/8 multi.
    // ==================================================================

    private const string CallSource = """
        namespace C35Fixture;

        public sealed class CallTarget
        {
            public int SideCalls;
            public int Next() { SideCalls++; return SideCalls; }
            public int M(int a, int b) => a * 10 + b;
        }

        public static class CallFixture
        {
            public static int ValueCall(CallTarget? c)
                => c?.M(c.Next(), c.Next()) ?? -100;
        }
        """;

    private static void AssertResidualFree(string output)
    {
        var root = CSharpSyntaxTree.ParseText(output, ParseOptions)
            .GetCompilationUnitRoot();
        Assert.Empty(root.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>());
        Assert.Empty(root.DescendantNodes().OfType<ElementBindingExpressionSyntax>());
        Assert.Empty(root.DescendantNodes().OfType<MemberBindingExpressionSyntax>());
    }

    private static Assembly LoweredAssembly(string source, out ExpressionDesugarResult result)
    {
        var (preprocessed, _) = PreprocessSoleFile(source);
        result = preprocessed;
        Assert.DoesNotContain(
            preprocessed.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);
        Assert.True(preprocessed.Changed);
        AssertResidualFree(preprocessed.OutputCode);
        var compilation = BuildCompilation(preprocessed.OutputCode);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        return EmitAndLoad(compilation);
    }

    [Fact]
    public void NC3_ValueCall_ArgsEvaluatedOnce_OnlyOnNonNullPath()
    {
        var statsAssertions = new Action<NullConditionalStatistics>(s =>
        {
            Assert.Equal(1, s.ConditionalAccessSites);
            Assert.Equal(1, s.SegmentTempsLifted);
        });

        foreach (var assembly in new[]
                 {
                     EmitAndLoad(BuildCompilation(CallSource)),
                     LoweredAssembly(CallSource, out var lowered),
                 })
        {
            var fixture = assembly.GetType("C35Fixture.CallFixture", throwOnError: true)!;
            var targetType = assembly.GetType("C35Fixture.CallTarget", throwOnError: true)!;

            // Null receiver: fallback and ZERO argument evaluation.
            var nullResult = (int)fixture.GetMethod("ValueCall")!
                .Invoke(null, new object?[] { null })!;
            Assert.Equal(-100, nullResult);

            // Non-null: M(1,2)=12; each side-effecting arg evaluated once.
            var target = Activator.CreateInstance(targetType)!;
            var nonNullResult = (int)fixture.GetMethod("ValueCall")!
                .Invoke(null, new[] { target })!;
            Assert.Equal(12, nonNullResult);
            Assert.Equal(2, targetType.GetField("SideCalls")!.GetValue(target));
        }

        var (result, _) = PreprocessSoleFile(CallSource);
        statsAssertions(result.Statistics.NullConditional);
    }

    private const string IndexSource = """
        using System.Collections.Generic;

        namespace C35Fixture;

        public sealed class Indexed
        {
            public int Calls;
            public int Next() { Calls++; return Calls - 1; }
            public int this[int i] => i + 1000;
        }

        public static class IndexFixture
        {
            public static int ArrayIndex(int[]? a, Indexed ix) => a?[ix.Next()] ?? -1;
            public static int ListIndex(List<int>? l, Indexed ix) => l?[ix.Next()] ?? -2;
            public static int CustomIndex(Indexed? ix, int i) => ix?[i] ?? -3;
        }
        """;

    [Fact]
    public void NC4_ConditionalIndex_Array_List_CustomIndexer_BehaveIdentically()
    {
        Assembly[] assemblies =
        {
            EmitAndLoad(BuildCompilation(IndexSource)),
            LoweredAssembly(IndexSource, out var lowered),
        };

        foreach (var assembly in assemblies)
        {
            var fixture = assembly.GetType("C35Fixture.IndexFixture", throwOnError: true)!;
            var indexedType = assembly.GetType("C35Fixture.Indexed", throwOnError: true)!;
            var listType = typeof(List<int>);

            var indexed = Activator.CreateInstance(indexedType)!;

            // Array: null -> -1 no index eval; non-null -> element 5 at index 0.
            Assert.Equal(-1, InvokeStatic(fixture, "ArrayIndex", null, indexed));
            Assert.Equal(5, InvokeStatic(fixture, "ArrayIndex", new[] { 5, 6 }, indexed));
            Assert.Equal(1, indexedType.GetField("Calls")!.GetValue(indexed));

            // List: null -> -2; non-null -> element 8.
            Assert.Equal(-2, InvokeStatic(fixture, "ListIndex", null, indexed));
            var list = Activator.CreateInstance(listType)!;
            listType.GetMethod("Add")!.Invoke(list, new object[] { 7 });
            listType.GetMethod("Add")!.Invoke(list, new object[] { 8 });
            Assert.Equal(8, InvokeStatic(fixture, "ListIndex", list, indexed));

            // Custom indexer: null -> -3; non-null -> i+1000.
            Assert.Equal(-3, InvokeStatic(fixture, "CustomIndex", null, 4));
            Assert.Equal(1007, InvokeStatic(fixture, "CustomIndex", indexed, 7));
        }

        var (result, _) = PreprocessSoleFile(IndexSource);
        Assert.Equal(3, result.Statistics.NullConditional.ConditionalIndexSites);
        Assert.Equal(3, result.Statistics.NullConditional.SegmentTempsLifted);
    }

    private static object? InvokeStatic(Type type, string method, params object?[] args)
        => type.GetMethod(method)!.Invoke(null, args);

    private const string DelegateSource = """
        using System;

        namespace C35Fixture;

        public sealed class DelegateFixture
        {
            public int HandlerCalls;
            public void Handler(int x) { HandlerCalls += x; }
            public static int Doubler(int x) => x * 2;

            public Action<int>? Pub;
            public event Action<int>? Ev;
            public Func<int, int>? Fn;

            public void VoidDelegate(int x) { Pub?.Invoke(x); }
            public void VoidEventNull() { Ev?.Invoke(4); }
            public void VoidEventSubscribed(int x)
            {
                Ev += Handler;
                Ev?.Invoke(x);
            }
            public int ValueFunc(int input) => Fn?.Invoke(input) ?? -1;
        }
        """;

    [Fact]
    public void NC5_Invoke_Delegate_FieldEvent_SubscriberSnapshot_BehaveIdentically()
    {
        Assembly[] assemblies =
        {
            EmitAndLoad(BuildCompilation(DelegateSource)),
            LoweredAssembly(DelegateSource, out var lowered),
        };

        foreach (var assembly in assemblies)
        {
            var type = assembly.GetType("C35Fixture.DelegateFixture", throwOnError: true)!;
            var instance = Activator.CreateInstance(type)!;

            // Null delegate/event: zero throws, zero calls.
            type.GetMethod("VoidDelegate")!.Invoke(instance, new object[] { 7 });
            type.GetMethod("VoidEventNull")!.Invoke(instance, null);
            Assert.Equal(0, type.GetField("HandlerCalls")!.GetValue(instance));

            // Subscribed field-style event fires exactly once.
            type.GetMethod("VoidEventSubscribed")!.Invoke(instance, new object[] { 9 });
            Assert.Equal(9, type.GetField("HandlerCalls")!.GetValue(instance));

            // Delegate field snapshot: Pub=Handler then invoke.
            type.GetField("Pub")!.SetValue(instance,
                Delegate.CreateDelegate(typeof(Action<int>), instance,
                    type.GetMethod("Handler")!));
            type.GetMethod("VoidDelegate")!.Invoke(instance, new object[] { 5 });
            Assert.Equal(14, type.GetField("HandlerCalls")!.GetValue(instance));

            // Value-returning delegate: null -> -1; assigned -> Doubler.
            Assert.Equal(-1, type.GetMethod("ValueFunc")!.Invoke(instance, new object[] { 3 }));
            type.GetField("Fn")!.SetValue(instance,
                Delegate.CreateDelegate(typeof(Func<int, int>),
                    type.GetMethod("Doubler")!));
            Assert.Equal(8, type.GetMethod("ValueFunc")!.Invoke(instance, new object[] { 4 }));
        }

        var (result, _) = PreprocessSoleFile(DelegateSource);
        var s = result.Statistics.NullConditional;
        Assert.Equal(4, s.InvokeSites);
        Assert.Equal(2, s.EventSites);
        Assert.Equal(2, s.DelegateSites);
        Assert.Equal(3, s.VoidStatementSites);
    }

    private const string MultiSource = """
        namespace C35Fixture;

        public sealed class Leaf
        {
            public int Value;
            public int FireCalls;
            public void Fire(int x) { FireCalls++; }
            public override string ToString() => "L" + Value;
        }

        public sealed class MidNode
        {
            public Leaf? Leaf;
        }

        public sealed class RootNode
        {
            public MidNode? Mid;
        }

        public static class MultiFixture
        {
            public static int? ValueMulti(RootNode? r)
            {
                var x = r?.Mid?.Leaf?.Value;
                return x;
            }

            public static string? ValueCallMulti(RootNode? r)
            {
                var x = r?.Mid?.Leaf?.ToString();
                return x;
            }

            public static void VoidMulti(RootNode? r, int seed)
            {
                r?.Mid?.Leaf?.Fire(seed);
            }
        }
        """;

    /// <summary>Builds the 0..3 depth tree: null root / root only / +mid / +leaf(Value=9).</summary>
    private static object? BuildTree(Assembly assembly, int depth)
    {
        if (depth == 0)
            return null;

        var rootType = assembly.GetType("C35Fixture.RootNode", throwOnError: true)!;
        var midType = assembly.GetType("C35Fixture.MidNode", throwOnError: true)!;
        var leafType = assembly.GetType("C35Fixture.Leaf", throwOnError: true)!;

        var root = Activator.CreateInstance(rootType)!;
        if (depth == 1)
            return root;

        var mid = Activator.CreateInstance(midType)!;
        rootType.GetField("Mid")!.SetValue(root, mid);
        if (depth == 2)
            return root;

        var leaf = Activator.CreateInstance(leafType)!;
        leafType.GetField("Value")!.SetValue(leaf, 9);
        midType.GetField("Leaf")!.SetValue(mid, leaf);
        return root;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NC2_NC8_MultiSegment_NestedGuards_NullEveryLevel_BehaveIdentically(int depth)
    {
        Assembly[] assemblies =
        {
            EmitAndLoad(BuildCompilation(MultiSource)),
            LoweredAssembly(MultiSource, out var lowered),
        };

        foreach (var assembly in assemblies)
        {
            var fixture = assembly.GetType("C35Fixture.MultiFixture", throwOnError: true)!;
            var tree = BuildTree(assembly, depth);

            var valueResult = fixture.GetMethod("ValueMulti")!.Invoke(null, new[] { tree });
            Assert.Equal(depth == 3 ? (object)9 : null, valueResult);

            var callResult = fixture.GetMethod("ValueCallMulti")!.Invoke(null, new[] { tree });
            Assert.Equal(depth == 3 ? "L9" : null, callResult);

            fixture.GetMethod("VoidMulti")!.Invoke(null, new[] { tree, 1 });

            // Fire only when all three levels present; leaf identity is depth 3's leaf.
            if (depth == 3)
            {
                var leaf = assembly.GetType("C35Fixture.Leaf")!;
                var mid = ((dynamic?)tree)!.Mid;
                Assert.Equal(1, leaf.GetField("FireCalls")!.GetValue(mid.Leaf));
            }
        }
    }

    [Fact]
    public void NC2_MultiValue_Rewrite_IsIdempotent()
    {
        var (first, _) = PreprocessSoleFile(MultiSource);
        Assert.True(first.Changed);

        var secondCompilation = BuildCompilation(first.OutputCode, "c35-multi-idem");
        var secondModel = secondCompilation.GetSemanticModel(secondCompilation.SyntaxTrees[0]);
        var second = new ExpressionDesugarPreprocessor()
            .Preprocess(first.OutputCode, secondModel, C35Enabled);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void NC11_VoidCall_ArrowBody_CS2J0558_AtomicNoRewrite()
    {
        const string source = """
            namespace C35Fixture;

            public sealed class Target { public void M2() { } }

            public sealed class ArrowVoid
            {
                private Target? _t;
                public void Go() => _t?.M2();
            }
            """;

        var (result, _) = PreprocessSoleFile(source);

        Assert.Contains(
            result.Diagnostics, d => d.Code == NullConditionalDiagnosticCodes.CS2J0558);
        Assert.Contains(
            result.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void NC2_MultiValue_AnchorNotLocalOrReturn_CS2J0557_AtomicNoRewrite()
    {
        const string source = """
            namespace C35Fixture;

            public sealed class Leaf { public int Value; }
            public sealed class MidN { public Leaf? Leaf; }
            public sealed class RootN { public MidN? Mid; public int? Stored; }

            public static class BadAnchorFixture
            {
                public static void Go(RootN? r, RootN sink)
                {
                    sink.Stored = r?.Mid?.Leaf?.Value;
                }
            }
            """;

        var (result, _) = PreprocessSoleFile(source);

        Assert.Contains(
            result.Diagnostics, d => d.Code == NullConditionalDiagnosticCodes.CS2J0557);
        Assert.Contains(
            result.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }
}
