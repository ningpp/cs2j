using System.Text.RegularExpressions;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C24 object/collection-initializer M0 probes and behavior lock against
/// Roslyn 5.0.0 (Microsoft.CodeAnalysis.CSharp 5.0.x). The facts pin the
/// runtime IOperation shapes that M1/M2 lowering will depend on; conclusions
/// are mirrored in docs/superpowers/plans/2026-09-29-c24-initializer-desugar.md §6.
/// The behavior facts lock the M0 transparent contract: dirty files are left
/// byte-identical and Initializers emits no diagnostics, so existing C36
/// behavior is untouched.
/// </summary>
public class InitializerDesugarUnitTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static (CSharpCompilation Compilation, SemanticModel Model) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    private static List<ObjectCreationExpressionSyntax> ExplicitCreations(
        SemanticModel model)
        => model.SyntaxTree.GetCompilationUnitRoot()
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>().ToList();

    private static IObjectCreationOperation OperationOf(
        SemanticModel model, ObjectCreationExpressionSyntax creation)
        => Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(creation));

    private static IReadOnlyList<IOperation> ItemsOf(IObjectCreationOperation op)
        => Assert.IsAssignableFrom<IObjectOrCollectionInitializerOperation>(op.Initializer)
            .Initializers.ToList();

    // ------------------------------------------------------------------
    // Probe group 1: object-initializer member item classification.
    // ------------------------------------------------------------------

    private const string ObjectMembersSource = """
        using System.Collections.Generic;
        public class Dto
        {
            public int Prop { get; set; }
            public int Field;
            public Dto Inner { get; set; } = new Dto();
            public List<int> Items { get; set; } = new List<int>();
        }
        public class C
        {
            public void M()
            {
                var a = new Dto { Prop = 1, Field = 2, Inner = { Prop = 3 }, Items = { 4, 5 } };
                var b = new Dto { Inner = new Dto { Prop = 6 } };
            }
        }
        """;

    [Fact]
    public void Probe_ObjectInitializer_ItemClassification_IsLocked()
    {
        var (compilation, model) = Compile(ObjectMembersSource);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        var creation = ExplicitCreations(model)[2]; // the 'a' initializer
        var op = OperationOf(model, creation);

        // IObjectCreationOperation.Initializer is the common concrete operation
        // for BOTH object and collection initializers.
        Assert.NotNull(op.Initializer);
        Assert.Equal(
            "Microsoft.CodeAnalysis.Operations.ObjectOrCollectionInitializerOperation",
            op.Initializer!.GetType().FullName);
        Assert.Equal(OperationKind.ObjectOrCollectionInitializer, op.Initializer.Kind);
        Assert.IsAssignableFrom<IObjectOrCollectionInitializerOperation>(op.Initializer);

        var items = ItemsOf(op);
        Assert.Equal(4, items.Count);

        // Property write: SimpleAssignment whose Target is a property reference.
        var propertyItem = items[0];
        Assert.Equal(
            "Microsoft.CodeAnalysis.Operations.SimpleAssignmentOperation",
            propertyItem.GetType().FullName);
        var propertyAssignment = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(propertyItem);
        var propertyTarget = Assert.IsAssignableFrom<IPropertyReferenceOperation>(
            propertyAssignment.Target);
        Assert.Equal("Prop", propertyTarget.Property.Name);
        Assert.False(propertyTarget.Property.SetMethod!.IsInitOnly);
        Assert.Empty(propertyTarget.Arguments);

        // Field write: SimpleAssignment whose Target is a field reference.
        var fieldAssignment = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(items[1]);
        Assert.Equal(
            "Microsoft.CodeAnalysis.Operations.FieldReferenceOperation",
            fieldAssignment.Target.GetType().FullName);
        var fieldTarget = Assert.IsAssignableFrom<IFieldReferenceOperation>(fieldAssignment.Target);
        Assert.Equal("Field", fieldTarget.Field.Name);
        Assert.False(fieldTarget.Field.IsReadOnly);

        // Nested member initializer P = { ... }: MemberInitializerOperation.
        var nestedMember = Assert.IsAssignableFrom<IMemberInitializerOperation>(items[2]);
        Assert.Equal(
            "Microsoft.CodeAnalysis.Operations.MemberInitializerOperation",
            items[2].GetType().FullName);
        var nestedMemberRef = Assert.IsAssignableFrom<IPropertyReferenceOperation>(
            nestedMember.InitializedMember);
        Assert.Equal("Inner", nestedMemberRef.Property.Name);
        Assert.Single(nestedMember.Initializer.Initializers);

        // Nested collection member initializer Items = { 4, 5 }: two items.
        var nestedItems = Assert.IsAssignableFrom<IMemberInitializerOperation>(items[3]);
        Assert.Equal(2, nestedItems.Initializer.Initializers.Count());

        // Nested creation RHS: the assignment Value is an ObjectCreationOperation
        // which itself carries the inner initializer.
        var nestedCreate = ExplicitCreations(model)[3]; // the 'b' initializer
        var nestedCreateItem = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(
            ItemsOf(OperationOf(model, nestedCreate)).Single());
        Assert.Equal("Inner", Assert.IsAssignableFrom<IPropertyReferenceOperation>(
            nestedCreateItem.Target).Property.Name);
        var value = Assert.IsAssignableFrom<IObjectCreationOperation>(nestedCreateItem.Value);
        Assert.NotNull(value.Initializer);
    }

    // ------------------------------------------------------------------
    // Probe group 2: dictionary index initializer shape.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_DictionaryIndexInitializer_TargetIsIndexerPropertyReference()
    {
        const string source = """
            using System.Collections.Generic;
            public class MyDict
            {
                private readonly Dictionary<string, int> _d = new();
                public int this[string k] { get => _d[k]; set => _d[k] = value; }
                public void Add(string k, int v) => _d[k] = v;
            }
            public class C
            {
                public void M()
                {
                    var d1 = new Dictionary<int, string> { [1] = "a" };
                    var d2 = new MyDict { ["x"] = 1 };
                }
            }
            """;
        var (compilation, model) = Compile(source);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        foreach (var creation in ExplicitCreations(model)
                     .Where(c => c.Initializer?.IsKind(
                         SyntaxKind.ObjectInitializerExpression) == true))
        {
            var item = ItemsOf(OperationOf(model, creation)).Single();
            Assert.Equal(
                "Microsoft.CodeAnalysis.Operations.SimpleAssignmentOperation",
                item.GetType().FullName);
            var assignment = Assert.IsAssignableFrom<ISimpleAssignmentOperation>(item);

            // The index item binds to a PropertyReference (the indexer), NOT to
            // an ImplicitElementAccess operation.
            var target = Assert.IsAssignableFrom<IPropertyReferenceOperation>(assignment.Target);
            Assert.Equal(
                "Microsoft.CodeAnalysis.Operations.PropertyReferenceOperation",
                assignment.Target.GetType().FullName);
            Assert.True(target.Property.IsIndexer);
            Assert.Single(target.Arguments);
            Assert.Equal("set_Item", target.Property.SetMethod!.Name);
        }
    }

    // ------------------------------------------------------------------
    // Probe group 3: Add binding shapes.
    // ------------------------------------------------------------------

    private const string AddShapesSource = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        public class Pairs : IEnumerable { public void Add(int a, int b) { } public IEnumerator GetEnumerator() => null!; }
        public class FluentBag : IEnumerable { public FluentBag Add(int x) => this; public IEnumerator GetEnumerator() => null!; }
        public class ParamsBag : IEnumerable { public void Add(params int[] xs) { } public IEnumerator GetEnumerator() => null!; }
        public class NoAdd : IEnumerable { public IEnumerator GetEnumerator() => null!; }
        public class GenBag : IEnumerable<int> { public IEnumerator<int> GetEnumerator() => null!; IEnumerator IEnumerable.GetEnumerator() => GetEnumerator(); }
        public static class GenExt { public static void Add(this GenBag b, int x) { } }
        public class C
        {
            public void M()
            {
                var l = new List<int> { 1, 2 };
                var p = new Pairs { { 1, 2 } };
                var f = new FluentBag { 1 };
                var pa = new ParamsBag { 1, 2, 3 };
                var na = new NoAdd { 1 };
                var gb = new GenBag { 1, 2 };
            }
        }
        """;

    [Fact]
    public void Probe_AddBindings_AreInvocations_NotObsoleteCollectionElementInitializer()
    {
        var (compilation, model) = Compile(AddShapesSource);
        var creations = ExplicitCreations(model)
            .Where(c => c.Type.ToString() is "List<int>" or "Pairs" or "FluentBag"
                or "ParamsBag" or "NoAdd" or "GenBag")
            .ToList();

        IReadOnlyList<IOperation> ItemsOfType(string typeName)
            => ItemsOf(OperationOf(model,
                creations.Single(c => c.Type.ToString() == typeName)));

        // Roslyn 5.0.0 replaced ICollectionElementInitializerOperation (now
        // obsolete) with plain IInvocationOperation items.
        var single = ItemsOfType("List<int>");
        Assert.Equal(2, single.Count);
        Assert.All(single, item =>
        {
            Assert.Equal(
                "Microsoft.CodeAnalysis.Operations.InvocationOperation",
                item.GetType().FullName);
            var invocation = Assert.IsAssignableFrom<IInvocationOperation>(item);
            Assert.Equal("Add", invocation.TargetMethod.Name);
            Assert.Single(invocation.Arguments);
            Assert.True(invocation.TargetMethod.ReturnsVoid);
            Assert.False(invocation.TargetMethod.IsExtensionMethod);
            Assert.Null(invocation.TargetMethod.ReducedFrom);
        });

        // Multi-argument complex element { 1, 2 } -> Add(int, int).
        var multi = Assert.IsAssignableFrom<IInvocationOperation>(ItemsOfType("Pairs").Single());
        Assert.Equal(2, multi.Arguments.Length);
        Assert.Equal("Add", multi.TargetMethod.Name);

        // Fluent Add returns non-void; the value is discarded by an expression
        // statement at emission time.
        var fluent = Assert.IsAssignableFrom<IInvocationOperation>(ItemsOfType("FluentBag").Single());
        Assert.False(fluent.TargetMethod.ReturnsVoid);
        Assert.Single(fluent.Arguments);

        // params Add: one Add call per source element; the parameter is bound in
        // its ParamArray form and each argument value is a single-element array.
        var paramsItems = ItemsOfType("ParamsBag");
        Assert.Equal(3, paramsItems.Count);
        Assert.All(paramsItems, item =>
        {
            var invocation = Assert.IsAssignableFrom<IInvocationOperation>(item);
            var argument = invocation.Arguments.Single();
            Assert.True(argument.Parameter!.IsParams);
            Assert.Equal(ArgumentKind.ParamArray, argument.ArgumentKind);
            Assert.Equal("int[]", argument.Parameter.Type.ToDisplayString());
            Assert.Equal("int[]", argument.Value.Type!.ToDisplayString());
        });

        // Extension Add: exposed as an IInvocationOperation whose TargetMethod is
        // the extension (ReducedFrom == null) with the receiver passed as
        // Arguments[0] -- M2 must re-emit reduced syntax from that receiver.
        var extensionItems = ItemsOfType("GenBag");
        Assert.Equal(2, extensionItems.Count);
        Assert.All(extensionItems, item =>
        {
            var invocation = Assert.IsAssignableFrom<IInvocationOperation>(item);
            Assert.True(invocation.TargetMethod.IsExtensionMethod);
            Assert.Null(invocation.TargetMethod.ReducedFrom);
            Assert.Equal(2, invocation.Arguments.Length);
            Assert.Equal("GenBag", invocation.Arguments[0].Value.Type!.ToDisplayString());
        });

        // IEnumerable-without-Add: the item is an error IInvalidOperation and the
        // baseline reports CS1929 (CS1922/CS0117 family) -- M2 maps this to
        // CS2J0533.
        var noAdd = ItemsOfType("NoAdd").Single();
        Assert.Equal(
            "Microsoft.CodeAnalysis.Operations.InvalidOperation",
            noAdd.GetType().FullName);
        Assert.IsAssignableFrom<IInvalidOperation>(noAdd);
        Assert.Contains(
            compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS1929");
    }

    // ------------------------------------------------------------------
    // Probe group 4: init-only / readonly baseline.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_InitOnly_And_ReadonlyGetOnly_BaselineErrors()
    {
        const string source = """
            public class InitOnly { public int P { get; init; } }
            public class ReadOnlyDto
            {
                public readonly int F;
                public int G { get; }
                public ReadOnlyDto(int f) { F = f; }
            }
            public class C
            {
                public void M()
                {
                    var a = new InitOnly { P = 1 };
                    var b = new ReadOnlyDto(0) { F = 1, G = 2 };
                }
            }
            """;
        var (compilation, model) = Compile(source);

        // init-only property: legal in an initializer; IsInitOnly marks it.
        var initOnlyTarget = Assert.IsAssignableFrom<IPropertyReferenceOperation>(
            Assert.IsAssignableFrom<ISimpleAssignmentOperation>(
                ItemsOf(OperationOf(model, ExplicitCreations(model)[0])).Single()).Target);
        Assert.Equal("P", initOnlyTarget.Property.Name);
        Assert.True(initOnlyTarget.Property.SetMethod!.IsInitOnly);

        // readonly field / get-only property in an initializer: baseline CS0191/CS0200.
        var errorIds = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id).ToHashSet();
        Assert.Contains("CS0191", errorIds);
        Assert.Contains("CS0200", errorIds);

        // The constructor-body write to the readonly field still resolves.
        var ctorWrite = model.SyntaxTree.GetCompilationUnitRoot()
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .First(a => a.Left.ToString() == "F");
        var ctorField = Assert.IsAssignableFrom<IFieldSymbol>(
            model.GetSymbolInfo(ctorWrite.Left).Symbol);
        Assert.True(ctorField.IsReadOnly);

        // In the (error) initializer, the read-only member symbols do not
        // resolve -- M1 cannot rely on a symbol for the defensive CS2J0531
        // classification of read-only members.
        var readOnlyWrites = model.SyntaxTree.GetCompilationUnitRoot()
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Parent is InitializerExpressionSyntax)
            .Where(a => a.Left.ToString() is "F" or "G")
            .ToList();
        Assert.Equal(2, readOnlyWrites.Count);
        Assert.All(readOnlyWrites,
            a => Assert.Null(model.GetSymbolInfo(a.Left).Symbol));
    }

    // ------------------------------------------------------------------
    // Probe group 5: target-typed new() ConvertedType across contexts.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_TargetTypedNew_ConvertedType_AcrossContexts()
    {
        const string source = """
            using System.Collections.Generic;
            public class TT
            {
                public List<int> Field = new();
                public List<int> Prop { get; set; } = new();
                public TT() : this(new()) { }
                public TT(List<int> x) { }
                public List<int> Local() { List<int> a = new(); return new(); }
                public void Arg(List<int> x) { Arg(new()); }
                public List<int> Tern(bool b) => b ? new() : new();
                public List<int>? Nullable() => new();
            }
            """;
        var (compilation, model) = Compile(source);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        var sites = model.SyntaxTree.GetCompilationUnitRoot()
            .DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>().ToList();

        // Field, property, :this(..), local, return, argument, two ternary branches,
        // nullable return = 9 sites.
        Assert.Equal(9, sites.Count);
        Assert.All(sites, site =>
        {
            var info = model.GetTypeInfo(site);
            Assert.Equal(
                "System.Collections.Generic.List<int>",
                info.ConvertedType?.ToDisplayString());
            // Nullable target: ConvertedType is already annotation-free.
            Assert.Equal(NullableAnnotation.NotAnnotated, info.ConvertedType?.NullableAnnotation);
        });

        // Implicit creation also binds to a plain ObjectCreationOperation.
        Assert.All(sites, site => Assert.Equal(
            "Microsoft.CodeAnalysis.Operations.ObjectCreationOperation",
            model.GetOperation(site)?.GetType().FullName));
    }

    // ------------------------------------------------------------------
    // Probe group 6: dirty-gate + residual predicates.
    // ------------------------------------------------------------------

    [Fact]
    public void Probe_DirtyGate_And_ResidualPredicates()
    {
        const string source = """
            using System.Collections.Generic;
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    var a = new Dto { A = 1 };
                    var b = new Dto { };
                    var c = new Dto();
                    var d = new List<int> { 1 };
                    var e = new();
                    int[] f = [1, 2];
                    int[] g = new int[] { 1 };
                    int[] h = new[] { 1 };
                    var anon = new { A = 1 };
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();

        // Triggers: object creation with an initializer (object or collection),
        // and any target-typed new() (bare or with initializer).
        Assert.Equal(4, root.DescendantNodes()
            .Count(InitializerSyntaxFacts.IsTriggerNode));
        Assert.True(InitializerSyntaxFacts.HasTriggerSyntax(root));

        // Excluded: plain new Dto(), array creations and anonymous objects.
        Assert.False(InitializerSyntaxFacts.IsTriggerNode(
            root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Single(o => o.Initializer is null && o.Type.ToString() == "Dto")));
        Assert.All(root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>(),
            n => Assert.False(InitializerSyntaxFacts.IsTriggerNode(n)));
        Assert.All(root.DescendantNodes().OfType<ImplicitArrayCreationExpressionSyntax>(),
            n => Assert.False(InitializerSyntaxFacts.IsTriggerNode(n)));
        Assert.All(root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>(),
            n => Assert.False(InitializerSyntaxFacts.IsTriggerNode(n)));

        // Residual kinds: object/collection/complex initializer expressions plus
        // bare new(); array initializer expressions are excluded.
        var initializerExpressions = root.DescendantNodes()
            .OfType<InitializerExpressionSyntax>().ToList();
        Assert.Contains(initializerExpressions,
            i => i.IsKind(SyntaxKind.ObjectInitializerExpression)
                 && InitializerSyntaxFacts.IsResidualNode(i));
        Assert.Contains(initializerExpressions,
            i => i.IsKind(SyntaxKind.CollectionInitializerExpression)
                 && InitializerSyntaxFacts.IsResidualNode(i));
        Assert.All(initializerExpressions
                .Where(i => i.IsKind(SyntaxKind.ArrayInitializerExpression)),
            i =>
            {
                Assert.False(InitializerSyntaxFacts.IsResidualNode(i));
                Assert.True(InitializerSyntaxFacts.IsExcludedInitializer(i));
            });
        Assert.All(root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>(),
            n => Assert.True(InitializerSyntaxFacts.IsResidualNode(n)));
        Assert.All(root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>(),
            n => Assert.True(InitializerSyntaxFacts.IsExcludedCreation(n)));

        // Complex element { 1, 2 } is a residual kind.
        const string complex = """
            using System.Collections;
            public class Pairs : IEnumerable { public void Add(int a, int b) { } public IEnumerator GetEnumerator() => null!; }
            public static class C2 { public static void M() { var p = new Pairs { { 1, 2 } }; } }
            """;
        var complexRoot = CSharpSyntaxTree.ParseText(complex).GetCompilationUnitRoot();
        Assert.Contains(
            complexRoot.DescendantNodes().OfType<InitializerExpressionSyntax>(),
            i => i.IsKind(SyntaxKind.ComplexElementInitializerExpression)
                 && InitializerSyntaxFacts.IsResidualNode(i));

        // Explained pairing honours the shared family annotation.
        var annotated = root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .First(o => o.Initializer != null)
            .WithAdditionalAnnotations(ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        Assert.True(InitializerSyntaxFacts.IsExplained(annotated));
        var plain = root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>().First();
        Assert.False(InitializerSyntaxFacts.IsExplained(plain));
    }

    // ------------------------------------------------------------------
    // Probe group 7: family registration + options surface.
    // ------------------------------------------------------------------

    [Fact]
    public void M0_RuleSet_RegistersInitializersBeforeCollectionExpr()
    {
        Assert.Equal(
            new[] { "Initializers", "CollectionExpr" },
            ExpressionDesugarRuleSet.Rules.Select(r => r.Id).ToArray());

        // Options subsection is nullable and absent by default.
        Assert.Null(new ExpressionDesugarOptions().Initializers);
        Assert.True(InitializerOptions.Default.Strict);
        Assert.False(InitializerOptions.Default.RewriteInExpressionTrees);
        Assert.Equal(InitializerScope.All, InitializerOptions.Default.Scope);
        Assert.Equal(
            InitializerUnsupportedPolicy.Error, InitializerOptions.Default.Unsupported);
    }

    // ------------------------------------------------------------------
    // M0 behavior lock: the rule is transparent (no rewrite / no diagnostics).
    // ------------------------------------------------------------------

    private static SemanticModel BuildModel(string source) => Compile(source).Model;

    private static ExpressionDesugarResult Preprocess(string source)
        => new ExpressionDesugarPreprocessor().Preprocess(source, BuildModel(source));

    private static string Normalize(string code)
        => Regex.Replace(code, @"\s+", " ");

    [Fact]
    public void M0_CleanFile_Unchanged_AndCountedAsSkippedClean()
    {
        const string source = "public class C { void M() { int x = 1; } }\n";

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(1, result.Statistics.Initializers.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.Initializers.ObjectInitializerSites);
    }

    [Fact]
    public void M0_InitializerOnlyFile_IsDirtyNoRewrite_CountedAndBytesPreserved()
    {
        const string source = """
            public class Dto { public int Prop { get; set; } public int Field; }
            public static class C
            {
                public static void M()
                {
                    var d = new Dto { Prop = 1, Field = 2 };
                }
            }
            """;

        var result = Preprocess(source);

        // No C36 syntax: the file is dirty for Initializers only, and M0 leaves
        // it byte-identical (dirty-no-rewrite), emitting no diagnostics.
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(0, result.Statistics.FilesTransformed);

        var init = result.Statistics.Initializers;
        Assert.Equal(1, init.FilesDirtyNoRewrite);
        Assert.Equal(1, init.ObjectInitializerSites);
        Assert.Equal(0, init.CollectionInitializerSites);
        Assert.Equal(2, init.MemberAssignments);
        Assert.Equal(0, init.AddCalls);
    }

    [Fact]
    public void M0_InventoryCounters_CoverAllSiteKinds()
    {
        const string source = """
            using System.Collections.Generic;
            public class Dto { public int Prop { get; set; } }
            public static class C
            {
                public static void M()
                {
                    var a = new Dto { Prop = 1 };
                    var b = new Dto { Prop = 2 };
                    var l = new List<int>(8) { 3, 4 };
                    var keep = new Dto();
                    Dictionary<int, string> d = new() { [1] = "x" };
                    Dto n = new();
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);

        var init = result.Statistics.Initializers;
        Assert.Equal(3, init.ObjectInitializerSites);        // a, b, and new(){ [1]=.. }
        Assert.Equal(1, init.CollectionInitializerSites);    // new List<int>(8) { 3, 4 }
        Assert.Equal(1, init.DictionaryIndexInitializerSites);
        Assert.Equal(3, init.MemberAssignments);             // a.Prop, b.Prop, [1]
        Assert.Equal(1, init.MemberIndexerAssignments);
        Assert.Equal(2, init.TargetTypedNewSites);           // new() { ... }, new()
        Assert.Equal(1, init.TargetTypedNewBareSites);
        Assert.Equal(1, init.TargetTypedNewWithInitializerSites);
        Assert.Equal(2, init.AddCalls);                      // 3, 4
        Assert.Equal(2, init.AddSingle);
    }

    [Fact]
    public void M0_C36Sibling_StillRewrites_InitializersDoNotInterfere()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    int[] a = [1, 2];
                    var d = new Dto { A = 1 };
                }
            }
            """;

        var result = Preprocess(source);

        // C36 still rewrites its collection-expression site exactly as before;
        // the C24-dirty object initializer is left untouched and silent.
        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.Contains("new int[] { 1, 2 }", Normalize(result.OutputCode));
        Assert.Equal(1, result.Statistics.CollectionExpr.CollectionSites);
        Assert.Equal(1, result.Statistics.CollectionExpr.ByTargetArray);
        Assert.Equal(1, result.Statistics.FilesTransformed);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.Initializers.ObjectInitializerSites);
        Assert.Equal(1, result.Statistics.Initializers.FilesTransformed);

        // Idempotence: the C24 site makes the output dirty again, but M0 leaves
        // it byte-identical on the second run.
        var second = Preprocess(result.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(result.OutputCode, second.OutputCode);
    }

    [Fact]
    public void M0_DirtyFile_WithoutSemanticModel_IsDirtyNoRewrite_NoCrash()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C { public static void M() { var d = new Dto { A = 1 }; } }
            """;

        var result = new ExpressionDesugarPreprocessor()
            .Preprocess(source, semanticModel: null);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.Initializers.FilesDirtyNoRewrite);
    }
}