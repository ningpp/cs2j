using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C36 collection-expression M0 probes against Roslyn 5.0
/// (Microsoft.CodeAnalysis.CSharp 5.0.x). These facts lock the runtime API
/// shapes that M1 lowering will depend on; conclusions are mirrored in
/// docs/superpowers/plans/2026-10-01-c36-collection-expr.md §6.
/// </summary>
public class CollectionExprDesugarUnitTests
{
    private const string CollectionExpressionOperationFullName =
        "Microsoft.CodeAnalysis.Operations.CollectionExpressionOperation";
    private const string LiteralOperationFullName =
        "Microsoft.CodeAnalysis.Operations.LiteralOperation";
    private const string ConversionOperationFullName =
        "Microsoft.CodeAnalysis.Operations.ConversionOperation";
    private const string SpreadOperationFullName =
        "Microsoft.CodeAnalysis.Operations.SpreadOperation";
    private const string ParameterReferenceOperationFullName =
        "Microsoft.CodeAnalysis.Operations.ParameterReferenceOperation";

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Span<>).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(System.Runtime.CompilerServices.CollectionBuilderAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(System.Runtime.CompilerServices.RuntimeFeature).Assembly.Location),
        // System.Collections.Immutable ships in the shared .NET 10 runtime;
        // the generic-builder factory test resolves ImmutableArray.Create<T>.
        MetadataReference.CreateFromFile(
            typeof(System.Collections.Immutable.ImmutableArray<>).Assembly.Location),
        // LINQ runtime + the facade used by runtime probes that call .Select/.Count.
        MetadataReference.CreateFromFile(
            typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(
            Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    };

    private static (CSharpCompilation compilation, SemanticModel model, List<CollectionExpressionSyntax> sites)
        CompileSites(string source, bool preview = true)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            preview ? new CSharpParseOptions(LanguageVersion.Preview) : null);
        var compilation = CSharpCompilation.Create("ce-probe",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetCompilationUnitRoot();
        var sites = root.DescendantNodes().OfType<CollectionExpressionSyntax>().ToList();
        return (compilation, model, sites);
    }

    // ------------------------------------------------------------------
    // Probe group 1: five target shapes + element/spread operation paths.
    // ------------------------------------------------------------------

    private const string FiveTargetsSource = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;

        [CollectionBuilder(typeof(MyBuilder), "Create")]
        public class MyCollection { }

        public static class MyBuilder
        {
            public static MyCollection Create(ReadOnlySpan<int> items) => new MyCollection();
        }

        public static class P
        {
            public static void Go(int[] src)
            {
                int[] a = [1, 2, 3];
                List<int> b = [1, 2];
                IEnumerable<int> c = [1];
                Span<byte> d = [1, 2];
                MyCollection e = [1, 2];
                int[] f = [.. src, 2];
            }
        }
        """;

    [Fact]
    public void Probe_FiveTargets_ConvertedTypeForms_AreLocked()
    {
        var (compilation, model, sites) = CompileSites(FiveTargetsSource);

        Assert.Equal(6, sites.Count);
        Assert.Equal("int[]", model.GetTypeInfo(sites[0]).ConvertedType?.ToDisplayString());
        Assert.Equal("System.Collections.Generic.List<int>",
            model.GetTypeInfo(sites[1]).ConvertedType?.ToDisplayString());
        Assert.Equal("System.Collections.Generic.IEnumerable<int>",
            model.GetTypeInfo(sites[2]).ConvertedType?.ToDisplayString());
        Assert.Equal("System.Span<byte>",
            model.GetTypeInfo(sites[3]).ConvertedType?.ToDisplayString());
        Assert.Equal("MyCollection",
            model.GetTypeInfo(sites[4]).ConvertedType?.ToDisplayString());
        Assert.Equal("int[]", model.GetTypeInfo(sites[5]).ConvertedType?.ToDisplayString());

        // Every site binds to the same public concrete collection operation.
        foreach (var site in sites)
        {
            var op = model.GetOperation(site);
            Assert.Equal(CollectionExpressionOperationFullName, op?.GetType().FullName);
            var collection = Assert.IsAssignableFrom<ICollectionExpressionOperation>(op);
            Assert.NotEmpty(collection.Elements);
        }

        // Identity-compatible elements are bare literals (no conversion wrapper).
        var arrayElements = ((ICollectionExpressionOperation)model.GetOperation(sites[0])!).Elements;
        Assert.All(arrayElements, element =>
        {
            Assert.Equal(LiteralOperationFullName, element.GetType().FullName);
            Assert.Equal(OperationKind.Literal, element.Kind);
            Assert.Equal(SpecialType.System_Int32, element.Type!.SpecialType);
        });

        // Narrowing int -> byte is an explicit-shape IConversionOperation wrapper.
        var spanElements = ((ICollectionExpressionOperation)model.GetOperation(sites[3])!).Elements;
        Assert.All(spanElements, element =>
        {
            var conversion = Assert.IsAssignableFrom<IConversionOperation>(element);
            Assert.Equal(ConversionOperationFullName, element.GetType().FullName);
            Assert.Equal(SpecialType.System_Byte, conversion.Type!.SpecialType);
            var inner = Assert.IsAssignableFrom<ILiteralOperation>(conversion.Operand);
            Assert.Equal(SpecialType.System_Int32, inner.Type!.SpecialType);
        });

        // Spread element: ISpreadOperation has no Type; Operand is the raw
        // source expression (int[] parameter) without an implicit conversion.
        var spreadSite = (ICollectionExpressionOperation)model.GetOperation(sites[5])!;
        Assert.Equal(2, spreadSite.Elements.Length);
        var spread = Assert.IsAssignableFrom<ISpreadOperation>(spreadSite.Elements[0]);
        Assert.Equal(SpreadOperationFullName, spreadSite.Elements[0].GetType().FullName);
        Assert.Equal(OperationKind.Spread, spread.Kind);
        Assert.Null(spread.Type);
        Assert.Equal(ParameterReferenceOperationFullName, spread.Operand!.GetType().FullName);
        Assert.Equal("int[]", spread.Operand.Type?.ToDisplayString());
        Assert.IsAssignableFrom<ILiteralOperation>(spreadSite.Elements[1]);

        // Only benign diagnostics (CS9188: builder class declares no element
        // type in this synthetic fixture; CS8019: unnecessary using).
        Assert.Subset(
            new HashSet<string> { "CS9188", "CS8019" },
            new HashSet<string>(compilation.GetDiagnostics().Select(d => d.Id)));
    }

    [Fact]
    public void Probe_SpreadOperand_ExposesSourceNaturalTypeWithoutConversion()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            public static class S
            {
                public static void Go(int[] arr, Span<int> span, ReadOnlySpan<int> rospan,
                    List<int> list, IEnumerable<int> en, string s)
                {
                    int[] a = [.. arr];
                    int[] b = [.. list];
                    int[] c = [.. en];
                    Span<int> d = [.. span];
                    Span<int> e = [.. rospan];
                    char[] f = [.. s];
                }
            }
            """;
        var (compilation, model, sites) = CompileSites(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(6, sites.Count);

        string[] expectedOperandTypes =
        {
            "int[]",
            "System.Collections.Generic.List<int>",
            "System.Collections.Generic.IEnumerable<int>",
            "System.Span<int>",
            "System.ReadOnlySpan<int>",
            "string",
        };

        for (var i = 0; i < sites.Count; i++)
        {
            var collection = (ICollectionExpressionOperation)model.GetOperation(sites[i])!;
            var spread = Assert.IsAssignableFrom<ISpreadOperation>(collection.Elements.Single());
            Assert.Equal(OperationKind.ParameterReference, spread.Operand!.Kind);
            Assert.Equal(expectedOperandTypes[i], spread.Operand.Type?.ToDisplayString());
        }
    }

    // ------------------------------------------------------------------
    // Probe group 2: [CollectionBuilder] attribute + bound factory location.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_CollectionBuilder_AttributeArguments_AndNoBoundInvocationInOperationTree()
    {
        var (compilation, model, sites) = CompileSites(FiveTargetsSource);
        var builderSite = sites[4];

        var builderType = compilation.GetTypeByMetadataName("MyCollection");
        Assert.NotNull(builderType);
        var attr = builderType!.GetAttributes()
            .Single(a => a.AttributeClass?.ToDisplayString()
                == "System.Runtime.CompilerServices.CollectionBuilderAttribute");
        Assert.Equal(2, attr.ConstructorArguments.Length);

        var typeArg = attr.ConstructorArguments[0];
        Assert.Equal(TypedConstantKind.Type, typeArg.Kind);
        Assert.Equal("System.Type", typeArg.Type?.ToDisplayString());
        var builderSymbol = Assert.IsAssignableFrom<INamedTypeSymbol>(typeArg.Value);
        Assert.Equal("MyBuilder", builderSymbol.Name);

        var methodArg = attr.ConstructorArguments[1];
        Assert.Equal(TypedConstantKind.Primitive, methodArg.Kind);
        Assert.Equal(SpecialType.System_String, methodArg.Type!.SpecialType);
        Assert.Equal("Create", methodArg.Value);

        // The factory call is NOT lowered into the IOperation tree at this
        // stage: M1 must resolve Create from the attribute metadata, not from
        // a bound invocation node.
        var op = model.GetOperation(builderSite);
        Assert.Equal(CollectionExpressionOperationFullName, op?.GetType().FullName);
        Assert.Empty(op!.Descendants().OfType<IInvocationOperation>());
    }

    // ------------------------------------------------------------------
    // Probe group 3: spec violation diagnostic codes.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_SpecViolationDiagnostics_Are_CS9175_And_CS9176()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            public static class R
            {
                public static void Go()
                {
                    var x = [];
                    Expression<Func<int[]>> e = () => [1];
                }
            }
            """;
        var (compilation, _, sites) = CompileSites(source);
        var ids = compilation.GetDiagnostics().Select(d => d.Id).ToHashSet();

        Assert.Contains("CS9176", ids); // collection expression has no target type
        Assert.Contains("CS9175", ids); // expression trees cannot contain collection expressions
        Assert.Equal(2, sites.Count);   // both still parse as collection expressions
    }

    [Fact]
    public void Probe_CS9176_Site_Still_Parses_AsCollectionExpression()
    {
        // CS9176 is a semantic error only: the node shape remains parseable.
        const string source = """
            public static class R2 { public static void Go() { var x = []; } }
            """;
        var (_, _, sites) = CompileSites(source);
        Assert.Single(sites);
    }

    // ------------------------------------------------------------------
    // Probe group 4: interface target ConvertedType table.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_InterfaceTargetConvertedTypeTable_IsLocked()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            public static class Q
            {
                public static void Go()
                {
                    IEnumerable<int> a = [1];
                    IList<int> b = [1];
                    ICollection<int> c = [1];
                    IReadOnlyList<int> d = [1];
                    IReadOnlyCollection<int> e = [1];
                    IDictionary<int, string> f = [new KeyValuePair<int, string>(1, "x")];
                    IEnumerable g = [1];
                }
            }
            """;
        var (compilation, model, sites) = CompileSites(source);
        Assert.Equal(7, sites.Count);

        string[] expected =
        {
            "System.Collections.Generic.IEnumerable<int>",
            "System.Collections.Generic.IList<int>",
            "System.Collections.Generic.ICollection<int>",
            "System.Collections.Generic.IReadOnlyList<int>",
            "System.Collections.Generic.IReadOnlyCollection<int>",
            "System.Collections.Generic.IDictionary<int, string>",
            "System.Collections.IEnumerable",
        };
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], model.GetTypeInfo(sites[i]).ConvertedType?.ToDisplayString());

        // IDictionary<K,V> and non-generic IEnumerable are not constructible:
        // both produce CS9174 (M1 must route these to skip diagnostics).
        var notConstructible = compilation.GetDiagnostics()
            .Where(d => d.Id == "CS9174")
            .ToList();
        Assert.Equal(2, notConstructible.Count);
    }

    // ------------------------------------------------------------------
    // Probe: default (non-Preview) language version also binds collections.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_DefaultLanguageVersion_BindsCollectionExpression()
    {
        const string source = """
            public static class T { public static void Go() { int[] a = [1, 2, 3]; } }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var parseOptions = Assert.IsType<CSharpParseOptions>(tree.Options);
        Assert.True(parseOptions.LanguageVersion >= LanguageVersion.CSharp12);

        var compilation = CSharpCompilation.Create("ce-default",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var site = tree.GetCompilationUnitRoot()
            .DescendantNodes().OfType<CollectionExpressionSyntax>().Single();
        Assert.Equal("int[]", model.GetTypeInfo(site).ConvertedType?.ToDisplayString());
        Assert.Equal(CollectionExpressionOperationFullName,
            model.GetOperation(site)?.GetType().FullName);
    }

    // ------------------------------------------------------------------
    // M0 behavior tests: the preprocessor is analysis-only and never rewrites.
    // ------------------------------------------------------------------

    private static SemanticModel BuildModel(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create("ce-behavior",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetSemanticModel(tree);
    }

    [Fact]
    public void M0_CleanFile_Unchanged_AndCountedAsSkippedClean()
    {
        const string source = "public class C { void M() { int x = 1; } }\n";
        var preprocessor = new ExpressionDesugarPreprocessor();

        var result = preprocessor.Preprocess(source, BuildModel(source));

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.FilesScanned);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(0, result.Statistics.CollectionExpr.CollectionSites);
    }

    [Fact]
    public void M1_DirtyArrayFile_IsRewritten_ZeroTemp_AndTransformedCounted()
    {
        const string source = """
            public class C { void M() { int[] a = [1, 2, 3]; } }
            """;
        var preprocessor = new ExpressionDesugarPreprocessor();

        var result = preprocessor.Preprocess(source, BuildModel(source));

        Assert.True(result.Changed);
        Assert.Contains("new int[] { 1, 2, 3 }", Normalize(result.OutputCode));
        Assert.Equal(0, result.Statistics.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Equal(1, result.Statistics.CollectionExpr.CollectionSites);
        Assert.Equal(1, result.Statistics.CollectionExpr.ByTargetArray);
        AssertOutputCompiles(result.OutputCode);

        // Idempotence: the output has no collection-expression syntax.
        var second = preprocessor.Preprocess(result.OutputCode, BuildModel(result.OutputCode));
        Assert.False(second.Changed);
        Assert.Equal(result.OutputCode, second.OutputCode);
    }

    [Fact]
    public void M0_DirtyFile_WithoutSemanticModel_Warns_CS2J0571_AndDoesNotCrash()
    {
        const string source = """
            public class C { void M() { int[] a = [1, 2, 3]; } }
            """;
        var preprocessor = new ExpressionDesugarPreprocessor();

        var result = preprocessor.Preprocess(source, semanticModel: null);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExpressionDesugarSeverity.Warning, diagnostic.Severity);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0571, diagnostic.Code);
    }

    [Fact]
    public void M3_GuardSyntaxOnlyFile_IsDirtyNoRewrite_AndEmits0585ErrorDiagnostic()
    {
        // static abstract interface member is CE-GUARD syntax: it makes the
        // file dirty even with zero collection-expression sites, and M3 emits
        // the symbol-verified CS2J0585 declaration diagnostic. Strict default
        // makes it an Error -> atomic Changed=false, but the diagnostic is
        // still surfaced (dirty-no-rewrite path preserves diagnostics).
        const string source = """
            public interface IGuard { static abstract void M(); }
            """;
        var preprocessor = new ExpressionDesugarPreprocessor();

        var result = preprocessor.Preprocess(source, BuildModel(source));

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(0, result.Statistics.CollectionExpr.CollectionSites);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0585, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Contains("IGuard.M()", diagnostic.Message);
        Assert.Contains("independent workstream", diagnostic.Message);
        Assert.Equal(
            1,
            result.Statistics.CollectionExpr.GuardStaticAbstractDeclarations);
        Assert.Equal(
            0,
            result.Statistics.CollectionExpr.GuardStaticAbstractCallSites);
    }

    [Fact]
    public void M2_TargetBucketAndShapeCounters_FollowSemanticClassification_AllSitesRewrite()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            [CollectionBuilder(typeof(B), "Create")]
            public class MyCollection { }
            public static class B { public static MyCollection Create(ReadOnlySpan<int> x) => new(); }

            public class C
            {
                void M(int[] src)
                {
                    int[] a = [1];
                    List<int> b = [1];
                    IEnumerable<int> c = [1];
                    Span<byte> d = [1];
                    MyCollection e = [1];
                    int[] f = [.. src];
                    int[] g = [];
                    int[][] h = [[1]];
                }
            }
            """;
        var preprocessor = new ExpressionDesugarPreprocessor();

        // M2: interface sites construct List<T> (0574) and builder sites emit
        // the resolved factory, so a legal file rewrites end to end; the Span
        // site carries the CS2J0578 stack->heap Info.
        var result = preprocessor.Preprocess(source, BuildModel(source));

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.DoesNotContain(
            CollectionExprDiagnosticCodes.CS2J0580,
            result.Diagnostics.Select(d => d.Code));
        Assert.DoesNotContain(
            CollectionExprDiagnosticCodes.CS2J0581,
            result.Diagnostics.Select(d => d.Code));
        Assert.Contains(
            CollectionExprDiagnosticCodes.CS2J0578,
            result.Diagnostics.Select(d => d.Code));
        Assert.All(
            result.Diagnostics.Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0578),
            d => Assert.Equal(ExpressionDesugarSeverity.Info, d.Severity));

        var stats = result.Statistics.CollectionExpr;
        Assert.Equal(9, stats.CollectionSites);
        Assert.Equal(1, stats.ByTargetMutableConcrete); // List<T>
        Assert.Equal(1, stats.ByTargetInterface);       // IEnumerable<int>
        Assert.Equal(1, stats.ByTargetSpan);            // Span<byte>
        Assert.Equal(1, stats.ByTargetCollectionBuilder); // MyCollection
        Assert.Equal(0, stats.ByTargetUnknown);
        // a, f, g plus both outer and inner sites of [[1]] are arrays.
        Assert.Equal(5, stats.ByTargetArray);
        Assert.Equal(1, stats.SpanArraySites);
        Assert.Equal(1, stats.BuilderSites);
        Assert.Equal(1, stats.BuilderFactoryEmitted);
        Assert.Equal(1, stats.InterfaceConstructSites);
        Assert.Equal(1, stats.EmptySites);              // []
        Assert.Equal(1, stats.NestedSites);             // inner [1] inside [[1]]
        Assert.Equal(1, stats.SpreadElements);          // .. src
        Assert.False(stats.SkippedByReason
            .ContainsKey(CollectionExprDiagnosticCodes.CS2J0580));
        Assert.False(stats.SkippedByReason
            .ContainsKey(CollectionExprDiagnosticCodes.CS2J0581));
    }

    [Fact]
    public void M2_NonStrict_InterfaceAndBuilderSitesRewrite_WithSiblingArray()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            [CollectionBuilder(typeof(B), "Create")]
            public class MyCollection : IEnumerable<int>
            {
                public IEnumerator<int> GetEnumerator() => throw new NotImplementedException();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public static class B { public static MyCollection Create(ReadOnlySpan<int> x) => new(); }

            public class C
            {
                void M()
                {
                    int[] a = [1];
                    IEnumerable<int> c = [2];
                    MyCollection e = [3];
                }
            }
            """;
        var options = new ExpressionDesugarOptions
        {
            CollectionExpr = new CollectionExprOptions { Strict = false },
        };
        var preprocessor = new ExpressionDesugarPreprocessor();

        var result = preprocessor.Preprocess(source, BuildModel(source), options);

        Assert.True(result.Changed); // all three sites rewrite under M2
        Assert.Equal(1, result.Statistics.FilesTransformed);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("new int[] { 1 }", normalized);
        // Interface -> List<int> collection initializer; builder -> B.Create.
        Assert.Contains("List<int>() { 2 }", normalized);
        Assert.Contains(".Create(", normalized);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.Statistics.CollectionExpr.SkippedByReason
            .ContainsKey(CollectionExprDiagnosticCodes.CS2J0580));
        Assert.False(result.Statistics.CollectionExpr.SkippedByReason
            .ContainsKey(CollectionExprDiagnosticCodes.CS2J0581));
        Assert.Equal(1, result.Statistics.CollectionExpr.InterfaceConstructSites);
        Assert.Equal(1, result.Statistics.CollectionExpr.BuilderFactoryEmitted);
        AssertOutputCompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // M1 shared helpers: output recompile gate + runtime behavior probes.
    // ------------------------------------------------------------------

    private static string Normalize(string code)
        => Regex.Replace(code, @"\s+", " ");

    private static (CSharpCompilation compilation, SemanticModel model) BuildNamedCompilation(
        string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ce-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    /// <summary>Recompiles preprocessed output with the same refs; asserts zero Error diagnostics.</summary>
    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(
            output, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ce-out-" + Guid.NewGuid().ToString("N"),
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
        string source, ExpressionDesugarOptions? options = null)
    {
        var (_, model) = BuildNamedCompilation(source);
        return new ExpressionDesugarPreprocessor()
            .Preprocess(source, model, options);
    }

    /// <summary>
    /// Behavior probe: compiles <paramref name="source"/> to a net10 DLL,
    /// loads it in the default ALC and invokes a static parameterless method.
    /// Unique assembly names keep native and preprocessed builds side by side.
    /// </summary>
    private static string RunStaticMethod(string source, string typeName, string methodName = "Run")
    {
        var (compilation, _) = BuildNamedCompilation(source);
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

    /// <summary>
    /// Runs the same parameterless probe method on the native source and on
    /// the preprocessed output and asserts both return the expected value.
    /// </summary>
    private static void AssertNativeAndPreprocessedProbe(
        string source, string typeName, string expected, string methodName = "Run")
    {
        Assert.Equal(expected, RunStaticMethod(source, typeName, methodName));
        var result = Preprocess(source);
        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(expected, RunStaticMethod(result.OutputCode, typeName, methodName));
    }

    // ------------------------------------------------------------------
    // M1 matrix 1: zero-temp arrays (CE-1/2).
    // ------------------------------------------------------------------

    [Fact]
    public void M1_ArrayTargets_EmitZeroTemp_IntStringCustomStruct()
    {
        const string source = """
            public struct Point { public int X; public int Y; }
            public static class C
            {
                public static void M()
                {
                    int[] a = [1, 2, 3];
                    string[] b = ["x", "y"];
                    Point[] c = [new Point { X = 1, Y = 2 }];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Matches(
            @"new (int|global::System\.Int32)\[\] \{ 1, 2, 3 \}", normalized);
        Assert.Contains(@"""x""", normalized);
        Assert.True(normalized.Contains("Point { X = 1, Y = 2 }"), normalized);
        Assert.Equal(3, result.Statistics.CollectionExpr.ByTargetArray);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_ArrayTarget_AtParamsArgument_ZeroTemp()
    {
        const string source = """
            public static class C
            {
                static void Print(params int[] x) { }
                static void M() { Print([1, 2]); }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        Assert.Matches(
            @"Print\(new (int|global::System\.Int32)\[\] \{ 1, 2 \}\)",
            Normalize(result.OutputCode));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_ArrayTarget_FieldInitializer_ZeroTempPasses()
    {
        const string source = """
            public class C
            {
                private static readonly int[] Field = [1, 2];
                public int[] InstanceField = [3];
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Matches(@"\[\] \{ 1, 2 \}", normalized);
        Assert.Matches(@"\[\] \{ 3 \}", normalized);
        Assert.Equal(0, result.Statistics.CollectionExpr.StatementLiftedSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_JaggedNestedArray_BothLevelsRewrite()
    {
        const string source = """
            public static class C
            {
                public static void M()
                {
                    int[][] j = [[1], [2]];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.CollectionExpr.CollectionSites);
        Assert.Equal(3, result.Statistics.CollectionExpr.ByTargetArray);
        Assert.Equal(2, result.Statistics.CollectionExpr.NestedSites);
        Assert.DoesNotContain("=[", Normalize(result.OutputCode).Replace(" ", ""));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_EmptyArray_EmitsEmptyInitializer()
    {
        const string source = """
            public static class C { public static void M() { int[] a = []; } }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        Assert.Matches(
            @"new (int|global::System\.Int32)\[\] \{ \}",
            Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.CollectionExpr.EmptySites);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("0", RunStaticMethod("""
            public static class P { public static string Run() { int[] a = []; return a.Length.ToString(); } }
            """, "P"));
    }

    [Fact]
    public void M1_MultidimensionalArrayTarget_Strict0581_AtomicPreserve()
    {
        const string source = """
            public static class C
            {
                public static void M()
                {
                    int[,] m = [1, 2];
                    int[] a = [9];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0581, diagnostic.Code);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
    }

    // ------------------------------------------------------------------
    // M1 matrix 2: mutable concrete targets (CE-3) + runtime mutability.
    // ------------------------------------------------------------------

    [Fact]
    public void M1_ListTarget_ZeroTemp_MutableAtRuntime()
    {
        const string source = """
            using System.Collections.Generic;
            public static class Probe
            {
                public static string Run()
                {
                    List<int> l = [1, 2];
                    l.Add(3);
                    return l.Count + "," + l[2];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Matches(
            @"new (global::System\.Collections\.Generic\.)?List<int>\(\) \{ 1, 2 \}",
            normalized);
        Assert.DoesNotContain("java", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.ByTargetMutableConcrete);
        Assert.Equal(1, result.Statistics.CollectionExpr.MutableConstructSites);
        AssertOutputCompiles(result.OutputCode);

        Assert.Equal("3,3", RunStaticMethod(source, "Probe"));
        Assert.Equal("3,3", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M1_HashSetAndCustomAddCollection_ZeroTemp()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            public class Bag : IEnumerable<int>
            {
                private readonly List<int> _items = new();
                public void Add(int x) => _items.Add(x);
                public int Count => _items.Count;
                public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public static class C
            {
                public static void M()
                {
                    HashSet<int> h = [1, 2];
                    Bag b = [1, 2];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Matches(
            @"new (global::System\.Collections\.Generic\.)?HashSet<int>\(\) \{ 1, 2 \}",
            normalized);
        Assert.True(normalized.Contains("Bag() { 1, 2 }"), normalized + " ||| " +
            string.Join(";", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(2, result.Statistics.CollectionExpr.ByTargetMutableConcrete);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_EmptyConcreteTarget_EmitsBareConstruction()
    {
        const string source = """
            using System.Collections.Generic;
            public static class C { public static void M() { List<int> l = []; } }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Matches(
            @"new (global::System\.Collections\.Generic\.)?List<int>\(\);",
            normalized);
        Assert.DoesNotContain("List<int>() { }", normalized);
        AssertOutputCompiles(result.OutputCode);
    }

    // ------------------------------------------------------------------
    // M1 matrix 3/5/6/7: spread expansion (CE-5/6) + stats buckets.
    // ------------------------------------------------------------------

    [Fact]
    public void M1_SpreadArray_AllPositions_AddRange_ContentOrder()
    {
        const string source = """
            public static class C
            {
                public static void M(int[] a, int[] b)
                {
                    int[] x = [.. a];
                    int[] y = [.. a, 9];
                    int[] z = [9, .. a];
                    int[] w = [.. a, .. b];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("AddRange(a)", normalized);
        Assert.Contains("AddRange(b)", normalized);
        Assert.Contains(".Add(9)", normalized);
        Assert.Contains(".ToArray()", normalized);
        var stats = result.Statistics.CollectionExpr;
        Assert.Equal(5, stats.SpreadElements);
        Assert.Equal(5, stats.SpreadAddRange);
        Assert.Equal(4, stats.StatementLiftedSites);
        Assert.Equal(0, stats.SpreadEnumerableForeach);
        Assert.Equal(0, stats.SpreadSpanForeach);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_SpreadString_ForcesForeachAdd()
    {
        const string source = """
            public static class Probe
            {
                public static string Run()
                {
                    string s = "ab";
                    char[] c = [.. s, 'c'];
                    return new string(c);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.DoesNotContain("AddRange(s)", normalized);
        Assert.Contains("foreach", normalized);
        Assert.Contains(".Add(", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.SpreadEnumerableForeach);
        Assert.Equal(0, result.Statistics.CollectionExpr.SpreadAddRange);
        Assert.Equal("abc", RunStaticMethod(source, "Probe"));
        Assert.Equal("abc", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M1_SpreadSpan_ForeachAdd_RuntimeContent()
    {
        const string source = """
            using System;
            public static class Probe
            {
                public static string Run()
                {
                    Span<int> s = new[] { 7, 8 };
                    int[] r = [.. s];
                    return string.Join(",", r);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("foreach", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.SpreadSpanForeach);
        Assert.Equal(0, result.Statistics.CollectionExpr.SpreadAddRange);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("7,8", RunStaticMethod(source, "Probe"));
        Assert.Equal("7,8", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M1_SpreadConcreteTargetWithoutAddRange_ForeachAdd()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            public class MyBag : IEnumerable<int>
            {
                private readonly List<int> _items = new();
                public void Add(int x) => _items.Add(x);
                public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public static class Probe
            {
                static IEnumerable<int> Yielded()
                {
                    yield return 5;
                    yield return 6;
                }

                public static string Run()
                {
                    MyBag bag = [.. Yielded()];
                    return string.Join(",", bag);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("foreach", normalized);
        Assert.DoesNotContain("AddRange", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.SpreadEnumerableForeach);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("5,6", RunStaticMethod(source, "Probe"));
        Assert.Equal("5,6", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M1_SpreadNonEnumerable_Strict0576_AtomicPreserve()
    {
        const string source = """
            public sealed class NotEnumerable { }
            public static class C
            {
                public static void M(NotEnumerable n)
                {
                    int[] x = [.. n];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0576, diagnostic.Code);
        Assert.Equal(1, result.Statistics.CollectionExpr
            .SkippedByReason[CollectionExprDiagnosticCodes.CS2J0576]);
    }

    // ------------------------------------------------------------------
    // M1 matrix 10: single evaluation / source order behavior probes.
    // ------------------------------------------------------------------

    [Fact]
    public void M1_RuntimeProbe_ElementOrder_SpreadEvaluatedOnce_EnumeratedOnce()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            public sealed class ProbeEnumerable : IEnumerable<int>
            {
                private readonly List<string> _log;
                public ProbeEnumerable(List<string> log) { _log = log; }
                public IEnumerator<int> GetEnumerator()
                {
                    _log.Add("enumerate");
                    yield return 1;
                    yield return 2;
                }
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public static class Probe
            {
                static int Next(List<string> log) { log.Add("next"); return 9; }
                static IEnumerable<int> Source(List<string> log)
                {
                    log.Add("source");
                    return new ProbeEnumerable(log);
                }

                public static string Run()
                {
                    var log = new List<string>();
                    int[] x = [Next(log), .. Source(log), Next(log)];
                    return string.Join(",", x) + "|" + string.Join(",", log);
                }
            }
            """;

        const string expected = "9,1,2,9|next,source,enumerate,next";
        var result = Preprocess(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.CollectionExpr.SpreadAddRange);
        AssertOutputCompiles(result.OutputCode);
        AssertNativeAndPreprocessedProbe(source, "Probe", expected);

        // Explicit MoveNext accounting: exactly one enumeration pass.
        var trace = RunStaticMethod(result.OutputCode, "Probe").Split('|')[1].Split(',');
        Assert.Equal(1, trace.Count(t => t == "enumerate"));
        Assert.Equal(1, trace.Count(t => t == "source"));
        Assert.Equal(2, trace.Count(t => t == "next"));
    }

    [Fact]
    public void M1_RuntimeProbe_MultiSourceContentAndOrder()
    {
        const string source = """
            using System.Collections.Generic;
            public static class Probe
            {
                static IEnumerable<int> Yielded()
                {
                    yield return 5;
                    yield return 6;
                }

                public static string Run()
                {
                    int[] a = [1, 2];
                    var b = new List<int> { 3, 4 };
                    int[] r = [.. a, 10, .. b, .. Yielded()];
                    return string.Join(",", r);
                }
            }
            """;

        AssertNativeAndPreprocessedProbe(source, "Probe", "1,2,10,3,4,5,6");
    }

    // ------------------------------------------------------------------
    // M1 matrix 11: statement-liftable contexts + 0583 gates.
    // ------------------------------------------------------------------

    [Fact]
    public void M1_Spread_SafeStatementContexts_AllRewrite()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            public class C
            {
                static string Describe(int[] x) => x.Length.ToString();

                int[] Local(int[] a)
                {
                    int[] x = [.. a];
                    return x;
                }

                int[] ReturnIt(int[] a) => [.. a];

                IEnumerable<int[]> YieldIt(int[] a)
                {
                    yield return [.. a];
                }

                string ThrowIt(int[] a)
                {
                    // Collection site is in argument position inside the
                    // throw statement (direct cast/index receiver positions
                    // are not collection-expression target contexts).
                    throw new InvalidOperationException(Describe([.. a]));
                }

                void Assign(int[] a)
                {
                    int[] x = new[] { 0 };
                    x = [.. a];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(
            result.Changed,
            Normalize(result.OutputCode) + " ||| " +
            string.Join(";", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.DoesNotContain(
            CollectionExprDiagnosticCodes.CS2J0583,
            result.Diagnostics.Select(d => d.Code));
        Assert.Equal(5, result.Statistics.CollectionExpr.StatementLiftedSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_Spread_TernaryBranches_0583_ArgumentStillLifts_NonStrict()
    {
        const string source = """
            public class C
            {
                static void Consume(string tag, params int[] x) { }

                int[] Tern(bool b, int[] a, int[] c) => b ? [.. a] : [.. c];

                void Arg(int[] a) { Consume("x", [.. a]); }
            }
            """;

        // M2 (E): prefix-hoisting a spread-bearing '?:' branch would evaluate
        // the not-taken branch, so both branches stay on 0583; the argument
        // site remains a safe statement-lift position. Non-strict keeps the
        // ternary sites (Warnings) while still rewriting the argument site.
        var options = new ExpressionDesugarOptions
        {
            CollectionExpr = new CollectionExprOptions { Strict = false },
        };
        var result = Preprocess(source, options);

        Assert.True(result.Changed); // argument site rewrites
        var ternaryDiagnostics = result.Diagnostics
            .Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0583)
            .ToList();
        Assert.Equal(2, ternaryDiagnostics.Count);
        Assert.All(ternaryDiagnostics,
            d => Assert.Equal(ExpressionDesugarSeverity.Warning, d.Severity));
        Assert.Contains("conditional", ternaryDiagnostics[0].Message);
        Assert.Equal(1, result.Statistics.CollectionExpr.StatementLiftedSites);
        Assert.Equal(2, result.Statistics.CollectionExpr
            .SkippedByReason[CollectionExprDiagnosticCodes.CS2J0583]);

        var residualSites = CSharpSyntaxTree.ParseText(
                result.OutputCode,
                new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot()
            .DescendantNodes().OfType<CollectionExpressionSyntax>().Count();
        Assert.Equal(2, residualSites); // the preserved ternary branches only
        AssertOutputCompiles(result.OutputCode);

        // Strict mode: both branch sites are Errors and abort the whole file.
        var strict = Preprocess(source);
        Assert.False(strict.Changed);
        Assert.Equal(source, strict.OutputCode);
        Assert.All(strict.Diagnostics, d =>
        {
            Assert.Equal(ExpressionDesugarSeverity.Error, d.Severity);
            Assert.Equal(CollectionExprDiagnosticCodes.CS2J0583, d.Code);
        });
        Assert.Equal(2, strict.Diagnostics.Count);
    }

    [Fact]
    public void M2_Spread_NullCoalescingRightOperand_0583()
    {
        const string source = """
            public class C
            {
                int[] M(int[]? a, int[] b) => a ?? [.. b];
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0583, diagnostic.Code);
        Assert.Contains("right operand of '??'", diagnostic.Message);
    }

    [Fact]
    public void M1_Spread_BlockBodiedLambda_LocallyLifted()
    {
        const string source = """
            using System;

            public class C
            {
                void M(int[] a)
                {
                    Func<int[]> f = () => { return [.. a]; };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        Assert.Empty(result.Diagnostics);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_Spread_SideEffectBeforeSite_0583_AtomicStrict()
    {
        const string source = """
            using System;

            public class C
            {
                static int Foo() => 1;
                static void Consume(int x, params int[] rest) { }

                void M(int[] a)
                {
                    Consume(Foo(), [.. a]);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0583, diagnostic.Code);
    }

    [Fact]
    public void M1_Spread_TernaryWithCallBefore_0583()
    {
        const string source = """
            using System;

            public class C
            {
                static int Foo() => 1;

                void M(int[] a, int[] b)
                {
                    int[] r = Foo() > 0 ? [.. a] : [.. b];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.Equal(ExpressionDesugarSeverity.Error, d.Severity);
            Assert.Equal(CollectionExprDiagnosticCodes.CS2J0583, d.Code);
        });
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact]
    public void M1_Spread_FieldInitializer_0583()
    {
        const string source = """
            public class C
            {
                static int[] Source() => new[] { 1 };
                int[] Field = [.. Source()];
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0583, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void M1_Spread_ExpressionBodiedLambda_0583()
    {
        const string source = """
            using System;

            public class C
            {
                void M(int[] a)
                {
                    Func<int[]> f = () => [.. a];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0583, diagnostic.Code);
    }

    // ------------------------------------------------------------------
    // M1 diagnostic specials: 0571 (no target), 0580 (builder message).
    // ------------------------------------------------------------------

    [Fact]
    public void M1_CS9176NoTarget_Strict0571_AtomicPreserve()
    {
        const string source = """
            public static class R { public static void Go() { var x = []; } }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0571, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void M2_BuilderUnresolved_0580MessageNamesBuilderFactoryAndReason()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            [CollectionBuilder(typeof(B), "Create")]
            public class MyCollection : IEnumerable<int>
            {
                public IEnumerator<int> GetEnumerator() => throw new NotImplementedException();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            // No Create(ReadOnlySpan<int>)/Create(int[]) factory: the symbol
            // resolver finds zero candidates (baseline CS9187 shape).
            public static class B { public static MyCollection Create(int x) => new(); }

            public class C
            {
                void M()
                {
                    MyCollection e = [1, 2];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0580, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Contains("B", diagnostic.Message);
        Assert.Contains("Create", diagnostic.Message);
        Assert.Contains("no accessible static method", diagnostic.Message);
        var stats = result.Statistics.CollectionExpr;
        Assert.Equal(1, stats.BuilderSites);
        Assert.Equal(0, stats.BuilderFactoryEmitted);
        Assert.Equal(1, stats.SkippedByReason[CollectionExprDiagnosticCodes.CS2J0580]);
    }

    // ------------------------------------------------------------------
    // M1 matrix 15: zero false positives + idempotence.
    // ------------------------------------------------------------------

    [Fact]
    public void M1_NormalArrayInitializer_IsCleanAndBytePreserved()
    {
        const string source = """
            public class C
            {
                void M()
                {
                    int[] a = new int[] { 1 };
                    int[][] j = new int[][] { new[] { 1 } };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.CollectionExpr.CollectionSites);
    }

    [Fact]
    public void M1_SpreadOutput_SecondRunByteIdentical()
    {
        const string source = """
            public static class C
            {
                public static void M(int[] a, int[] b)
                {
                    int[] x = [.. a, 1, .. b];
                }
            }
            """;

        var first = Preprocess(source);
        Assert.True(first.Changed);
        AssertOutputCompiles(first.OutputCode);

        var (_, secondModel) = BuildNamedCompilation(first.OutputCode);
        var second = new ExpressionDesugarPreprocessor()
            .Preprocess(first.OutputCode, secondModel);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    // ==================================================================
    // M2 CE-4: interface targets -> List<T> concrete construction (0574).
    // ==================================================================

    private const string AllFiveInterfacesSource = """
        using System.Collections.Generic;
        public static class C
        {
            public static void M()
            {
                IEnumerable<int> a = [1];
                ICollection<int> b = [2];
                IList<int> c = [3];
                IReadOnlyCollection<int> d = [4];
                IReadOnlyList<int> e = [5];
            }
        }
        """;

    [Fact]
    public void M2_InterfaceTargets_AllFive_EmitZeroTempList_AndCount0574()
    {
        var result = Preprocess(AllFiveInterfacesSource);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var stats = result.Statistics.CollectionExpr;
        Assert.Equal(5, stats.ByTargetInterface);
        Assert.Equal(5, stats.InterfaceConstructSites);
        Assert.Equal(0, stats.MutableConstructSites); // interfaces are not the T3 bucket
        Assert.False(stats.SkippedByReason.ContainsKey(
            CollectionExprDiagnosticCodes.CS2J0581));

        var normalized = Normalize(result.OutputCode);
        Assert.Equal(5, Regex.Matches(
            normalized,
            @"new global::System\.Collections\.Generic\.List<(int|global::System\.Int32)>\(\) \{ \d \}").Count);
        Assert.DoesNotContain("=[", normalized.Replace(" ", ""));
    }

    [Fact]
    public void M2_IListTarget_ZeroTempList_MutableAtRuntime_DoubleRun()
    {
        const string source = """
            using System.Collections.Generic;
            public static class Probe
            {
                public static string Run()
                {
                    IList<int> e = [1, 2];
                    e.Add(3);
                    return e.Count + "," + e[2];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(1, result.Statistics.CollectionExpr.InterfaceConstructSites);
        Assert.Equal("3,3", RunStaticMethod(source, "Probe"));
        Assert.Equal("3,3", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_InterfaceTarget_WithSpread_ListTempStatement_DoubleRun()
    {
        const string source = """
            using System.Collections.Generic;
            public static class Probe
            {
                public static string Run()
                {
                    int[] a = [1, 2];
                    IEnumerable<int> e = [.. a, 4];
                    return string.Join(",", e);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        var normalized = Normalize(result.OutputCode);
        // List<int> temp + AddRange; the interface local takes the temp
        // directly (no ToArray — List<int> converts to IEnumerable<int>).
        Assert.Contains(
            "new global::System.Collections.Generic.List<int>()", normalized);
        Assert.Contains("AddRange(a)", normalized);
        Assert.DoesNotContain(".ToArray()", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.InterfaceConstructSites);
        Assert.Equal(1, result.Statistics.CollectionExpr.StatementLiftedSites);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("1,2,4", RunStaticMethod(source, "Probe"));
        Assert.Equal("1,2,4", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_IDictionaryAndNongenericEnumerable_0581_MessageNamesCS9174()
    {
        const string source = """
            using System.Collections;
            using System.Collections.Generic;
            public static class C
            {
                public static void Go()
                {
                    IDictionary<int, string> f = [new KeyValuePair<int, string>(1, "x")];
                    IEnumerable g = [1];
                }
            }
            """;

        // Baseline CS9174 sites are unreachable from compilable input; the
        // defensive rule keeps them on 0581 and names the baseline error.
        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.Equal(ExpressionDesugarSeverity.Error, d.Severity);
            Assert.Equal(CollectionExprDiagnosticCodes.CS2J0581, d.Code);
            Assert.Contains("CS9174", d.Message);
        });
        Assert.Equal(2,
            result.Statistics.CollectionExpr.SkippedByReason[
                CollectionExprDiagnosticCodes.CS2J0581]);
        Assert.Equal(0, result.Statistics.CollectionExpr.InterfaceConstructSites);
    }

    [Fact]
    public void M2_InterfaceTarget_Empty_ZeroTempList_EverywhereIncludingField()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            public class Holder { public IEnumerable<int> Values = []; }
            public static class Probe
            {
                public static string Run()
                {
                    IEnumerable<int> local = [];
                    return new Holder().Values.Count() + "," + local.Count();
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(2, result.Statistics.CollectionExpr.EmptySites);
        Assert.DoesNotContain("List<int>() { }", Normalize(result.OutputCode));
        Assert.Equal("0,0", RunStaticMethod(source, "Probe"));
        Assert.Equal("0,0", RunStaticMethod(result.OutputCode, "Probe"));
    }

    // ==================================================================
    // M2 CE-8: deterministic nested temp/loop numbering + mixed dispatch.
    // ==================================================================

    [Fact]
    public void M2_NestedStatementSites_SiblingsSourceOrder_InnerBeforeOuter()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            public static class Probe
            {
                public static string Run()
                {
                    var a = new List<int> { 1 };
                    var b = new List<int> { 2 };
                    var c = new List<List<int>> { new() { 9 } };
                    List<List<int>> nested = [.. c, [.. a], [.. b]];
                    return string.Join("|", nested.Select(l => string.Join(",", l)));
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);

        // Two inner sibling sites (source order) before the enclosing site.
        var firstInner = normalized.IndexOf("var __ed_0 =", StringComparison.Ordinal);
        var secondInner = normalized.IndexOf("var __ed_1 =", StringComparison.Ordinal);
        var outer = normalized.IndexOf("var __ed_2 =", StringComparison.Ordinal);
        Assert.True(firstInner >= 0 && secondInner > firstInner && outer > secondInner,
            normalized);

        var stats = result.Statistics.CollectionExpr;
        Assert.Equal(3, stats.StatementLiftedSites);
        Assert.Equal(2, stats.NestedSites); // two inner sites
        Assert.Equal(3, stats.TempsLifted);
        Assert.Equal("9|1|2", RunStaticMethod(source, "Probe"));
        Assert.Equal("9|1|2", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_NestedSpreadAtBothLevels_InnerDeclaredBeforeOuter_DoubleRun()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            public static class Probe
            {
                public static string Run()
                {
                    var lists = new List<List<int>> { new() { 10 }, new() { 20 } };
                    var inner = new List<int> { 1, 2 };
                    List<List<int>> outer = [.. lists, [.. inner]];
                    return string.Join(";", outer.Select(l => string.Join(",", l)));
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        var innerDecl = normalized.IndexOf("var __ed_0 =", StringComparison.Ordinal);
        var outerDecl = normalized.IndexOf("var __ed_1 =", StringComparison.Ordinal);
        Assert.True(innerDecl >= 0 && outerDecl > innerDecl, normalized);
        // The outer list receives the inner temp as a plain element.
        Assert.Contains(".Add(__ed_0)", normalized);
        Assert.Equal("10;20;1,2", RunStaticMethod(source, "Probe"));
        Assert.Equal("10;20;1,2", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_MixedNesting_ZeroTempForms_CollectionOfArray_ArrayOfCollection_InterfaceInner()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            public static class Probe
            {
                public static string Run()
                {
                    List<int[]> cols = [[1, 2], [3]];
                    List<int>[] arrs = [new List<int> { 4 }, [5, 6]];
                    List<IEnumerable<int>> ifaces = [[7, 8]];
                    return cols.Count + "," + cols[0].Length + "|"
                        + arrs.Length + "," + arrs[1].Count + "|"
                        + ifaces[0].Count();
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        // Zero-temp forms all around: no statement temps.
        Assert.DoesNotContain("__ed_", normalized);
        // Inner interface site dispatches independently to List<int>.
        Assert.Contains(
            "new global::System.Collections.Generic.List<int>() { 7, 8 }",
            normalized);
        Assert.Equal("2,2|2,2|2", RunStaticMethod(source, "Probe"));
        Assert.Equal("2,2|2,2|2", RunStaticMethod(result.OutputCode, "Probe"));
    }

    // ==================================================================
    // M2 CE-9: Span targets -> heap array + CS2J0578 Info; ref struct 0582.
    // ==================================================================

    [Fact]
    public void M2_SpanTargets_EmitHeapArray_Report0578Info_AndCount()
    {
        const string source = """
            using System;
            public static class C
            {
                public static void M()
                {
                    Span<byte> s = [1, 2];
                    ReadOnlySpan<int> r = [3];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var infos = result.Diagnostics
            .Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0578)
            .ToList();
        Assert.Equal(2, infos.Count);
        Assert.All(infos, d => Assert.Equal(ExpressionDesugarSeverity.Info, d.Severity));
        Assert.Contains("heap array", infos[0].Message);
        Assert.Equal(2, result.Statistics.CollectionExpr.SpanArraySites);
        Assert.Contains("new byte[] { 1, 2 }", Normalize(result.OutputCode));
    }

    [Fact]
    public void M2_SpanTarget_IndexEquality_DoubleRun()
    {
        const string source = """
            using System;
            public static class Probe
            {
                public static string Run()
                {
                    Span<int> s = [1, 2, 3];
                    ReadOnlySpan<int> r = [4, 5];
                    return s[0] + "," + s[2] + "," + r[1];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(2, result.Diagnostics
            .Count(d => d.Code == CollectionExprDiagnosticCodes.CS2J0578));
        Assert.Equal(2, result.Statistics.CollectionExpr.SpanArraySites);
        Assert.Equal("1,3,5", RunStaticMethod(source, "Probe"));
        Assert.Equal("1,3,5", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_SpanRefStructElement_0582_StrictError_NonStrictWarning()
    {
        const string source = """
            using System;

            public readonly ref struct Tiny
            {
                public readonly int V;
                public Tiny(int v) { V = v; }
            }

            public static class C
            {
                public static void M()
                {
                    Span<Tiny> s = [new Tiny(1)];
                }
            }
            """;

        // Strict: Error and the file is atomically preserved.
        var strict = Preprocess(source);
        Assert.False(strict.Changed);
        Assert.Equal(source, strict.OutputCode);
        var error = Assert.Single(strict.Diagnostics);
        Assert.Equal(ExpressionDesugarSeverity.Error, error.Severity);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0582, error.Code);
        Assert.Contains("Tiny", error.Message);

        // Non-strict: Warning and the site is preserved (residual-gate paired).
        var nonStrict = Preprocess(source, new ExpressionDesugarOptions
        {
            CollectionExpr = new CollectionExprOptions { Strict = false },
        });
        Assert.False(nonStrict.Changed);
        var warning = Assert.Single(nonStrict.Diagnostics);
        Assert.Equal(ExpressionDesugarSeverity.Warning, warning.Severity);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0582, warning.Code);
        Assert.Equal(0, nonStrict.Statistics.CollectionExpr.ResidualCollectionCount);
    }

    // ==================================================================
    // M2 CE-10: CollectionBuilder generic factory emission.
    // ==================================================================

    private const string CustomBuilderSourcePrefix = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;

        [CollectionBuilder(typeof(B), "Create")]
        public sealed class MyBuilt : IEnumerable<int>
        {
            private readonly int[] _items;
            public MyBuilt(int[] items) { _items = items; }
            public int Count => _items.Length;
            public int this[int i] => _items[i];
            public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)_items).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
        }

        public static class B
        {
            public static MyBuilt Create(ReadOnlySpan<int> items) => new MyBuilt(items.ToArray());
        }

        """;

    [Fact]
    public void M2_BuilderFactory_NoSpread_EmitsGlobalFactoryCall_DoubleRun()
    {
        var source = CustomBuilderSourcePrefix + """
            public static class Probe
            {
                public static string Run()
                {
                    MyBuilt x = [1, 2, 3];
                    return x.Count + ":" + string.Join(",", x);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("global::B.Create(", normalized);
        Assert.Contains("new int[] { 1, 2, 3 }", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.BuilderFactoryEmitted);
        Assert.Equal(0, result.Statistics.CollectionExpr.StatementLiftedSites);
        Assert.Equal("3:1,2,3", RunStaticMethod(source, "Probe"));
        Assert.Equal("3:1,2,3", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_BuilderFactory_Empty_EmitsEmptyArrayArgument_DoubleRun()
    {
        var source = CustomBuilderSourcePrefix + """
            public static class Probe
            {
                public static string Run()
                {
                    MyBuilt empty = [];
                    return empty.Count.ToString();
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("global::B.Create(new int[] { })", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.EmptySites);
        Assert.Equal("0", RunStaticMethod(source, "Probe"));
        Assert.Equal("0", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_BuilderFactory_WithSpread_ListTempThenToArray_DoubleRun()
    {
        var source = CustomBuilderSourcePrefix + """
            public static class Probe
            {
                public static string Run()
                {
                    int[] src = [1, 2];
                    MyBuilt y = [.. src, 7];
                    return string.Join(",", y);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        // List<int> temp accumulates, then the factory wraps ToArray().
        Assert.Contains("new global::System.Collections.Generic.List<int>()", normalized);
        Assert.Contains("global::B.Create(__ed_", normalized);
        Assert.Contains(".ToArray())", normalized);
        Assert.Equal(1, result.Statistics.CollectionExpr.BuilderFactoryEmitted);
        Assert.Equal(1, result.Statistics.CollectionExpr.StatementLiftedSites);
        Assert.Equal("1,2,7", RunStaticMethod(source, "Probe"));
        Assert.Equal("1,2,7", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_BuilderFactory_KeyValuePairElement_DoubleRun()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            [CollectionBuilder(typeof(DictB), "Create")]
            public sealed class MyDict : IEnumerable<KeyValuePair<string, int>>
            {
                private readonly KeyValuePair<string, int>[] _items;
                public MyDict(KeyValuePair<string, int>[] items) { _items = items; }
                public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
                    => ((IEnumerable<KeyValuePair<string, int>>)_items).GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
            }

            public static class DictB
            {
                public static MyDict Create(ReadOnlySpan<KeyValuePair<string, int>> items)
                    => new MyDict(items.ToArray());
            }

            public static class Probe
            {
                public static string Run()
                {
                    MyDict d = [new KeyValuePair<string, int>("a", 1)];
                    foreach (var pair in d) return pair.Key + pair.Value;
                    return "none";
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.Contains(
            "global::System.Collections.Generic.KeyValuePair",
            Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.CollectionExpr.BuilderFactoryEmitted);
        Assert.Equal("a1", RunStaticMethod(source, "Probe"));
        Assert.Equal("a1", RunStaticMethod(result.OutputCode, "Probe"));
    }

    [Fact]
    public void M2_BuilderFactory_AmbiguousCandidates_0580()
    {
        const string source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            [CollectionBuilder(typeof(B), "Create")]
            public sealed class MyBuilt : IEnumerable<int>
            {
                public IEnumerator<int> GetEnumerator() => throw new NotImplementedException();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public static class B
            {
                public static MyBuilt Create(ReadOnlySpan<int> items) => new MyBuilt();
                public static MyBuilt Create<T>(ReadOnlySpan<T> items) => new MyBuilt();
            }

            public static class C
            {
                public static void M()
                {
                    MyBuilt x = [1, 2];
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CollectionExprDiagnosticCodes.CS2J0580, diagnostic.Code);
        Assert.Equal(ExpressionDesugarSeverity.Error, diagnostic.Severity);
        Assert.Contains("ambiguous", diagnostic.Message);
        Assert.Equal(1,
            result.Statistics.CollectionExpr.SkippedByReason[
                CollectionExprDiagnosticCodes.CS2J0580]);
        Assert.Equal(0, result.Statistics.CollectionExpr.BuilderFactoryEmitted);
    }

    [Fact]
    public void M2_BuilderFactory_GenericFactory_ImmutableArray_DoubleRun()
    {
        const string source = """
            using System.Collections.Immutable;
            public static class Probe
            {
                public static string Run()
                {
                    ImmutableArray<int> a = [1, 2, 3];
                    ImmutableArray<int> b = [];
                    return string.Join(",", a) + "|" + b.Length;
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains(
            "global::System.Collections.Immutable.ImmutableArray.Create<int>(",
            normalized);
        Assert.Equal(2, result.Statistics.CollectionExpr.BuilderFactoryEmitted);
        Assert.Equal("1,2,3|0", RunStaticMethod(source, "Probe"));
        Assert.Equal("1,2,3|0", RunStaticMethod(result.OutputCode, "Probe"));
    }

    // ==================================================================
    // M2 F: empty-collection matrix + empty-spread.
    // ==================================================================

    [Fact]
    public void M2_EmptyMatrix_ArrayListInterfaceSpan_ZeroLengthOrCount_DoubleRun()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Holder
            {
                public IEnumerable<int> InterfaceField = [];
                public static ReadOnlySpan<int> SpanProperty => [];
            }

            public static class Probe
            {
                public static string Run()
                {
                    int[] a = [];
                    List<int> b = [];
                    IEnumerable<int> c = [];
                    Span<int> d = [];
                    int[] empty = [];
                    int[] spreadOnly = [.. empty];
                    var holder = new Holder();
                    return string.Join(",",
                        a.Length, b.Count, c.Count(), d.Length,
                        spreadOnly.Length, holder.InterfaceField.Count(),
                        Holder.SpanProperty.Length);
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(7, result.Statistics.CollectionExpr.EmptySites);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("new int[] { }", normalized);
        Assert.Contains("new global::System.Collections.Generic.List<int>()",
            normalized);
        Assert.Equal("0,0,0,0,0,0,0", RunStaticMethod(source, "Probe"));
        Assert.Equal("0,0,0,0,0,0,0", RunStaticMethod(result.OutputCode, "Probe"));
    }

    // ==================================================================
    // M3 CE-GUARD: 0585/0586/0587/0588 semantic inventory.
    // ==================================================================

    private const string StaticAbstractKindsSource = """
        using System;

        public interface IGuard
        {
            static virtual int VirtualProperty => 1;
            static abstract int AbstractProperty { get; set; }
            static abstract event Action Happened;
            static abstract IGuard operator +(IGuard left, IGuard right);
        }
        """;

    [Fact]
    public void M3_0585_StaticAbstractKinds_Declarations_StrictError_SymbolVerified()
    {
        var result = Preprocess(StaticAbstractKindsSource);

        Assert.False(result.Changed);
        Assert.Equal(StaticAbstractKindsSource, result.OutputCode);

        var diagnostics = result.Diagnostics
            .Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0585)
            .ToList();
        Assert.Equal(4, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(ExpressionDesugarSeverity.Error, d.Severity);
            Assert.Contains("IGuard", d.Message);
            Assert.Contains("independent workstream", d.Message);
        });

        // Member kinds named in the messages.
        var joined = string.Join("|", diagnostics.Select(d => d.Message));
        Assert.Contains("property", joined);
        Assert.Contains("event", joined);
        Assert.Contains("operator", joined);

        var stats = result.Statistics.CollectionExpr;
        Assert.Equal(4, stats.GuardStaticAbstractDeclarations);
        Assert.Equal(0, stats.GuardStaticAbstractCallSites);
    }

    [Fact]
    public void M3_0585_StaticAbstractKinds_Declarations_NonStrictWarning()
    {
        var result = Preprocess(StaticAbstractKindsSource, new ExpressionDesugarOptions
        {
            CollectionExpr = new CollectionExprOptions { Strict = false },
        });

        Assert.False(result.Changed); // guard-only file, nothing to rewrite
        var diagnostics = result.Diagnostics
            .Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0585)
            .ToList();
        Assert.Equal(4, diagnostics.Count);
        Assert.All(diagnostics, d =>
            Assert.Equal(ExpressionDesugarSeverity.Warning, d.Severity));
    }

    [Fact]
    public void M3_0585_GenericMathCallSites_AreWarnings_NotErrors_SymbolVerified()
    {
        // Uses the BCL generic-math interface (no static-abstract declaration
        // in this file): a + b resolves to IAdditionOperators<...>.op_Addition,
        // an interface static abstract operator -> Warning inventory. Call-site
        // inventory runs in files the cheap scan already marked dirty, so a
        // sibling collection site carries the semantic pass; the Warning never
        // blocks the sibling rewrite.
        const string source = """
            using System.Numerics;

            public static class MathProbe
            {
                public static T Add<T>(T a, T b) where T : INumber<T> => a + b;
                public static int[] Values() => [1, 2];
            }
            """;

        var strict = Preprocess(source);

        Assert.True(strict.Changed); // sibling collection site still rewrites
        Assert.DoesNotContain(
            strict.Diagnostics,
            d => d.Severity == ExpressionDesugarSeverity.Error);
        var call = Assert.Single(
            strict.Diagnostics,
            d => d.Code == CollectionExprDiagnosticCodes.CS2J0585);
        Assert.Equal(ExpressionDesugarSeverity.Warning, call.Severity);
        Assert.Contains("operator", call.Message);
        Assert.Contains("IAdditionOperators", call.Message);
        var stats = strict.Statistics.CollectionExpr;
        Assert.Equal(0, stats.GuardStaticAbstractDeclarations);
        Assert.Equal(1, stats.GuardStaticAbstractCallSites);
        AssertOutputCompiles(strict.OutputCode);
    }

    [Fact]
    public void M3_0585_GuardDisabled_SkipsInventoryEntirely()
    {
        var result = Preprocess(StaticAbstractKindsSource, new ExpressionDesugarOptions
        {
            CollectionExpr = new CollectionExprOptions
            {
                Strict = true,
                EnableGuard = false,
            },
        });

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == CollectionExprDiagnosticCodes.CS2J0585);
        Assert.Equal(
            0,
            result.Statistics.CollectionExpr.GuardStaticAbstractDeclarations);
    }

    [Fact]
    public void M3_0586_ScopedParameterIsInfo_ScopedLocalIsWarning()
    {
        const string source = """
            using System;

            public class C
            {
                void M(scoped ReadOnlySpan<int> p)
                {
                    scoped ReadOnlySpan<int> local = p;
                }
            }
            """;

        var strict = Preprocess(source);

        Assert.False(strict.Changed);
        var byCode = strict.Diagnostics
            .Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0586)
            .ToList();
        Assert.Equal(2, byCode.Count);
        Assert.Contains(
            byCode,
            d => d.Severity == ExpressionDesugarSeverity.Info
                 && d.Message.Contains("parameter"));
        Assert.Contains(
            byCode,
            d => d.Severity == ExpressionDesugarSeverity.Warning
                 && d.Message.Contains("local/other-form"));

        var stats = strict.Statistics.CollectionExpr;
        Assert.Equal(1, stats.GuardScopedParameterSites);
        Assert.Equal(1, stats.GuardScopedLocalSites);
    }

    [Fact]
    public void M3_0587_GenericAttribute_DeclarationError_ApplicationWarning()
    {
        const string source = """
            using System;

            [GenericAttr<int>()]
            public class Target { }

            public class GenericAttr<T> : Attribute { }
            """;

        // Strict: declaration is Error (atomic preserve); application Warning.
        var strict = Preprocess(source);

        Assert.False(strict.Changed);
        Assert.Equal(source, strict.OutputCode);
        var strictAttrs = strict.Diagnostics
            .Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0587)
            .ToList();
        Assert.Equal(2, strictAttrs.Count);
        Assert.Contains(
            strictAttrs,
            d => d.Severity == ExpressionDesugarSeverity.Error
                 && d.Message.Contains("GenericAttr<T>"));
        Assert.Contains(
            strictAttrs,
            d => d.Severity == ExpressionDesugarSeverity.Warning
                 && d.Message.Contains("GenericAttr<int>"));

        var stats = strict.Statistics.CollectionExpr;
        Assert.Equal(1, stats.GuardGenericAttributeDeclarations);
        Assert.Equal(1, stats.GuardGenericAttributeApplications);

        // Non-strict: both downgrade to Warning.
        var nonStrict = Preprocess(source, new ExpressionDesugarOptions
        {
            CollectionExpr = new CollectionExprOptions { Strict = false },
        });
        Assert.All(
            nonStrict.Diagnostics.Where(d =>
                d.Code == CollectionExprDiagnosticCodes.CS2J0587),
            d => Assert.Equal(ExpressionDesugarSeverity.Warning, d.Severity));
    }

    [Fact]
    public void M3_0587_NonGenericAttribute_IsNotInventoried()
    {
        const string source = """
            using System;

            [Plain]
            public class Target { }

            public class PlainAttribute : Attribute { }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == CollectionExprDiagnosticCodes.CS2J0587);
        Assert.Equal(
            0,
            result.Statistics.CollectionExpr.GuardGenericAttributeDeclarations);
        Assert.Equal(
            0,
            result.Statistics.CollectionExpr.GuardGenericAttributeApplications);
    }

    [Fact]
    public void M3_0588_CollectionInsideExpressionTree_InfoSkip_PreservedAndPaired()
    {
        // Baseline CS9175 territory (normally uncompilable): the defensive
        // 0588 check fires before target resolution and pairs the residual.
        const string source = """
            using System;
            using System.Linq.Expressions;

            public class C
            {
                public static Expression<Func<int[]>> Make() => () => [1];
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var diagnostic = Assert.Single(result.Diagnostics
            .Where(d => d.Code == CollectionExprDiagnosticCodes.CS2J0588));
        Assert.Equal(ExpressionDesugarSeverity.Info, diagnostic.Severity);
        Assert.Equal(
            1,
            result.Statistics.CollectionExpr
                .SkippedByReason[CollectionExprDiagnosticCodes.CS2J0588]);
        Assert.Equal(1, result.Statistics.CollectionExpr.CollectionSites);
    }

    // ==================================================================
    // M3 CE-12 whole-tree residual gate (white-box algorithm tests).
    // ==================================================================

    [Fact]
    public void M3_TreeGate_CountPairedNodes_SkipSitesPlusPreservedSpreads()
    {
        var stats = new CollectionExprStatistics();
        stats.AddSkip(CollectionExprDiagnosticCodes.CS2J0571);
        stats.AddSkip(CollectionExprDiagnosticCodes.CS2J0583);
        stats.AddSkip(CollectionExprDiagnosticCodes.CS2J0588);
        stats.SkippedSpreadElements = 2;

        // 3 paired sites + 2 carried spread nodes = 5 paired residuals.
        Assert.Equal(5, CollectionExprTreeGate.CountPairedNodes(stats));
        Assert.Equal(
            7,
            CollectionExprTreeGate.PairedSkipCodes.Length);
        Assert.Contains(
            CollectionExprDiagnosticCodes.CS2J0588,
            CollectionExprTreeGate.PairedSkipCodes);
    }

    [Fact]
    public void M3_TreeGate_Reconcile_BalancedAndUnbalanced_PerFile()
    {
        var residual = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["a.cs"] = 3,
            ["b.cs"] = 1,
        };
        var paired = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["a.cs"] = 3,
            ["b.cs"] = 1,
        };

        var balanced = CollectionExprTreeGate.Reconcile(residual, paired);
        Assert.True(balanced.Balanced);
        Assert.Equal(4, balanced.ResidualNodes);
        Assert.Equal(4, balanced.PairedNodes);
        Assert.Empty(balanced.UnpairedFiles);

        var broken = CollectionExprTreeGate.Reconcile(
            new Dictionary<string, int>(StringComparer.Ordinal) { ["a.cs"] = 3 },
            new Dictionary<string, int>(StringComparer.Ordinal) { ["a.cs"] = 2 });
        Assert.False(broken.Balanced);
        var file = Assert.Single(broken.UnpairedFiles);
        Assert.Equal("a.cs", file.File);

        var message = string.Format(
            CollectionExprDiagnostics.TreeResidualMessage,
            broken.ResidualNodes,
            broken.PairedNodes,
            string.Join(", ", broken.UnpairedFiles
                .Select(f => $"{f.File} ({f.ResidualNodes} residual/{f.PairedNodes} paired)")));
        Assert.Contains("3", message);
        Assert.Contains("2", message);
        Assert.Contains("a.cs", message);
    }

    [Fact]
    public void M3_0584_InventorySummaryMessage_CarriesAllBuckets()
    {
        var message = string.Format(
            CollectionExprDiagnostics.InventorySummaryMessage,
            1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Contains("new-syntax inventory", message);
        Assert.Contains("static-abstract 1 declaration(s)/2 call site(s)", message);
        Assert.Contains("generic-attribute 3 declaration(s)/4 application(s)", message);
        Assert.Contains("scoped 5 parameter site(s)/6 local/other site(s)", message);
        Assert.Contains("7 residual node(s) = 8 paired", message);
    }

    // ==================================================================
    // M3 E09 linkage observation (E09 = UnsignedTypeSyntaxRewriter byte
    // narrowing). E09 wraps constants 128..255 ONLY when the literal's parent
    // is InitializerExpressionSyntax/CollectionExpressionSyntax/
    // ExpressionElementSyntax and ConvertedType is byte. These tests pin the
    // parent-node shapes C36 emits; they do not modify E09.
    // ==================================================================

    [Fact]
    public void M3_E09Observation_ZeroTempByteArray_HighLiteralsSitInArrayInitializer()
    {
        const string source = """
            public static class Probe
            {
                public static string Run()
                {
                    byte[] b = [128, 200, 127];
                    return b[0] + "," + b[1] + "," + b[2];
                }
            }
            """;

        var result = Preprocess(source);
        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("128,200,127", RunStaticMethod(source, "Probe"));
        Assert.Equal("128,200,127", RunStaticMethod(result.OutputCode, "Probe"));

        // Observation: the zero-temp form is new byte[]{ 128, 200, 127 }; each
        // high literal's parent is the array InitializerExpressionSyntax, so
        // E09 IsByteArrayInitializerElement matches and wraps 128/200 in
        // (byte) — no C36 change required on this path.
        var outputTree = CSharpSyntaxTree.ParseText(
            result.OutputCode, new CSharpParseOptions(LanguageVersion.Preview));
        var literals = outputTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l is { RawKind: (int)SyntaxKind.NumericLiteralExpression })
            .Where(l => l.Token.ValueText is "128" or "200" or "127")
            .ToList();
        Assert.Equal(3, literals.Count);
        Assert.All(literals, l => Assert.IsType<InitializerExpressionSyntax>(l.Parent));
        Assert.All(literals, l =>
            Assert.Equal(SyntaxKind.ArrayInitializerExpression, l.Parent!.Kind()));
    }

    [Fact]
    public void M3_E09Observation_StatementSequence_HighLiteralSitsInAddArgument_NotInitializer()
    {
        const string source = """
            public static class Probe
            {
                public static string Run()
                {
                    byte[] prefix = new byte[] { 1 };
                    byte[] b = [.. prefix, 200];
                    return string.Join(",", b);
                }
            }
            """;

        var result = Preprocess(source);
        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal("1,200", RunStaticMethod(source, "Probe"));
        Assert.Equal("1,200", RunStaticMethod(result.OutputCode, "Probe"));

        var outputRoot = CSharpSyntaxTree.ParseText(
                result.OutputCode, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot();

        // Observation 1: statement-lifted Add(200) — the high literal's parent
        // is an ArgumentSyntax. E09's initializer-only predicate does NOT
        // match argument position, so E09 leaves it unwrapped today.
        var highLiteral = outputRoot.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Single(l => l.Token.ValueText == "200");
        Assert.IsType<ArgumentSyntax>(highLiteral.Parent);
        var addCall = Assert.IsType<InvocationExpressionSyntax>(
            highLiteral.Parent!.Parent!.Parent);
        Assert.Contains(".Add(", addCall.ToString());

        // Observation 2: the .ToArray() reference carries no element
        // literals at all (elements were statement-added to the temp).
        var toArray = outputRoot.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression
                .ToString()
                .EndsWith(".ToArray", StringComparison.Ordinal));
        Assert.DoesNotContain(
            toArray.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            l => l.Token.ValueText is "128" or "200");
    }
}
