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
    public void M0_DirtyArrayFile_NotRewritten_WithSiteStatistics()
    {
        const string source = """
            public class C { void M() { int[] a = [1, 2, 3]; } }
            """;
        var preprocessor = new ExpressionDesugarPreprocessor();

        var result = preprocessor.Preprocess(source, BuildModel(source));

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(0, result.Statistics.FilesSkippedClean);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(0, result.Statistics.FilesTransformed);
        Assert.Equal(1, result.Statistics.CollectionExpr.CollectionSites);
        Assert.Equal(1, result.Statistics.CollectionExpr.ByTargetArray);
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
    public void M0_GuardSyntaxOnlyFile_IsDirtyNoRewrite_WithoutCollectionSites()
    {
        // static abstract interface member is CE-GUARD syntax (M3 inventory):
        // it makes the file dirty even with zero collection-expression sites.
        const string source = """
            public interface IGuard { static abstract void M(); }
            """;
        var preprocessor = new ExpressionDesugarPreprocessor();

        var result = preprocessor.Preprocess(source, BuildModel(source));

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(0, result.Statistics.CollectionExpr.CollectionSites);
    }

    [Fact]
    public void M0_TargetBucketAndShapeCounters_FollowSemanticClassification()
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

        var result = preprocessor.Preprocess(source, BuildModel(source));

        Assert.False(result.Changed);
        var stats = result.Statistics.CollectionExpr;
        Assert.Equal(9, stats.CollectionSites);
        Assert.Equal(1, stats.ByTargetUnknown);        // List<T> (M0 coarse bucket)
        Assert.Equal(1, stats.ByTargetInterface);      // IEnumerable<int>
        Assert.Equal(1, stats.ByTargetSpan);           // Span<byte>
        Assert.Equal(1, stats.ByTargetCollectionBuilder); // MyCollection
        // a, f, g plus both outer and inner sites of [[1]] are arrays.
        Assert.Equal(5, stats.ByTargetArray);
        Assert.Equal(1, stats.SpanArraySites);
        Assert.Equal(1, stats.BuilderSites);
        Assert.Equal(1, stats.EmptySites);             // []
        Assert.Equal(1, stats.NestedSites);            // inner [1] inside [[1]]
        Assert.Equal(1, stats.SpreadElements);         // .. src
    }
}
