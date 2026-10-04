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
}
