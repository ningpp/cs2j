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
/// C24 object/collection-initializer probes and behavior tests against
/// Roslyn 5.0.0 (Microsoft.CodeAnalysis.CSharp 5.0.x). The probe facts pin the
/// runtime IOperation shapes M1 lowering depends on; conclusions are mirrored
/// in docs/superpowers/plans/2026-09-29-c24-initializer-desugar.md §6.
/// The M1 facts lock the main-path desugaring (object sequence, single-arg
/// Add, bare <c>new()</c> explicitization), the CS2J0530/0531/0532/0533/0535/
/// 0536/0542 diagnostics and the preserved-site boundaries (M2 shapes, C36
/// coexistence deferral).
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
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
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
        // Merged family order (C23 first, C24 second, C35 third):
        // Interpolations, Initializers, NullConditional, CollectionExpr.
        Assert.Equal(
            new[] { "Interpolations", "Initializers", "NullConditional", "CollectionExpr" },
            ExpressionDesugarRuleSet.Rules.Select(r => r.Id).ToArray());

        // Options subsections are nullable and absent by default.
        Assert.Null(new ExpressionDesugarOptions().Initializers);
        Assert.Null(new ExpressionDesugarOptions().NullConditional);
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

    /// <summary>
    /// Opt-in options: the C24 rule is inert unless the
    /// <see cref="ExpressionDesugarOptions.Initializers"/> subsection is present
    /// (rollout gate, design §9), so the M1 facts activate it explicitly.
    /// </summary>
    private static readonly ExpressionDesugarOptions C24Enabled = new()
    {
        Initializers = new InitializerOptions(),
    };

    private static ExpressionDesugarResult Preprocess(
        string source, ExpressionDesugarOptions? options = null)
        => new ExpressionDesugarPreprocessor().Preprocess(
            source, BuildModel(source), options ?? C24Enabled);

    private static string Normalize(string code)
        => Regex.Replace(code, @"\s+", " ");

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(
            output, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-out-" + Guid.NewGuid().ToString("N"),
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

    private static int IndexOf(string normalized, string fragment)
        => normalized.IndexOf(fragment, StringComparison.Ordinal);

    private static bool HasDiagnostic(
        ExpressionDesugarResult result, string code, ExpressionDesugarSeverity severity)
        => result.Diagnostics.Any(d => d.Code == code && d.Severity == severity);

    [Fact]
    public void M6_InterpolationCoexistence_InitializerSitesStillLower()
    {
        const string source = """
            public static class Program
            {
                public static void Main()
                {
                    var settings = new BuildSettings { Name = "app", Retries = 1 };
                    var reusable = new Reusable { V = 11 };
                    System.Console.WriteLine($"{settings.Retries},{reusable.V}");
                    System.Console.WriteLine($"{settings.Retries}");
                }
            }

            public struct BuildSettings
            {
                public string Name { get; set; }
                public int Retries { get; set; }
            }
            public struct Reusable { public int V; }
            """;

        var result = Preprocess(source, new ExpressionDesugarOptions
        {
            Initializers = new InitializerOptions(),
            Interpolations = new CSharpToJava.Core.ExpressionDesugar.Interpolations.InterpolationOptions
            {
                Enabled = true,
                FoldConstants = true,
                KeepFormattableString = true,
            },
        });

        var diags = string.Join("\n",
            result.Diagnostics.Select(d => $"{d.Severity} {d.Code}: {d.Message}"));
        Assert.True(result.Changed, "DIAGS:\n" + diags + "\nOUT:\n" + result.OutputCode);
        Assert.DoesNotContain("CS2J0545", diags);
        Assert.DoesNotContain("CS2J0516", diags);
        Assert.Contains("__ed_", result.OutputCode);
        Assert.DoesNotContain("new BuildSettings {", result.OutputCode);
        Assert.DoesNotContain("new Reusable {", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

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
    public void M1_RolloutGate_InertWithoutOptionsSubsection()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C { public static void M() { var d = new Dto { A = 1 }; } }
            """;

        // No Initializers subsection -> the rule stays inert (design §9
        // DefaultEnabled=false equivalent for C24); M0 behaviour preserved.
        var result = new ExpressionDesugarPreprocessor()
            .Preprocess(source, BuildModel(source));

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.Initializers.FilesDirtyNoRewrite);
    }

    [Fact]
    public void M1_ObjectInitializer_PropertyFieldMixed_SourceOrder()
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

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        var construct = IndexOf(normalized, "var __ed_0 = new Dto()");
        var propWrite = IndexOf(normalized, "__ed_0.Prop = 1");
        var fieldWrite = IndexOf(normalized, "__ed_0.Field = 2");
        var alias = IndexOf(normalized, "var d = __ed_0");
        Assert.True(
            construct >= 0 && propWrite > construct && fieldWrite > propWrite
            && alias > fieldWrite, normalized);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0530, ExpressionDesugarSeverity.Info));

        var init = result.Statistics.Initializers;
        Assert.Equal(1, init.ObjectInitializerSites);
        Assert.Equal(0, init.CollectionInitializerSites);
        Assert.Equal(2, init.MemberAssignments);
        Assert.Equal(1, init.MemberPropertyAssignments);
        Assert.Equal(1, init.MemberFieldAssignments);
        Assert.Equal(0, init.AddCalls);
        Assert.Equal(1, init.ContextLoweredSites);
        Assert.Equal(1, init.TempsLifted);
    }

    [Fact]
    public void M1_ObjectInitializer_WithCtorArgs_Preserved()
    {
        const string source = """
            public class Point
            {
                public int X;
                public int Y;
                public Point(int x, int y) { X = x; Y = y; }
            }
            public static class C
            {
                public static void M()
                {
                    var p = new Point(1, 2) { X = 3 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        var construct = IndexOf(normalized, "var __ed_0 = new Point(1, 2);");
        var write = IndexOf(normalized, "__ed_0.X = 3");
        Assert.True(construct >= 0 && write > construct, normalized);
    }

    [Fact]
    public void M1_ObjectInitializer_CtorArgumentsEvaluatedBeforeMemberValues()
    {
        const string source = """
            public class Dto
            {
                public int P { get; set; }
                public Dto(int seed) { }
            }
            public static class C
            {
                static int Next() => 1;
                public static void M()
                {
                    var d = new Dto(Next()) { P = Next() };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        var construct = IndexOf(normalized, "var __ed_0 = new Dto(Next())");
        var write = IndexOf(normalized, "__ed_0.P = Next()");
        Assert.True(construct >= 0 && write > construct, normalized);
    }

    [Fact]
    public void M1_ObjectInitializer_Empty_FoldsToConstructorCall()
    {
        const string source = """
            public class Dto { public int P { get; set; } }
            public static class C
            {
                public static void M()
                {
                    var d = new Dto { };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("new Dto()", normalized);
        Assert.DoesNotContain("{ }", normalized);
        Assert.Equal(1, result.Statistics.Initializers.ObjectInitializerSites);
    }

    [Fact]
    public void M1_ObjectInitializer_SecondRunByteIdentical()
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

        var first = Preprocess(source);
        Assert.True(first.Changed);
        AssertOutputCompiles(first.OutputCode);

        var second = Preprocess(first.OutputCode);
        Assert.False(second.Changed, string.Join("; ", second.Diagnostics));
        Assert.Equal(first.OutputCode, second.OutputCode);
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

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var init = result.Statistics.Initializers;
        Assert.Equal(3, init.ObjectInitializerSites);        // a, b, and new(){ [1]=.. }
        Assert.Equal(1, init.CollectionInitializerSites);    // new List<int>(8) { 3, 4 }
        Assert.Equal(1, init.DictionaryIndexInitializerSites);
        Assert.Equal(3, init.MemberAssignments);             // a.Prop, b.Prop, [1]
        Assert.Equal(2, init.MemberPropertyAssignments);     // a.Prop, b.Prop
        Assert.Equal(1, init.MemberIndexerAssignments);      // [1]
        Assert.Equal(2, init.TargetTypedNewSites);           // new() { ... }, new()
        Assert.Equal(1, init.TargetTypedNewBareSites);
        Assert.Equal(1, init.TargetTypedNewWithInitializerSites);
        Assert.Equal(2, init.AddCalls);                      // 3, 4
        Assert.Equal(2, init.AddSingle);

        // The dictionary-index site is an M2 shape: preserved verbatim.
        Assert.Contains("[1] = \"x\"", Normalize(result.OutputCode));
    }

    [Fact]
    public void M1_C36Coexistence_C36SiteInFile_DefersWholeFile_ThenSecondRunCompletes()
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

        var first = Preprocess(source);

        // Verified empirically (see M1_C36Coexistence_RewriterIdentityIsNotPreserved):
        // a Roslyn rewrite of ANY node yields fresh instances for untouched
        // siblings too, so C36's original-node-keyed plan lookup breaks as soon
        // as C24 rewrites anything in the file. C24 therefore defers the WHOLE
        // file while C36 collection-expression syntax is present: C36 lowers its
        // site, the C24 site stays verbatim, and no diagnostic is produced.
        Assert.True(first.Changed, string.Join("; ", first.Diagnostics));
        Assert.Contains("new int[] { 1, 2 }", Normalize(first.OutputCode));
        Assert.Contains("new Dto { A = 1 }", Normalize(first.OutputCode));
        Assert.Empty(first.Diagnostics);
        Assert.Equal(1, first.Statistics.CollectionExpr.CollectionSites);
        Assert.Equal(1, first.Statistics.CollectionExpr.ByTargetArray);
        Assert.Equal(1, first.Statistics.Initializers.ObjectInitializerSites);
        Assert.Equal(1, first.Statistics.Initializers.CoexistenceDeferredSites);

        // The C36 syntax is gone after the first run, so the second run lowers
        // the deferred initializer; a third run is byte-stable.
        var second = Preprocess(first.OutputCode);
        Assert.True(second.Changed, string.Join("; ", second.Diagnostics));
        Assert.Contains("__ed_0.A = 1", Normalize(second.OutputCode));
        AssertOutputCompiles(second.OutputCode);

        var third = Preprocess(second.OutputCode);
        Assert.False(third.Changed, string.Join("; ", third.Diagnostics));
        Assert.Equal(second.OutputCode, third.OutputCode);
    }

    [Fact]
    public void M1_C36Coexistence_RewriterIdentityIsNotPreserved()
    {
        // Ground truth for the whole-file deferral above: Roslyn's rewriter
        // returns a DIFFERENT instance for an untouched sibling whenever some
        // other node in the file is replaced, so node identity cannot key a plan
        // across progressive rule rewrites in the same family pass.
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
        var root = CSharpSyntaxTree
            .ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot();
        var collectionExpression = root.DescendantNodes()
            .OfType<CollectionExpressionSyntax>().Single();

        var rewritten =
            (CompilationUnitSyntax)new MinimalReplaceRewriter().Visit(root)!;
        var after = rewritten.DescendantNodes()
            .OfType<CollectionExpressionSyntax>().Single();

        Assert.NotSame(collectionExpression, after);
    }

    private sealed class MinimalReplaceRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitObjectCreationExpression(
            ObjectCreationExpressionSyntax node)
            => SyntaxFactory.IdentifierName("__replaced");
    }

    [Fact]
    public void M1_C36Coexistence_NestedSiteDefers_OuterStillRewrites()
    {
        const string source = """
            public class Point { public int X { get; set; } public int Y { get; set; } }
            public static class C
            {
                public static void M()
                {
                    Point[] c = [new Point { X = 1, Y = 2 }];
                }
            }
            """;

        var first = Preprocess(source);

        // Whole-file deferral: C36 lowers the enclosing collection expression
        // while the nested object initializer stays verbatim (counted as a
        // deferred site), so C36's plan identity is never disturbed.
        Assert.True(first.Changed, string.Join("; ", first.Diagnostics));
        Assert.Contains("new global::Point[] { new Point { X = 1, Y = 2 } }",
            Normalize(first.OutputCode));
        Assert.Equal(1, first.Statistics.Initializers.CoexistenceDeferredSites);
        AssertOutputCompiles(first.OutputCode);

        // With the C36 syntax gone, the deferred site lowers on the next run.
        var second = Preprocess(first.OutputCode);
        Assert.True(second.Changed, string.Join("; ", second.Diagnostics));
        Assert.Contains("__ed_0.X = 1", Normalize(second.OutputCode));
        AssertOutputCompiles(second.OutputCode);

        var third = Preprocess(second.OutputCode);
        Assert.False(third.Changed, string.Join("; ", third.Diagnostics));
        Assert.Equal(second.OutputCode, third.OutputCode);
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

    // ------------------------------------------------------------------
    // M1: collection initializers (IN-3/IN-6).
    // ------------------------------------------------------------------

    [Fact]
    public void M1_CollectionInitializer_CtorArgsPreserved_OrderedAdds_NoPut()
    {
        const string source = """
            using System.Collections.Generic;
            public static class C
            {
                public static void M()
                {
                    var xs = new List<int>(8) { 1, 2 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        var construct = IndexOf(normalized, "var __ed_0 = new List<int>(8);");
        var add1 = IndexOf(normalized, "__ed_0.Add(1)");
        var add2 = IndexOf(normalized, "__ed_0.Add(2)");
        var alias = IndexOf(normalized, "var xs = __ed_0");
        Assert.True(construct >= 0 && add1 > construct && add2 > add1
            && alias > add2, normalized);
        Assert.DoesNotContain("put", normalized);
        Assert.DoesNotContain("{ 1, 2 }", normalized);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0532, ExpressionDesugarSeverity.Info));

        var init = result.Statistics.Initializers;
        Assert.Equal(1, init.CollectionInitializerSites);
        Assert.Equal(0, init.ObjectInitializerSites);
        Assert.Equal(2, init.AddCalls);
        Assert.Equal(2, init.AddSingle);
        Assert.Equal(0, init.AddMulti);
        Assert.Equal(1, init.TempsLifted);
    }

    [Fact]
    public void M1_CollectionInitializer_FluentAdd_ValueDiscarded()
    {
        const string source = """
            using System.Collections;
            using System.Collections.Generic;
            public class FluentBag : IEnumerable
            {
                public FluentBag Add(int x) => this;
                public IEnumerator GetEnumerator() => null!;
            }
            public static class C
            {
                public static void M()
                {
                    var b = new FluentBag { 1, 2 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("__ed_0.Add(1)", normalized);
        Assert.Contains("__ed_0.Add(2)", normalized);

        var init = result.Statistics.Initializers;
        Assert.Equal(2, init.AddSingle);
        Assert.Equal(2, init.AddFluent);
    }

    // ------------------------------------------------------------------
    // M1: bare target-typed new() (IN-9).
    // ------------------------------------------------------------------

    [Fact]
    public void M1_BareTargetTypedNew_LocalReturnNullable_ExpressionBodied()
    {
        const string source = """
            using System.Collections.Generic;
            public class TT
            {
                public List<int> Local() { List<int> a = new(); return new(); }
                public List<int>? Nullable() => new();
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        Assert.DoesNotContain("new()", normalized);
        Assert.Contains("new global::System.Collections.Generic.List<int>()", normalized);

        Assert.Equal(3, result.Diagnostics
            .Count(d => d.Code == InitializerDiagnosticCodes.CS2J0536
                        && d.Severity == ExpressionDesugarSeverity.Info));
        Assert.Equal(3, result.Statistics.Initializers.TargetTypedNewBareSites);
    }

    [Fact]
    public void M1_TargetTypedNew_WithInitializer_SequenceUsesExplicitType()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    Dto d = new() { A = 1 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("new global::Dto()", normalized);
        Assert.Contains("__ed_0.A = 1", normalized);
        Assert.Equal(1, result.Statistics.Initializers.TargetTypedNewWithInitializerSites);
    }

    [Fact]
    public void M1_BareTargetTypedNew_UninferrableTarget_CS2J0535_AtomicUnchanged()
    {
        const string source = """
            public static class C
            {
                public static void M()
                {
                    var x = new();
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0535, ExpressionDesugarSeverity.Error));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
    }

    // ------------------------------------------------------------------
    // M1: boundaries and diagnostics.
    // ------------------------------------------------------------------

    [Fact]
    public void M1_InitOnlyProperty_CS2J0531_FileAtomicallyUnchanged()
    {
        const string source = """
            public class InitOnly { public int P { get; init; } }
            public static class C
            {
                public static void M()
                {
                    var a = new InitOnly { P = 1 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0531, ExpressionDesugarSeverity.Error));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.Initializers.ObjectInitializerSites);
    }

    [Fact]
    public void M1_ReadonlyField_CS2J0531()
    {
        const string source = """
            public class ReadOnlyDto
            {
                public readonly int F;
                public ReadOnlyDto(int f) { F = f; }
            }
            public static class C
            {
                public static void M()
                {
                    var a = new ReadOnlyDto(0) { F = 1 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0531, ExpressionDesugarSeverity.Error));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void M1_UnresolvedAdd_CS2J0533()
    {
        const string source = """
            using System.Collections;
            public class NoAdd : IEnumerable
            {
                public IEnumerator GetEnumerator() => null!;
            }
            public static class C
            {
                public static void M()
                {
                    var n = new NoAdd { 1 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0533, ExpressionDesugarSeverity.Error));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void M1_NonStrict_DowngradesAndPreservesBadSite_WhileSiblingsRewrite()
    {
        const string source = """
            public class InitOnly { public int P { get; init; } }
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    var a = new InitOnly { P = 1 };
                    var b = new Dto { A = 2 };
                }
            }
            """;

        var result = Preprocess(source, new ExpressionDesugarOptions
        {
            Initializers = new InitializerOptions { Strict = false },
        });

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0531, ExpressionDesugarSeverity.Warning));

        var normalized = Normalize(result.OutputCode);
        Assert.Contains("new InitOnly { P = 1 }", normalized);
        Assert.Contains("var __ed_0 = new Dto(); __ed_0.A = 2; var b = __ed_0;", normalized);
    }

    [Fact]
    public void M1_ExpressionTree_CS2J0542_SkippedAndUnchanged()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    Expression<Func<Dto>> e = () => new Dto { A = 1 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0542, ExpressionDesugarSeverity.Info));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void M1_ArraysAndAnonymousObjects_AreNotTouched()
    {
        const string source = """
            public static class C
            {
                public static void M()
                {
                    int[] a = new int[] { 1, 2 };
                    int[] b = new[] { 1 };
                    var anon = new { A = 1 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.Initializers.ObjectInitializerSites);
        Assert.Equal(0, result.Statistics.Initializers.CollectionInitializerSites);
    }

    [Fact]
    public void M2_FormerM1ShapeSites_AreNowRewritten()
    {
        const string source = """
            using System.Collections;
            using System.Collections.Generic;
            public class Dto
            {
                public int A { get; set; }
                public Dto Inner { get; set; } = new Dto();
            }
            public class Pairs : IEnumerable
            {
                public void Add(int a, int b) { }
                public IEnumerator GetEnumerator() => null!;
            }
            public static class C
            {
                public static void M()
                {
                    var a = new Dto { A = 1, Inner = new Dto { A = 2 } };
                    var b = new Dto { Inner = { A = 3 } };
                    Dictionary<int, string> d = new() { [1] = "x" };
                    var p = new Pairs { { 1, 2 } };
                }
            }
            """;

        var result = Preprocess(source);

        // M2 handles the shapes M1 preserved: nested creation, nested member
        // init, dictionary index and multi-arg Add all desugar now.
        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.DoesNotContain("new Dto { A = 2 }", normalized);
        Assert.DoesNotContain("{ A = 3 }", normalized);
        Assert.DoesNotContain("{ [1] = \"x\" }", normalized);
        Assert.DoesNotContain("{ { 1, 2 } }", normalized);
        Assert.Contains(".Add(1, 2);", normalized);

        var init = result.Statistics.Initializers;
        Assert.Equal(1, result.Statistics.Initializers.DictionaryIndexInitializerSites);
        Assert.Equal(1, init.AddMulti);
        Assert.Equal(1, init.NestedCreateSites);
        Assert.Equal(1, init.NestedMemberInitSites);

        // Idempotent on the second run.
        var second = Preprocess(result.OutputCode);
        Assert.False(second.Changed, string.Join("; ", second.Diagnostics));
        Assert.Equal(result.OutputCode, second.OutputCode);
    }

    [Fact]
    public void M1_CleanFileWithBom_BytePreserved()
    {
        const string source = "\uFEFFpublic class C { void M() { int x = 1; } }\n";

        var result = Preprocess(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    // ------------------------------------------------------------------
    // M2: nested creation (IN-4).
    // ------------------------------------------------------------------

    [Fact]
    public void M2_NestedCreate_TwoLevels_InnerBeforeOuterAssignment()
    {
        const string source = """
            public class I { public int D { get; set; } }
            public class O { public I Inner { get; set; } = new I(); }
            public static class C
            {
                public static void M() { var o = new O { Inner = new I { D = 5 } }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        var outer = IndexOf(normalized, "var __ed_0 = new O();");
        var inner = IndexOf(normalized, "var __ed_1 = new I();");
        var write = IndexOf(normalized, "__ed_1.D = 5;");
        var assign = IndexOf(normalized, "__ed_0.Inner = __ed_1;");
        Assert.True(outer >= 0 && inner > outer && write > inner && assign > write,
            normalized);

        var init = result.Statistics.Initializers;
        Assert.Equal(1, init.NestedCreateSites);
    }

    [Fact]
    public void M2_NestedCreate_ThreeLevels_BottomUp()
    {
        const string source = """
            public class C3 { public int X { get; set; } }
            public class B3 { public C3 C { get; set; } = new C3(); }
            public class A3 { public B3 B { get; set; } = new B3(); }
            public static class C
            {
                public static void M() { var a = new A3 { B = new B3 { C = new C3 { X = 1 } } }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.True(
            IndexOf(normalized, "var __ed_0 = new A3();")
            < IndexOf(normalized, "var __ed_1 = new B3();")
            && IndexOf(normalized, "var __ed_1 = new B3();")
            < IndexOf(normalized, "var __ed_2 = new C3();")
            && IndexOf(normalized, "var __ed_2 = new C3();")
            < IndexOf(normalized, "__ed_2.X = 1;")
            && IndexOf(normalized, "__ed_2.X = 1;")
            < IndexOf(normalized, "__ed_1.C = __ed_2;")
            && IndexOf(normalized, "__ed_1.C = __ed_2;")
            < IndexOf(normalized, "__ed_0.B = __ed_1;"), normalized);

        Assert.Equal(2, result.Statistics.Initializers.NestedCreateSites);
    }

    [Fact]
    public void M2_NestedCreate_InsideCollectionElement_InlineLowered()
    {
        const string source = """
            using System.Collections.Generic;
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M() { var l = new List<Dto> { new Dto { A = 1 } }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.True(
            IndexOf(normalized, "var __ed_0 = new List<Dto>();")
            < IndexOf(normalized, "var __ed_1 = new Dto();")
            && IndexOf(normalized, "var __ed_1 = new Dto();")
            < IndexOf(normalized, "__ed_1.A = 1;")
            && IndexOf(normalized, "__ed_1.A = 1;")
            < IndexOf(normalized, "__ed_0.Add(__ed_1);"), normalized);
        Assert.DoesNotContain("new Dto { A = 1 }", normalized);
    }

    // ------------------------------------------------------------------
    // M2: nested member initializer (IN-5).
    // ------------------------------------------------------------------

    [Fact]
    public void M2_NestedMemberInit_GetterTempEvaluatedOnce()
    {
        const string source = """
            public class I { public int D { get; set; } }
            public class O { public I Inner { get; set; } = new I(); }
            public static class C
            {
                public static void M() { var o = new O { Inner = { D = 5 } }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.True(
            IndexOf(normalized, "var __ed_0 = new O();")
            < IndexOf(normalized, "var __ed_1 = __ed_0.Inner;")
            && IndexOf(normalized, "var __ed_1 = __ed_0.Inner;")
            < IndexOf(normalized, "__ed_1.D = 5;"), normalized);
        Assert.DoesNotContain("__ed_0.Inner =", normalized);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0540, ExpressionDesugarSeverity.Info));
        Assert.Equal(1, result.Statistics.Initializers.NestedMemberInitSites);
    }

    [Fact]
    public void M2_NestedMemberInit_MultiLevel_ChainsLayerByLayer()
    {
        const string source = """
            public class C3 { public int X { get; set; } }
            public class B3 { public C3 C { get; set; } = new C3(); }
            public class A3 { public B3 B { get; set; } = new B3(); }
            public static class C
            {
                public static void M() { var a = new A3 { B = { C = { X = 1 } } }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.True(
            IndexOf(normalized, "var __ed_1 = __ed_0.B;")
            < IndexOf(normalized, "var __ed_2 = __ed_1.C;")
            && IndexOf(normalized, "var __ed_2 = __ed_1.C;")
            < IndexOf(normalized, "__ed_2.X = 1;"), normalized);
    }

    // ------------------------------------------------------------------
    // M2: dictionary index initializer (IN-8).
    // ------------------------------------------------------------------

    [Fact]
    public void M2_DictionaryIndex_SingleAndMultiKey_IndexAssignments()
    {
        const string source = """
            using System.Collections.Generic;
            public class MyDict
            {
                private readonly int[,] _grid = new int[2, 2];
                public int this[int a, int b] { get => _grid[a, b]; set => _grid[a, b] = value; }
            }
            public static class C
            {
                public static void M()
                {
                    var d = new Dictionary<int, string> { [1] = "a", [2] = "b" };
                    var g = new MyDict { [0, 1] = 7 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.Contains("__ed_0[1] = \"a\";", normalized);
        Assert.Contains("__ed_0[2] = \"b\";", normalized);
        Assert.Contains("[0, 1] = 7", normalized);

        var init = result.Statistics.Initializers;
        Assert.Equal(2, init.DictionaryIndexInitializerSites);
        Assert.Equal(3, init.MemberIndexerAssignments);
        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0534, ExpressionDesugarSeverity.Info));
    }

    [Fact]
    public void M2_DictionaryIndex_NonWritableIndexer_CS2J0531()
    {
        const string source = """
            public class RO
            {
                public int this[int k] => k;
            }
            public static class C
            {
                public static void M() { var d = new RO { [1] = 2 }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0531, ExpressionDesugarSeverity.Error));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    // ------------------------------------------------------------------
    // M2: Add variants (IN-7).
    // ------------------------------------------------------------------

    private const string M2AddShapes = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        public class Pairs : IEnumerable { public void Add(int a, int b) { } public IEnumerator GetEnumerator() => null!; }
        public class FluentBag : IEnumerable { public FluentBag Add(int x) => this; public IEnumerator GetEnumerator() => null!; }
        public class ParamsBag : IEnumerable { public void Add(params int[] xs) { } public IEnumerator GetEnumerator() => null!; }
        public class GenBag : IEnumerable<int> { public IEnumerator<int> GetEnumerator() => null!; IEnumerator IEnumerable.GetEnumerator() => GetEnumerator(); }
        public static class GenExt { public static void Add(this GenBag b, int x) { } }
        public class C
        {
            public void M()
            {
                var p = new Pairs { { 1, 2 } };
                var f = new FluentBag { 1 };
                var pa = new ParamsBag { 1, 2, 3 };
                var g = new GenBag { 1, 2 };
            }
        }
        """;

    [Fact]
    public void M2_AddVariants_MultiExtensionParamsFluent_Classified()
    {
        var result = Preprocess(M2AddShapes);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.Contains("__ed_0.Add(1, 2);", normalized);   // multi-arg, never put(...)
        Assert.DoesNotContain("put", normalized);
        Assert.Contains("__ed_1.Add(1);", normalized);       // fluent, value discarded
        Assert.Contains("__ed_2.Add(1);", normalized);       // params: one call per element
        Assert.Contains("__ed_2.Add(2);", normalized);
        Assert.Contains("__ed_2.Add(3);", normalized);
        Assert.Contains("__ed_3.Add(1);", normalized);       // extension reduced re-emission
        Assert.Contains("__ed_3.Add(2);", normalized);

        var init = result.Statistics.Initializers;
        Assert.Equal(1, init.AddMulti);
        Assert.Equal(3, init.AddParams);
        Assert.Equal(1, init.AddFluent);
        Assert.Equal(2, init.AddExtension);
        Assert.Equal(1, init.AddSingle);   // fluent single-arg instance Add
        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0539, ExpressionDesugarSeverity.Info));
    }

    // ------------------------------------------------------------------
    // M2: condition guards (S6).
    // ------------------------------------------------------------------

    [Fact]
    public void M2_Ternary_GuardNotHoisted_BothBranchesAssign()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static int F() => 1;
                public static void M(bool cond) { var a = cond ? new Dto { A = F() } : null; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.Contains("global::Dto __ed_0;", normalized);
        Assert.Contains("if (cond)", normalized);
        Assert.True(
            IndexOf(normalized, "if (cond)")
            < IndexOf(normalized, "var __ed_1 = new Dto();")
            && IndexOf(normalized, "var __ed_1 = new Dto();")
            < IndexOf(normalized, "else")
            && IndexOf(normalized, "else") < IndexOf(normalized, "__ed_0 = null;"),
            normalized);
    }

    [Fact]
    public void M2_NullCoalescing_GuardNotHoisted()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static int F() => 1;
                public static void M(Dto x) { var a = x ?? new Dto { A = F() }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.Contains("var __ed_0 = x;", normalized);
        Assert.Contains("if (__ed_0 == null)", normalized);
        Assert.True(
            IndexOf(normalized, "if (__ed_0 == null)")
            < IndexOf(normalized, "var __ed_1 = new Dto();"), normalized);
    }

    // ------------------------------------------------------------------
    // M2: argument interleaving (IN-11).
    // ------------------------------------------------------------------

    [Fact]
    public void M2_Argument_HoistsPrecedingSideEffectArguments()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static int F() => 1;
                public static int H() => 2;
                public static void M(int a, Dto d, int h) { }
                public static void M2() { M(F(), new Dto { A = F() }, H()); }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.True(
            IndexOf(normalized, "var __ed_0 = F();")
            < IndexOf(normalized, "var __ed_1 = new Dto();")
            && IndexOf(normalized, "var __ed_1 = new Dto();")
            < IndexOf(normalized, "M(__ed_0, __ed_1, H());"), normalized);
    }

    [Fact]
    public void M2_Argument_NoSideEffectPreceding_NotHoisted()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M(int a, Dto d) { }
                public static void M2() { int x = 1; M(x, new Dto { A = 2 }); }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.Contains("M(x, __ed_0", normalized);
        Assert.DoesNotContain("var __ed_0 = x", normalized);
    }

    // ------------------------------------------------------------------
    // M2: statement-lowered target-typed new + return/yield/throw/using.
    // ------------------------------------------------------------------

    [Fact]
    public void M2_ReturnYieldThrowUsing_StatementLowered()
    {
        const string source = """
            using System.Collections;
            using System.Collections.Generic;
            public class Bag : IEnumerable<int>, System.IDisposable
            {
                public void Add(int x) { }
                public IEnumerator<int> GetEnumerator() => null!;
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                public void Dispose() { }
            }
            public static class C
            {
                public static List<int> R() { return new List<int> { 1, 2 }; }
                public static void M() { using var x = new Bag { 3 }; }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);

        var normalized = Normalize(result.OutputCode);
        Assert.True(
            IndexOf(normalized, "var __ed_0 = new List<int>();")
            < IndexOf(normalized, "__ed_0.Add(1);")
            && IndexOf(normalized, "__ed_0.Add(2);")
            < IndexOf(normalized, "return __ed_0;"), normalized);
        Assert.Contains("using var x = __ed_1;", normalized);
        Assert.Contains("__ed_1.Add(3);", normalized);
    }

    // ------------------------------------------------------------------
    // M2: unsupported / skip family.
    // ------------------------------------------------------------------

    [Fact]
    public void M2_ConstructorChainArgument_CS2J0537_AtomicUnchanged()
    {
        const string source = """
            public class Dto
            {
                public int A { get; set; }
                public Dto() { }
                public Dto(Dto other) { }
            }
            public class Sub : Dto
            {
                public Sub() : base(new Dto { A = 1 }) { }
            }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0537, ExpressionDesugarSeverity.Error));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void M2_FieldInitializerWithStatements_CS2J0538_AtomicUnchanged()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public class Holder { private readonly Dto _d = new Dto { A = 1 }; }
            """;

        var result = Preprocess(source);

        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0538, ExpressionDesugarSeverity.Error));
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void M2_NonStrict_UnsupportedSitePreserved_SiblingsRewrite()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public class Holder { private readonly Dto _d = new Dto { A = 1 }; }
            public static class C
            {
                public static void M() { var d = new Dto { A = 2 }; }
            }
            """;

        var result = Preprocess(source, new ExpressionDesugarOptions
        {
            Initializers = new InitializerOptions { Strict = false },
        });

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.True(HasDiagnostic(
            result, InitializerDiagnosticCodes.CS2J0538, ExpressionDesugarSeverity.Warning));
        Assert.Contains("new Dto { A = 1 }", Normalize(result.OutputCode));
        Assert.Contains("__ed_0.A = 2", Normalize(result.OutputCode));
    }

    [Fact]
    public void M2_M2Shapes_CompileAndIdempotent()
    {
        const string source = """
            using System.Collections;
            using System.Collections.Generic;
            public class I { public int D { get; set; } }
            public class O { public I Inner { get; set; } = new I(); }
            public class Pairs : IEnumerable { public void Add(int a, int b) { } public IEnumerator GetEnumerator() => null!; }
            public static class C
            {
                public static void M()
                {
                    var a = new O { Inner = new I { D = 1 } };
                    var b = new O { Inner = { D = 2 } };
                    Dictionary<int, string> d = new() { [1] = "x" };
                    var p = new Pairs { { 1, 2 } };
                }
            }
            """;

        var first = Preprocess(source);
        Assert.True(first.Changed, string.Join("; ", first.Diagnostics));
        AssertOutputCompiles(first.OutputCode);
        Assert.DoesNotContain("new I { D = 1 }", Normalize(first.OutputCode));
        Assert.DoesNotContain("{ D = 2 }", Normalize(first.OutputCode));
        Assert.DoesNotContain("{ [1] = \"x\" }", Normalize(first.OutputCode));
        Assert.DoesNotContain("{ { 1, 2 } }", Normalize(first.OutputCode));

        var second = Preprocess(first.OutputCode);
        Assert.False(second.Changed, string.Join("; ", second.Diagnostics));
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    // ------------------------------------------------------------------
    // M3: IN-13 residual gate, coexistence matrix, idempotency/BOM.
    // ------------------------------------------------------------------

    [Fact]
    public void M3_ResidualGate_DeferredOnlyFile_NoCs2j0545()
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

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        // Whole-file C36-coexistence deferral: C24 rewrote nothing and the
        // deferred residual nodes are paired by count, so no CS2J0545.
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == InitializerDiagnosticCodes.CS2J0545);
        var init = result.Statistics.Initializers;
        Assert.Equal(0, init.ResidualInitializerCount);
        Assert.Equal(1, init.CoexistenceDeferredSites);
        Assert.Equal(1, init.DeferredResidualNodes);
        Assert.Equal(0, init.PreservedResidualNodes);
    }

    [Fact]
    public void M3_ResidualGate_SyntheticUnpairedResidual_StrictError_NonStrictWarning()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C { public static void M() { var d = new Dto { A = 1 }; } }
            """;
        var (_, model) = Compile(source);
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        // Synthetic unpaired residual: the plan exists but the root was never
        // rewritten, so the residual node carries no explained annotation. This
        // is exactly the CS2J0545 condition the gate exists to catch.
        var strictStats = new ExpressionDesugarStatistics();
        var strictDiags = new List<ExpressionDesugarDiagnostic>();
        var strictCtx = BuildContext(root, model, C24Enabled, strictStats, strictDiags);
        var strictPlan = new InitializersRule().Analyze(strictCtx);
        var strictUnpaired = strictPlan.CheckResiduals(strictCtx, root);

        Assert.True(strictUnpaired > 0);
        Assert.Equal(strictUnpaired, strictStats.Initializers.ResidualInitializerCount);
        Assert.Contains(strictDiags, d =>
            d.Code == InitializerDiagnosticCodes.CS2J0545
            && d.Severity == ExpressionDesugarSeverity.Error);

        // Non-strict: the same leftover degrades to a Warning and the node is
        // kept verbatim (no rewrite happened).
        var looseOptions = new ExpressionDesugarOptions
        {
            Initializers = new InitializerOptions { Strict = false },
        };
        var looseStats = new ExpressionDesugarStatistics();
        var looseDiags = new List<ExpressionDesugarDiagnostic>();
        var looseCtx = BuildContext(root, model, looseOptions, looseStats, looseDiags);
        var loosePlan = new InitializersRule().Analyze(looseCtx);
        var looseUnpaired = loosePlan.CheckResiduals(looseCtx, root);

        Assert.Equal(strictUnpaired, looseUnpaired);
        Assert.Contains(looseDiags, d =>
            d.Code == InitializerDiagnosticCodes.CS2J0545
            && d.Severity == ExpressionDesugarSeverity.Warning);
        Assert.Contains(root.DescendantNodes(),
            n => n is InitializerExpressionSyntax i
                 && i.IsKind(SyntaxKind.ObjectInitializerExpression));
    }

    [Fact]
    public void M3_ResidualGate_InertWithoutOptionsSubsection_NoCs2j0545()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C { public static void M() { var d = new Dto { A = 1 }; } }
            """;
        var (_, model) = Compile(source);
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        // Without the Initializers subsection the rule must not police residual
        // syntax at all (the default chain emits C36 collection-initializer
        // forms that C24 does not own).
        var stats = new ExpressionDesugarStatistics();
        var diags = new List<ExpressionDesugarDiagnostic>();
        var ctx = BuildContext(root, model, new ExpressionDesugarOptions(), stats, diags);
        var plan = new InitializersRule().Analyze(ctx);

        Assert.Equal(0, plan.CheckResiduals(ctx, root));
        Assert.DoesNotContain(diags, d => d.Code == InitializerDiagnosticCodes.CS2J0545);
    }

    private static RuleAnalysisContext BuildContext(
        CompilationUnitSyntax root,
        SemanticModel model,
        ExpressionDesugarOptions options,
        ExpressionDesugarStatistics statistics,
        List<ExpressionDesugarDiagnostic> diagnostics)
        => new()
        {
            Root = root,
            SemanticModel = model,
            Options = options,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };

    [Fact]
    public void M3_ArrayInitializerAndAnonymousObject_ResidualExcluded()
    {
        const string source = """
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    var d = new Dto { A = 1 };
                    int[] a = new int[] { 1, 2 };
                    int[] b = new[] { 3 };
                    var anon = new { A = 1 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == InitializerDiagnosticCodes.CS2J0545);
        Assert.Equal(0, result.Statistics.Initializers.ResidualInitializerCount);
        Assert.Equal(0, result.Statistics.Initializers.PreservedResidualNodes);

        // Array initializer expressions and anonymous objects are never touched
        // (decision 15 excludes them from the residual口径).
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("new int[] { 1, 2 }", normalized);
        Assert.Contains("new[] { 3 }", normalized);
        Assert.Contains("new { A = 1 }", normalized);
    }

    [Fact]
    public void M3_C11ProductTreeInput_PlainSetMethodsAndFields_WorksNormally()
    {
        // Hand-written equivalent of member-desugar (C11/C27) output: properties
        // become plain set_X methods plus backing fields. C24 must classify the
        // residual general shapes by the runtime-tree symbols and rewrite them
        // normally (no CS2J0531 for the writable field/member).
        const string source = """
            using System.Collections.Generic;
            public class C11Dto
            {
                private int _p;
                public int F;
                public int get_P() => _p;
                public void set_P(int v) { _p = v; }
            }
            public static class C
            {
                public static void M()
                {
                    var d = new C11Dto { F = 1 };
                    var l = new List<int> { 2, 3 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == InitializerDiagnosticCodes.CS2J0531);
        var normalized = Normalize(result.OutputCode);
        Assert.Contains("__ed_0.F = 1", normalized);
        Assert.Contains(".Add(2)", normalized);
        Assert.Contains(".Add(3)", normalized);
    }

    [Fact]
    public void M3_Coexistence_C36Rewrites_C24Defers_NoDamage_SecondRunConverges()
    {
        // C24 + C36 coexist in one file: C36 lowers its collection expression
        // (including an emitted collection-initializer form), C24 defers whole
        // and reports no CS2J0545. The C36 face is gone on the next run, so C24
        // converges; a third run is byte-stable (no numbering/annotation clash).
        const string source = """
            using System.Collections.Generic;
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    List<int> xs = [1, 2];
                    var d = new Dto { A = 1 };
                }
            }
            """;

        var first = Preprocess(source);

        Assert.True(first.Changed, string.Join("; ", first.Diagnostics));
        AssertOutputCompiles(first.OutputCode);
        Assert.DoesNotContain(
            first.Diagnostics, d => d.Code == InitializerDiagnosticCodes.CS2J0545);
        Assert.Equal(0, first.Statistics.Initializers.ResidualInitializerCount);
        Assert.True(first.Statistics.Initializers.CoexistenceDeferredSites >= 1);
        // C24 site stayed verbatim while C36 rewrote its own syntax.
        Assert.Contains("new Dto { A = 1 }", Normalize(first.OutputCode));
        Assert.DoesNotContain("[1, 2]", Normalize(first.OutputCode));

        var second = Preprocess(first.OutputCode);
        Assert.True(second.Changed, string.Join("; ", second.Diagnostics));
        AssertOutputCompiles(second.OutputCode);
        Assert.DoesNotContain(
            second.Diagnostics, d => d.Code == InitializerDiagnosticCodes.CS2J0545);
        Assert.DoesNotContain("new Dto { A = 1 }", Normalize(second.OutputCode));

        var third = Preprocess(second.OutputCode);
        Assert.False(third.Changed, string.Join("; ", third.Diagnostics));
        Assert.Equal(second.OutputCode, third.OutputCode);
    }

    [Fact]
    public void M3_DirtyFileWithBom_SecondRunByteStable()
    {
        const string source =
            "\uFEFFpublic class Dto { public int A { get; set; } }\n" +
            "public static class C { public static void M() { var d = new Dto { A = 1 }; } }\n";

        var first = Preprocess(source);
        Assert.True(first.Changed, string.Join("; ", first.Diagnostics));
        AssertOutputCompiles(first.OutputCode);

        var second = Preprocess(first.OutputCode);
        Assert.False(second.Changed, string.Join("; ", second.Diagnostics));
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void M3_ResidualGate_NoListOfAndNoLinqEmission()
    {
        const string source = """
            using System.Collections.Generic;
            public class Dto { public int A { get; set; } }
            public static class C
            {
                public static void M()
                {
                    var d = new Dto { A = 1 };
                    var l = new List<int>(4) { 1, 2 };
                }
            }
            """;

        var result = Preprocess(source);

        Assert.True(result.Changed, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("List.of", result.OutputCode);
        Assert.DoesNotContain("System.Linq", result.OutputCode);
    }
}