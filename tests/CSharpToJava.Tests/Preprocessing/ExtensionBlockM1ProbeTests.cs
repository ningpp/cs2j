using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C49 M1 supplementary probes against Roslyn 5.0.0 GA (pinned facts that the
/// M1 implementation consumes; conclusions are mirrored into
/// docs/superpowers/specs/2026-09-29-c49-extension-members-design.md,
/// "M1 探针结论（2026-10-04）"):
///
/// ① An instance property inside an extension block is a normal
///    IPropertySymbol whose ContainingType is the synthetic
///    TypeKind.Extension container; its get/set accessors are Ordinary,
///    IsStatic=false, IsExtensionMethod=false. The declaration's first
///    ancestor is ExtensionBlockDeclarationSyntax.
/// ② Every call-site shape (read, write, compound, ++/--, chained,
///    null-conditional) binds directly to that IPropertySymbol (read) or its
///    accessor (write); nothing is ReducedExtension.
/// ③ Block instance method call sites (including under generic blocks) bind
///    directly to the Ordinary method inside the TypeKind.Extension container.
/// ④ C#15 extension indexers parse (preview only) as IndexerDeclarationSyntax
///    inside the block; under Latest they are syntax errors — the rejection
///    (CS2J1209) is driven by SyntaxKind + node classification.
/// </summary>
public sealed class ExtensionBlockM1ProbeTests
{
    private readonly ITestOutputHelper _output;

    public ExtensionBlockM1ProbeTests(ITestOutputHelper output) => _output = output;

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

    private static (CSharpCompilation Compilation, SemanticModel Model, SyntaxTree Tree) Compile(
        string source, LanguageVersion version = LanguageVersion.Latest, string name = "m1probe")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(version), path: name + ".cs");
        var compilation = CSharpCompilation.Create(
            name + "-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private void AssertZeroErrors(CSharpCompilation compilation, LanguageVersion version)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            $"[{version}] expected 0 errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    private static void DescribeAccessor(ITestOutputHelper output, IMethodSymbol? accessor, string label)
    {
        if (accessor is null)
        {
            output.WriteLine($"    {label}: <null>");
            return;
        }

        output.WriteLine(
            $"    {label}: name={accessor.Name}, methodKind={accessor.MethodKind}, " +
            $"isStatic={accessor.IsStatic}, isExtension={accessor.IsExtensionMethod}, " +
            $"reducedFrom={accessor.ReducedFrom?.ToDisplayString() ?? "<null>"}, " +
            $"container={accessor.ContainingType.ToDisplayString()} " +
            $"(typeKind={accessor.ContainingType.TypeKind}), " +
            $"returnType={accessor.ReturnType.ToDisplayString()}, " +
            $"parameters=[{string.Join(", ", accessor.Parameters.Select(p => p.Type.ToDisplayString()))}]");
    }

    // ── Probe ①: property symbol + accessor shape ───────────────────────────

    [Fact]
    public void Probe1_InstanceProperty_DeclaredSymbolShape()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int P
                        {
                            get { return v; }
                            set { var x = v + 1; }
                        }
                    }
                }
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, model, tree) = Compile(source, version);
            AssertZeroErrors(compilation, version);

            var propertyNode = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<PropertyDeclarationSyntax>().Single();

            var blockAncestor = propertyNode.FirstAncestorOrSelf<ExtensionBlockDeclarationSyntax>();
            Assert.NotNull(blockAncestor);
            _output.WriteLine($"[{version}] property first ExtensionBlock ancestor: present, " +
                $"block kind={(SyntaxKind)blockAncestor!.RawKind}, type={blockAncestor.GetType().FullName}");

            var property = Assert.IsAssignableFrom<IPropertySymbol>(
                model.GetDeclaredSymbol(propertyNode));
            _output.WriteLine(
                $"[{version}] property: name={property.Name}, metadataName={property.MetadataName}, " +
                $"isStatic={property.IsStatic}, isAbstract={property.IsAbstract}, isVirtual={property.IsVirtual}, " +
                $"isOverride={property.IsOverride}, isIndexer={property.IsIndexer}, " +
                $"type={property.Type.ToDisplayString()}, " +
                $"container={property.ContainingType.ToDisplayString()} " +
                $"(typeKind={property.ContainingType.TypeKind}), " +
                $"refKind={property.RefKind}, returnsByRef={property.ReturnsByRef}");

            Assert.False(property.IsStatic);
            Assert.False(property.IsIndexer);
            Assert.Equal(TypeKind.Extension, property.ContainingType.TypeKind);
            Assert.Equal("int", property.Type.ToDisplayString());

            DescribeAccessor(_output, property.GetMethod, "get");
            DescribeAccessor(_output, property.SetMethod, "set");

            Assert.NotNull(property.GetMethod);
            Assert.NotNull(property.SetMethod);
            Assert.Equal("get_P", property.GetMethod!.Name);
            Assert.Equal("set_P", property.SetMethod!.Name);
            Assert.Equal(MethodKind.PropertyGet, property.GetMethod.MethodKind);
            Assert.Equal(MethodKind.PropertySet, property.SetMethod.MethodKind);
            Assert.False(property.GetMethod.IsStatic);
            Assert.False(property.SetMethod.IsStatic);
            Assert.Equal(TypeKind.Extension, property.GetMethod.ContainingType.TypeKind);
        }
    }

    // ── Probe ②: call-site shapes ───────────────────────────────────────────

    private const string CallSitesSource = """
        namespace N
        {
            public static class Holder
            {
                extension(int v)
                {
                    public int P
                    {
                        get { return v; }
                        set { }
                    }
                }

                public class Bag { public int B; }
            }

            public class Site
            {
                public static int Read() => 3.P;
            }
        }
        """;

    [Fact]
    public void Probe2_PropertyCallSites_BindToExtensionProperty()
    {
        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, model, tree) = Compile(CallSitesSource, version);
            AssertZeroErrors(compilation, version);
            var root = tree.GetCompilationUnitRoot();

            // Read: the member access 3.P binds the property symbol directly.
            var readMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == "Read");
            var readAccess = readMethod.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Single(a => a.Name.Identifier.Text == "P");
            var readSymbol = model.GetSymbolInfo(readAccess).Symbol;
            _output.WriteLine($"[{version}] read 3.P -> {readSymbol?.Kind} {readSymbol?.ToDisplayString()} " +
                $"(container={(readSymbol as IPropertySymbol)?.ContainingType.ToDisplayString()})");
            var readProperty = Assert.IsAssignableFrom<IPropertySymbol>(readSymbol);
            Assert.Equal(TypeKind.Extension, readProperty.ContainingType.TypeKind);
        }
    }

    // ── Probe ②d: property write shapes across receiver forms ─────────────

    [Fact]
    public void Probe2d_PropertyWriteShapes_ReceiverMatrix()
    {
        const string source = """
            namespace N
            {
                public class Cc { }

                public static class Holder
                {
                    extension(int v)
                    {
                        public int P { get { return v; } set { } }
                    }

                    extension(Cc c)
                    {
                        public int Q { get { return 0; } set { } }
                    }
                }

                public class Site
                {
                    public static void A() { int x = 0; __SITE__; }
                    public static void B() { Cc c = new Cc(); __SITE2__; }
                }
            }
            """;

        // Struct-by-value receiver writes: which forms are legal?
        var intSites = new (string Label, string Site)[]
        {
            ("int literal write", "3.P = 1"),
            ("int lvalue write", "x.P = 1"),
            ("int lvalue compound", "x.P += 1"),
            ("int lvalue inc", "x.P++"),
            ("int literal compound", "3.P += 1"),
        };

        foreach (var (label, site) in intSites)
        {
            var text = source.Replace("__SITE__", site).Replace("__SITE2__", "");
            var (compilation, model, tree) = Compile(text);
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id).Distinct().ToList();
            _output.WriteLine($"[{label}] -> {(errors.Count == 0 ? "OK" : string.Join(",", errors))}");

            if (errors.Count == 0)
            {
                // Pin the bound left-side symbol the analyzer selects.
                var statement = tree.GetCompilationUnitRoot().DescendantNodes()
                    .OfType<ExpressionStatementSyntax>()
                    .First(s => s.ToString().Contains(".P"));
                var target = statement.Expression switch
                {
                    AssignmentExpressionSyntax { Left: var l } => l,
                    PostfixUnaryExpressionSyntax { Operand: var o } => o,
                    _ => statement.Expression,
                };
                var symbol = model.GetSymbolInfo(target).Symbol;
                _output.WriteLine($"    bound target -> {symbol?.Kind} " +
                    $"{(symbol as IPropertySymbol)?.ToDisplayString()}");
                var property = Assert.IsAssignableFrom<IPropertySymbol>(symbol);
                Assert.Equal(TypeKind.Extension, property.ContainingType.TypeKind);
            }
        }

        // Class receiver writes.
        var classSites = new (string Label, string Site)[]
        {
            ("class lvalue write", "c.Q = 1"),
            ("class rvalue write", "new Cc().Q = 1"),
            ("class lvalue compound", "c.Q += 1"),
        };

        foreach (var (label, site) in classSites)
        {
            var text = source.Replace("__SITE2__", site).Replace("__SITE__", "");
            var (compilation, _, _) = Compile(text);
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id).Distinct().ToList();
            _output.WriteLine($"[{label}] -> {(errors.Count == 0 ? "OK" : string.Join(",", errors))}");
        }

        Assert.True(true);
    }

    // ── Probe: chained access x.ExtProp.B ──────────────────────────────────

    [Fact]
    public void Probe2b_ChainedAccess_BothSitesBind()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    public class Bag { public int B; }

                    extension(int value)
                    {
                        public Bag ExtProp => new Bag();
                    }
                }

                public class Site
                {
                    public static int Run() => 3.ExtProp.B;
                }
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, model, tree) = Compile(source, version);
            AssertZeroErrors(compilation, version);

            var accesses = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>().ToList();
            foreach (var access in accesses)
            {
                var symbol = model.GetSymbolInfo(access).Symbol;
                _output.WriteLine($"[{version}] access {access} -> {symbol?.Kind} " +
                    $"{symbol?.ToDisplayString()}");
            }

            var extAccess = accesses.Single(a => a.Name.Identifier.Text == "ExtProp");
            var extProperty = Assert.IsAssignableFrom<IPropertySymbol>(
                model.GetSymbolInfo(extAccess).Symbol);
            Assert.Equal(TypeKind.Extension, extProperty.ContainingType.TypeKind);
        }
    }

    // ── Probe: null-conditional r?.ExtProp ─────────────────────────────────

    [Fact]
    public void Probe2c_NullConditionalProperty_TreeAndBinding()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension(List<int> items)
                    {
                        public int ExtProp => items.Count;
                    }
                }

                public class Site
                {
                    public static int? Run(List<int> r) => r?.ExtProp;
                }
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, model, tree) = Compile(source, version);
            AssertZeroErrors(compilation, version);

            var conditional = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<ConditionalAccessExpressionSyntax>().Single();
            _output.WriteLine($"[{version}] conditional: expr={conditional.Expression}, " +
                $"whenNotNull={conditional.WhenNotNull} (type={conditional.WhenNotNull.GetType().Name})");

            // WhenNotNull is a MemberBinding .ExtProp; its symbol is the property.
            var binding = Assert.IsType<MemberBindingExpressionSyntax>(conditional.WhenNotNull);
            var property = Assert.IsAssignableFrom<IPropertySymbol>(
                model.GetSymbolInfo(binding).Symbol);
            _output.WriteLine($"[{version}] member binding -> {property.ToDisplayString()} " +
                $"(container={property.ContainingType.ToDisplayString()}, typeKind={property.ContainingType.TypeKind})");
            Assert.Equal(TypeKind.Extension, property.ContainingType.TypeKind);

            var conditionalType = model.GetTypeInfo(conditional).Type;
            _output.WriteLine($"[{version}] conditional type: {conditionalType?.ToDisplayString()}");
        }
    }

    // ── Probe ③: generic block method call site ────────────────────────────

    [Fact]
    public void Probe3_GenericBlock_MethodCallBindsOrdinaryInExtensionContainer()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<T>(List<T> items)
                    {
                        public int Count2() => items.Count;
                        public U Map<U>(System.Func<T,U> f) => f(items[0]);
                    }
                }

                public class Site
                {
                    public static int Run(List<int> xs) => xs.Count2();
                    public static string Run2(List<int> xs) => xs.Map(x => x.ToString());
                }
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, model, tree) = Compile(source, version);
            AssertZeroErrors(compilation, version);

            var invocations = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax).ToList();
            foreach (var invocation in invocations)
            {
                var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                _output.WriteLine(
                    $"[{version}] call {invocation} -> methodKind={symbol?.MethodKind}, " +
                    $"isExtension={symbol?.IsExtensionMethod}, " +
                    $"reducedFrom={symbol?.ReducedFrom?.ToDisplayString() ?? "<null>"}, " +
                    $"container={symbol?.ContainingType.ToDisplayString()} " +
                    $"(typeKind={symbol?.ContainingType.TypeKind}), " +
                    $"arity={symbol?.Arity}, typeArgs=[{string.Join(", ", symbol?.TypeArguments.Select(t => t.ToDisplayString()) ?? new[] { "" })}]");
            }

            // Block-member call sites only (excludes e.g. x.ToString() inside lambdas).
            foreach (var invocation in invocations)
            {
                var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol?.ContainingType.TypeKind != TypeKind.Extension)
                {
                    continue;
                }

                Assert.Equal(MethodKind.Ordinary, symbol.MethodKind);
                Assert.Null(symbol.ReducedFrom);
                Assert.False(symbol.IsExtensionMethod);
            }

            // Declared method shape.
            var countMethod = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Select(m => model.GetDeclaredSymbol(m))
                .Single(s => s is { Name: "Count2" });
            _output.WriteLine($"[{version}] declared Count2: kind={countMethod!.MethodKind}, " +
                $"container={countMethod.ContainingType.ToDisplayString()}, " +
                $"parameters=[{string.Join(", ", countMethod.Parameters.Select(p => p.Type.ToDisplayString()))}], " +
                $"arity={countMethod.Arity}");

            // Block shape: type parameter list on the block node.
            var block = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<ExtensionBlockDeclarationSyntax>().Single();
            _output.WriteLine($"[{version}] block TypeParameterList={block.TypeParameterList}, " +
                $"ParameterList={block.ParameterList}, " +
                $"ConstraintClauses=[{string.Join(", ", block.ConstraintClauses)}], " +
                $"members={block.Members.Count}");
            Assert.NotNull(block.TypeParameterList);
            Assert.Single(block.TypeParameterList!.Parameters);
        }
    }

    // ── Probe: explicit type arguments at a block method site ──────────────

    [Fact]
    public void Probe3b_ExplicitTypeArguments_ShapeMatrix()
    {
        // Generic block (arity n=1) + member type parameter (m=1); the legal
        // explicit-argument spelling is pinned for each shape.
        const string genericBlock = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<T>(List<T> items)
                    {
                        public U Echo<U>(U u) => u;
                        public int Count2() => items.Count;
                    }
                }

                public class Site
                {
                    public static void A(List<int> xs) { __SITE__; }
                }
            }
            """;

        var shapes = new (string Label, string Site, bool ExpectClean)[]
        {
            ("inferred", "xs.Echo(\"x\")", true),
            ("explicit-full-n+m", "xs.Echo<int, string>(\"x\")", true),
            ("explicit-member-only", "xs.Echo<string>(\"x\")", false),
        };

        foreach (var (label, site, expectClean) in shapes)
        {
            var source = genericBlock.Replace("__SITE__", site);
            var (compilation, _, tree) = Compile(source, name: "matrix");
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _output.WriteLine($"[{label}] errors: " +
                (errors.Count == 0 ? "(none)"
                    : string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage().Split('\n')[0]))));
            Assert.Equal(expectClean, errors.Count == 0);

            if (errors.Count == 0)
            {
                var invocation = tree.GetCompilationUnitRoot().DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Single(i => i.ToString().Contains("Echo"));
                var symbol = compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                _output.WriteLine($"    bound: arity={symbol?.Arity}, " +
                    $"typeArgs=[{string.Join(", ", symbol?.TypeArguments.Select(t => t.ToDisplayString()) ?? new[] { "" })}]");
            }
        }

        // Non-generic block + member-generic method: one explicit arg, as usual.
        const string nonGenericBlock = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public U Echo<U>(U u) => u;
                    }
                }

                public class Site
                {
                    public static string Run() => 3.Echo<string>("x");
                }
            }
            """;

        var (compilation2, model2, tree2) = Compile(nonGenericBlock);
        AssertZeroErrors(compilation2, LanguageVersion.Latest);

        var invocation2 = tree2.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single();
        var genericName = Assert.IsType<GenericNameSyntax>(
            ((MemberAccessExpressionSyntax)invocation2.Expression).Name);
        Assert.Single(genericName.TypeArgumentList.Arguments);
        var symbol2 = model2.GetSymbolInfo(invocation2).Symbol as IMethodSymbol;
        Assert.Equal(1, symbol2!.Arity);
        _output.WriteLine($"[non-generic block] explicit single arg binds: arity={symbol2.Arity}, " +
            $"typeArgs=[{string.Join(", ", symbol2.TypeArguments.Select(t => t.ToDisplayString()))}]");
    }

    // ── Probe ④: C#15 extension indexer ───────────────────────────────────

    /// <summary>
    /// Roslyn 5.0.0 pin: extension indexers PARSE (as IndexerDeclarationSyntax
    /// inside the block) under Latest and Preview, but are rejected at compile
    /// with CS9282 under BOTH — the feature lands in a later Roslyn line. C49
    /// therefore always rejects block indexers with CS2J1209 regardless of the
    /// incoming language version.
    /// </summary>
    [Fact]
    public void Probe4_ExtensionIndexer_AlwaysRejectedInRoslyn50()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int this[int index] => v + index;
                    }
                }
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                source, new CSharpParseOptions(version));
            var parseErrors = tree.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.Empty(parseErrors);

            var indexerNode = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<IndexerDeclarationSyntax>().SingleOrDefault();
            Assert.NotNull(indexerNode);
            Assert.NotNull(indexerNode!.FirstAncestorOrSelf<ExtensionBlockDeclarationSyntax>());

            var compilation = CSharpCompilation.Create(
                "idx-" + Guid.NewGuid().ToString("N"),
                new[] { tree }, References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var compileErrors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _output.WriteLine($"[{version}] parse OK, compile: " +
                string.Join("; ", compileErrors.Select(d => d.Id)));
            Assert.Contains(compileErrors, d => d.Id == "CS9282");
        }
    }

    // ── Probe: expression-bodied / get-only / (banned init) property shapes ─

    [Fact]
    public void Probe5_PropertyVariants_AccessorShape()
    {
        // init is NOT a legal extension-block member (CS9304); auto-properties
        // (no place for a backing field) are CS9282. Legal variants are
        // expression-bodied get-only and block-bodied get/set.
        const string legalSource = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int GetOnly => v;
                        public int BlockGet { get { return v; } }
                        public int BlockSet { get { return v; } set { } }
                    }
                }
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, model, tree) = Compile(legalSource, version);
            AssertZeroErrors(compilation, version);

            foreach (var node in tree.GetCompilationUnitRoot().DescendantNodes()
                         .OfType<PropertyDeclarationSyntax>())
            {
                var property = Assert.IsAssignableFrom<IPropertySymbol>(
                    model.GetDeclaredSymbol(node));
                _output.WriteLine(
                    $"[{version}] {property.Name}: get={property.GetMethod != null}, " +
                    $"set={property.SetMethod != null}, " +
                    $"getKind={property.GetMethod?.MethodKind.ToString() ?? "<null>"}/" +
                    $"{property.SetMethod?.MethodKind.ToString() ?? "<null>"}");
                Assert.NotNull(property.GetMethod);
                Assert.Equal(TypeKind.Extension, property.ContainingType.TypeKind);
            }
        }

        const string initSource = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int Init { get; init; }
                    }
                }
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, _, _) = Compile(initSource, version);
            var ids = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id).ToList();
            _output.WriteLine($"[{version}] init diagnostics: {string.Join(", ", ids)}");
            // Pinned: init is rejected under both versions; CS9304 is the gate.
            Assert.Contains("CS9304", ids);
        }
    }

    // ── Probe: receiver named 'value' conflicts with setter parameter ──────

    [Fact]
    public void Probe6_ReceiverNamedValue_ConflictsWithSetter()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int value)
                    {
                        public int P { get; set; }
                    }
                }
            }
            """;

        var (compilation, _, _) = Compile(source);
        var ids = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id).ToList();
        _output.WriteLine($"receiver='value' diagnostics: {string.Join(", ", ids)}");
        Assert.Contains("CS9291", ids);
    }
}
