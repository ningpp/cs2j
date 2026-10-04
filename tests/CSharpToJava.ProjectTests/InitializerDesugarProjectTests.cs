using System.Reflection;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C24 project-level gate. Since wave-0 (2026-10-04) the CLI factories wire the
/// <see cref="ExpressionDesugarOptions.Initializers"/> subsection by default
/// (kill switch <c>--no-initializer-desugar</c>). This gate still drives the
/// double run directly through the Core <see cref="ExpressionDesugarPreprocessor"/>:
/// the fixture library is compiled and executed natively, then the
/// <c>Initializers*</c> files are C24-preprocessed and the same behavior
/// observations must hold on the preprocessed assembly.
/// The preprocessed files must contain no residual initializer syntax.
/// </summary>
public class InitializerDesugarProjectTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview);

    private static string LibDir => Path.Combine(
        AppContext.BaseDirectory, "SampleExpressionDesugarSolution", "SampleExpressionDesugarLib");

    private static readonly IReadOnlyList<MetadataReference> References = LoadReferences();

    private static readonly ExpressionDesugarOptions C24Enabled = new()
    {
        Initializers = new InitializerOptions(),
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

    private static string[] LibSources()
        => Directory.GetFiles(LibDir, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    private static CSharpCompilation BuildCompilation(
        IReadOnlyDictionary<string, string> sourceOverride)
    {
        var trees = LibSources()
            .Select(path => CSharpSyntaxTree.ParseText(
                sourceOverride.TryGetValue(path, out var text)
                    ? text
                    : File.ReadAllText(path),
                ParseOptions,
                path: path))
            .ToList();

        return CSharpCompilation.Create(
            "c24-project-fixture",
            trees,
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    private static Assembly EmitAndLoad(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(
            emit.Success,
            "fixture compilation failed to emit: " +
            string.Join("; ", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())));
        return Assembly.Load(stream.ToArray());
    }

    // ---- Behavior observations (identical calls on both assemblies) ----

    private static readonly (string Type, string Method, object?[] Args, string Expected)[] Observations =
    {
        ("InitializerDtoScenarios", "ConstructArgsPreserved", new object?[0], "42:0"),
        ("InitializerDtoScenarios", "MemberOrderAndSideEffects", new object?[0], "1,2,2"),
        ("InitializerDtoScenarios", "EmptyInitializer", new object?[0], "0"),
        ("CollectionScenarios", "CtorArgPreserved", new object?[0], "8:1,2"),
        ("CollectionScenarios", "MultiArg", new object?[0], "1-2;3-4"),
        ("CollectionScenarios", "Fluent", new object?[0], "1,2"),
        ("CollectionScenarios", "Params", new object?[0], "1,2,3"),
        ("CollectionScenarios", "Extension", new object?[0], "4,5"),
        ("CollectionScenarios", "DictionaryIndex", new object?[0], "ab:2"),
        ("NestedScenarios", "NestedCreate", new object?[0], "7"),
        ("NestedScenarios", "NestedMemberGetterCalledOnce", new object?[0], "5:1"),
        ("NestedScenarios", "ConditionalBranchSideEffects", new object?[] { true }, "x:1"),
        ("NestedScenarios", "ConditionalBranchSideEffects", new object?[] { false }, "null:0"),
        ("TargetTypedScenarios", "LocalInitializer", new object?[0], "2:12"),
        ("TargetTypedScenarios", "BareLocal", new object?[0], "9"),
        ("TargetTypedScenarios", "ReturnWithInitializer", new object?[0], "3,4"),
        ("TargetTypedScenarios", "NullableTarget", new object?[0], "1"),
    };

    private static IReadOnlyDictionary<string, string> Observe(Assembly assembly)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (type, method, args, _) in Observations)
        {
            var key = type + "." + method + "(" + string.Join(",", args) + ")";
            var target = assembly.GetType("SampleExpressionDesugarLib." + type, throwOnError: true)!;
            var info = target.GetMethod(
                method, BindingFlags.Public | BindingFlags.Static)!;
            string value;
            try
            {
                value = Normalize(info.Invoke(null, args));
            }
            catch (TargetInvocationException ex)
            {
                value = "EX:" + (ex.InnerException ?? ex).GetType().Name;
            }

            results[key] = value;
        }

        return results;
    }

    private static string Normalize(object? value) => value switch
    {
        null => "null",
        string text => text,
        System.Collections.IEnumerable items => string.Join(
            ",", items.Cast<object?>().Select(v => v?.ToString() ?? "null")),
        _ => value.ToString() ?? "null",
    };

    [Fact]
    public void Fixture_Initializers_NativeAndPreprocessed_BehaveIdentically_AndResidualFree()
    {
        Assert.True(Directory.Exists(LibDir), $"fixture lib not found: {LibDir}");

        // 1) Native compile + behavior.
        var native = BuildCompilation(new Dictionary<string, string>());
        Assert.Empty(native.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        var nativeObs = Observe(EmitAndLoad(native));

        // 2) C24-preprocess the Initializers* files through the Core preprocessor.
        var overrideSources = new Dictionary<string, string>();
        foreach (var path in LibSources())
        {
            if (!Path.GetFileName(path).StartsWith("Initializers", StringComparison.Ordinal))
                continue;

            var tree = native.SyntaxTrees.Single(t => t.FilePath == path);
            var model = native.GetSemanticModel(tree);
            var source = File.ReadAllText(path);
            var result = new ExpressionDesugarPreprocessor()
                .Preprocess(source, model, C24Enabled);

            Assert.DoesNotContain(
                result.Diagnostics, d => d.Severity == ExpressionDesugarSeverity.Error);
            Assert.DoesNotContain(
                result.Diagnostics, d => d.Code == InitializerDiagnosticCodes.CS2J0545);
            Assert.False(result.Statistics.Initializers.ResidualInitializerCount > 0);
            Assert.True(
                result.Changed,
                $"{Path.GetFileName(path)} should contain initializer sites to rewrite");
            Assert.True(result.Statistics.Initializers.PairedResidualNodes == 0);

            var outputRoot = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions)
                .GetCompilationUnitRoot();
            Assert.Empty(outputRoot.DescendantNodes()
                .Where(InitializerSyntaxFacts.IsResidualNode));

            overrideSources[path] = result.OutputCode;
        }

        Assert.NotEmpty(overrideSources);

        // 3) Preprocessed compile must be error-free, then behave identically.
        var preprocessed = BuildCompilation(overrideSources);
        Assert.Empty(preprocessed.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        var preprocessedObs = Observe(EmitAndLoad(preprocessed));

        foreach (var (type, method, args, expected) in Observations)
        {
            var key = type + "." + method + "(" + string.Join(",", args) + ")";
            Assert.Equal(expected, nativeObs[key]);
            Assert.Equal(nativeObs[key], preprocessedObs[key]);
        }
    }

    [Fact]
    public void Fixture_Initializers_PreprocessingIsIdempotent()
    {
        var native = BuildCompilation(new Dictionary<string, string>());
        var path = LibSources().Single(p =>
            Path.GetFileName(p) == "InitializersNested.cs");
        var model = native.GetSemanticModel(native.SyntaxTrees.Single(t => t.FilePath == path));

        var preprocessor = new ExpressionDesugarPreprocessor();
        var first = preprocessor.Preprocess(File.ReadAllText(path), model, C24Enabled);
        Assert.True(first.Changed);

        var secondTree = CSharpSyntaxTree.ParseText(first.OutputCode, ParseOptions, path: path);
        var secondModel = CSharpCompilation.Create(
                "c24-idem",
                new[] { secondTree },
                References,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable))
            .GetSemanticModel(secondTree);
        var second = preprocessor.Preprocess(first.OutputCode, secondModel, C24Enabled);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }
}
