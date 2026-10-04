using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit.Abstractions;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C49 M2 probes against Roslyn 5.0.0 GA (pinned facts for M2 rules;
/// conclusions are mirrored into
/// docs/superpowers/specs/2026-09-29-c49-extension-members-design.md,
/// "M2 探针结论"):
///
/// ① Operator declarations inside an extension block: IMethodSymbol with
///   MethodKind UserDefinedOperator/Conversion, ContainingType shape.
/// ② Four operator use-site kinds: binary/unary/conversion/compound-assignment.
/// ③ Static method/property declaration symbols inside blocks.
/// ④ "Receiver type-name qualified" call sites (IEnumerable&lt;int&gt;.Generate /
///   .Identity): the MemberAccess left side is a type symbol; the exact
///   IsExtension API name; explicit generic argument forms.
/// ⑤ Inference boundary (design §4.4-4): block parameters appearing only in
///   the receiver type.
/// ⑥ Receiver modifier matrix ref/in/readonly ref/scoped × member kinds;
///   pointer receiver; setter ref first parameter.
/// </summary>
public sealed class ExtensionBlockM2ProbeTests
{
    private readonly ITestOutputHelper _output;

    public ExtensionBlockM2ProbeTests(ITestOutputHelper output) => _output = output;

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static CSharpCompilationOptions Options(bool allowUnsafe = false)
        => new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe,
            nullableContextOptions: NullableContextOptions.Enable);

    private static (CSharpCompilation Compilation, SemanticModel Model, SyntaxTree Tree) Compile(
        string source, LanguageVersion version = LanguageVersion.Latest,
        string name = "m2probe", bool allowUnsafe = false)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(version), path: name + ".cs");
        var compilation = CSharpCompilation.Create(
            name + "-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            Options(allowUnsafe));
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private void AssertZeroErrors(CSharpCompilation compilation, LanguageVersion version)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            $"[{version}] expected 0 errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    private static bool IsDeclaredInBlock(IMethodSymbol method)
        => method.DeclaringSyntaxReferences
            .Any(r => r.GetSyntax()?.FirstAncestorOrSelf<ExtensionBlockDeclarationSyntax>() is not null);

    private static void DumpBoolProperties(object target, ITestOutputHelper output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var type = target.GetType();
        var count = 0;
        while (type is not null)
        {
            foreach (var property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(p => p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0))
            {
                if (!seen.Add(property.Name))
                {
                    continue;
                }

                count++;
                try
                {
                    output.WriteLine($"      bool {property.Name} = {property.GetValue(target)}");
                }
                catch (Exception ex)
                {
                    output.WriteLine($"      bool {property.Name} -> <{ex.GetType().Name}>");
                }
            }

            type = type.BaseType;
        }

        output.WriteLine($"      (bool property count on {target.GetType().Name}: {count})");
    }

    // ── Probe ①: operator declaration symbol shape ─────────────────────────

    // Roslyn 5.0.0 pin (Probe7): only binary/unary operators are legal block
    // members; conversion operators (implicit/explicit) are CS9282 under both
    // Latest and Preview — EMD-OP therefore covers binary/unary/compound only.
    private const string OperatorBlockSource = """
        namespace N
        {
            public readonly record struct Vec(int X, int Y);

            public static class VecOps
            {
                extension(Vec v)
                {
                    public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
                    public static Vec operator -(Vec a) => new Vec(-a.X, -a.Y);
                    public static Vec operator checked +(Vec a, Vec b) => new Vec(checked(a.X + b.X), checked(a.Y + b.Y));
                }
            }
        }
        """;

    [Fact]
    public void Probe1_OperatorDeclarations_SymbolShape()
    {
        var (compilation, model, tree) = Compile(OperatorBlockSource);
        AssertZeroErrors(compilation, LanguageVersion.Latest);

        var operators = tree.GetCompilationUnitRoot().DescendantNodes()
            .Select(node => node switch
            {
                OperatorDeclarationSyntax op => model.GetDeclaredSymbol(op),
                _ => null,
            })
            .OfType<IMethodSymbol>()
            .OrderBy(m => m.Locations.FirstOrDefault().SourceSpan.Start)
            .ToList();

        foreach (var op in operators)
        {
            _output.WriteLine(
                $"  op: name={op.Name}, methodKind={op.MethodKind}, isStatic={op.IsStatic}, " +
                $"return={op.ReturnType.ToDisplayString()}, " +
                $"params=[{string.Join(", ", op.Parameters.Select(p => p.Type.ToDisplayString()))}], " +
                $"container={op.ContainingType.ToDisplayString()} (typeKind={op.ContainingType.TypeKind}), " +
                $"inBlock={IsDeclaredInBlock(op)}");
        }

        Assert.Equal(3, operators.Count);
        Assert.Equal(MethodKind.UserDefinedOperator, operators[0].MethodKind);
        foreach (var op in operators)
        {
            Assert.Equal(TypeKind.Extension, op.ContainingType.TypeKind);
            Assert.True(op.ContainingType.IsExtension);
            Assert.True(op.IsStatic);
            Assert.True(IsDeclaredInBlock(op));
        }

        // Names pinned for the naming table calls.
        Assert.Equal("op_Addition", operators[0].Name);
        Assert.Equal("op_UnaryNegation", operators[1].Name);
        Assert.Equal("op_CheckedAddition", operators[2].Name);
    }

    // ── Probe ②: four operator use-site kinds ───────────────────────────────

    private const string OperatorUseSitesSource = """
        using System;
        namespace N
        {
            public readonly record struct Vec(int X, int Y);

            public static class VecOps
            {
                extension(Vec v)
                {
                    public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
                    public static Vec operator -(Vec a) => new Vec(-a.X, -a.Y);
                    public static Vec operator checked +(Vec a, Vec b) => new Vec(checked(a.X + b.X), checked(a.Y + b.Y));
                }
            }

            public class Site
            {
                public static void Run(Vec a, Vec b)
                {
                    Vec sum = a + b;
                    Vec neg = -a;
                    a += b;
                }

                public static Vec RunChecked(Vec a, Vec b)
                {
                    return checked(a + b);
                }

                public static Vec? RunLifted(Vec? a, Vec? b)
                {
                    return a + b;
                }
            }
        }
        """;

    [Fact]
    public void Probe2_OperatorUseSites_FourKindsBind()
    {
        var (compilation, model, tree) = Compile(OperatorUseSitesSource);
        AssertZeroErrors(compilation, LanguageVersion.Latest);
        var root = tree.GetCompilationUnitRoot();

        var runMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Run");

        var binary = runMethod.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Single(b => b.IsKind(SyntaxKind.AddExpression));
        var binSymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(binary).Symbol);
        _output.WriteLine($"  binary a+b -> {binSymbol.Name} ({binSymbol.MethodKind}), " +
            $"container typeKind={binSymbol.ContainingType.TypeKind}");
        Assert.Equal("op_Addition", binSymbol.Name);
        Assert.Equal(MethodKind.UserDefinedOperator, binSymbol.MethodKind);

        var unary = runMethod.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
            .Single(u => u.IsKind(SyntaxKind.UnaryMinusExpression));
        var unSymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(unary).Symbol);
        _output.WriteLine($"  unary -a -> {unSymbol.Name} ({unSymbol.MethodKind})");
        Assert.Equal("op_UnaryNegation", unSymbol.Name);

        // Compound assignment.
        var compoundAssign = runMethod.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.IsKind(SyntaxKind.AddAssignmentExpression));
        var compound = model.GetOperation(compoundAssign) as ICompoundAssignmentOperation;
        _output.WriteLine($"  a += b -> operatorMethod={compound?.OperatorMethod?.Name}, " +
            $"isLifted={compound?.IsLifted}");
        Assert.Equal("op_Addition", compound?.OperatorMethod?.Name);

        // Checked context binds op_CheckedAddition.
        var checkedMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "RunChecked");
        var checkedBinary = checkedMethod.DescendantNodes().OfType<BinaryExpressionSyntax>().Single();
        var checkedSymbol = Assert.IsAssignableFrom<IMethodSymbol>(
            model.GetSymbolInfo(checkedBinary).Symbol);
        _output.WriteLine($"  checked(a+b) -> {checkedSymbol.Name}");
        Assert.Equal("op_CheckedAddition", checkedSymbol.Name);

        // Lifted nullable site binds the same method, marked lifted.
        var liftedMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "RunLifted");
        var liftedBinary = liftedMethod.DescendantNodes().OfType<BinaryExpressionSyntax>().Single();
        var liftedOp = model.GetOperation(liftedBinary) as IBinaryOperation
            ?? (IBinaryOperation)model.GetOperation(liftedMethod).Descendants()
                .First(o => o is IBinaryOperation);
        _output.WriteLine($"  lifted Vec? a+b -> operatorMethod={liftedOp.OperatorMethod?.Name}, " +
            $"isLifted={liftedOp.IsLifted}, type={liftedOp.Type?.ToDisplayString()}");
        Assert.Equal("op_Addition", liftedOp.OperatorMethod?.Name);
        Assert.True(liftedOp.IsLifted);
    }

    // ── Probe ③: static member declaration symbols ─────────────────────────

    private const string StaticBlockSource = """
        using System.Collections.Generic;
        using System.Linq;
        namespace N
        {
            public static class Holder
            {
                extension<TSource>(IEnumerable<TSource>)
                {
                    public static IEnumerable<TSource> Generate(int low, int n)
                    {
                        for (var i = 0; i < n; i++)
                        {
                            yield return default(TSource)!;
                        }
                    }

                    public static IEnumerable<TSource> Identity => Enumerable.Empty<TSource>();

                    public static int Settable
                    {
                        get { return 0; }
                        set { }
                    }
                }
            }
        }
        """;

    [Fact]
    public void Probe3_StaticMemberDeclarations_SymbolShape()
    {
        var (compilation, model, tree) = Compile(StaticBlockSource);
        AssertZeroErrors(compilation, LanguageVersion.Latest);

        var staticMethod = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(m => model.GetDeclaredSymbol(m))
            .Single(m => m is { Name: "Generate" });

        _output.WriteLine(
            $"  static method: name={staticMethod!.Name}, methodKind={staticMethod.MethodKind}, " +
            $"isStatic={staticMethod.IsStatic}, isExtensionMethod={staticMethod.IsExtensionMethod}, " +
            $"reducedFrom={staticMethod.ReducedFrom?.ToDisplayString() ?? "<null>"}, " +
            $"arity={staticMethod.Arity}, container typeKind={staticMethod.ContainingType.TypeKind}, " +
            $"container arity={staticMethod.ContainingType.Arity}, " +
            $"params=[{string.Join(", ", staticMethod.Parameters.Select(p => p.Type.ToDisplayString()))}]");
        DumpBoolProperties(staticMethod, _output);

        Assert.Equal(MethodKind.Ordinary, staticMethod.MethodKind);
        Assert.True(staticMethod.IsStatic);
        // Block type parameters live on the extension container; the member's
        // own arity is 0 (Generate declares none).
        Assert.Equal(0, staticMethod.Arity);
        Assert.Equal(1, staticMethod.ContainingType.Arity);
        Assert.Equal(TypeKind.Extension, staticMethod.ContainingType.TypeKind);

        var properties = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Select(p => model.GetDeclaredSymbol(p))
            .Cast<IPropertySymbol>()
            .OrderBy(p => p.Name)
            .ToList();

        foreach (var property in properties)
        {
            _output.WriteLine(
                $"  static property: name={property.Name}, isStatic={property.IsStatic}, " +
                $"isIndexer={property.IsIndexer}, container typeKind={property.ContainingType.TypeKind}, " +
                $"get={property.GetMethod?.Name}/arity={property.GetMethod?.Arity}, " +
                $"set={property.SetMethod?.Name}/arity={property.SetMethod?.Arity}");
        }

        Assert.Equal(2, properties.Count);
        foreach (var property in properties)
        {
            Assert.True(property.IsStatic);
            Assert.Equal(TypeKind.Extension, property.ContainingType.TypeKind);
            Assert.Equal(0, property.GetMethod?.Arity);
            Assert.Equal(1, property.ContainingType.Arity);
        }
    }

    // ── Probe ④: receiver type-name qualified sites ─────────────────────────

    private const string StaticCallSitesSource = """
        using System.Collections.Generic;
        namespace N
        {
            public static class Holder
            {
                extension<TSource>(IEnumerable<TSource>)
                {
                    public static IEnumerable<TSource> Generate(int low, int n)
                    {
                        yield break;
                    }

                    public static TSource FirstOrDefault2() => default(TSource)!;

                    public static IEnumerable<TSource> Identity
                    {
                        get { yield break; }
                    }
                }
            }

            public class Site
            {
                public static void Run()
                {
                    var a = IEnumerable<int>.Generate(0, 10);
                    var b = IEnumerable<int>.Identity;
                    var c = IEnumerable<List<int>>.Generate(0, 1);
                }
            }
        }
        """;

    [Fact]
    public void Probe4_TypeNameQualifiedSites_LeftIsTypeAndIsExtensionApi()
    {
        var (compilation, model, tree) = Compile(StaticCallSitesSource);
        AssertZeroErrors(compilation, LanguageVersion.Latest);

        var siteMethod = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Run");

        var invocations = siteMethod.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        foreach (var invocation in invocations)
        {
            var access = (MemberAccessExpressionSyntax)invocation.Expression;
            var leftTypeInfo = model.GetTypeInfo(access.Expression);
            var leftSymbolInfo = model.GetSymbolInfo(access.Expression);
            var bound = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

            _output.WriteLine(
                $"  invocation {access.ToString().Split('\n')[0]}: " +
                $"leftType={leftTypeInfo.Type?.ToDisplayString()}, " +
                $"leftSymbol={leftSymbolInfo.Symbol?.Kind} {(leftSymbolInfo.Symbol as ITypeSymbol)?.ToDisplayString()}, " +
                $"bound={bound?.Name}, arity={bound?.Arity}, " +
                $"typeArgs=[{string.Join(",", bound?.TypeArguments.Select(t => t.ToDisplayString()) ?? new[] { "" })}]");

            Assert.NotNull(bound);
            Assert.True(leftSymbolInfo.Symbol is ITypeSymbol);
            Assert.Equal(TypeKind.Extension, bound!.ContainingType.TypeKind);

            DumpBoolProperties(bound, _output);
        }

        var firstInvocation = invocations[0];
        var firstBound = (IMethodSymbol)model.GetSymbolInfo(firstInvocation).Symbol!;
        Assert.Equal("Generate", firstBound.Name);
        Assert.Equal(0, firstBound.Arity);
        Assert.Equal("int", firstBound.ContainingType.TypeArguments[0].ToDisplayString());

        // Nested generic receiver: List<int> is the block argument.
        var nestedBound = (IMethodSymbol)model.GetSymbolInfo(invocations[1]).Symbol!;
        Assert.Equal("System.Collections.Generic.List<int>",
            nestedBound.ContainingType.TypeArguments[0].ToDisplayString());

        // Static property site.
        var propertyAccess = siteMethod.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .First(a => a.Name.Identifier.Text == "Identity");
        var leftIsType = model.GetSymbolInfo(propertyAccess.Expression).Symbol is ITypeSymbol;
        var boundProperty = model.GetSymbolInfo(propertyAccess).Symbol as IPropertySymbol;
        _output.WriteLine(
            $"  property IEnumerable<int>.Identity: leftIsType={leftIsType}, " +
            $"property={boundProperty?.Name}, getter={boundProperty?.GetMethod?.Name}, " +
            $"containerTypeArgs=[{string.Join(",", boundProperty?.ContainingType.TypeArguments.Select(t => t.ToDisplayString()) ?? new[] { "" })}]");
        DumpBoolProperties(boundProperty!.GetMethod!, _output);

        Assert.True(leftIsType);
        Assert.Equal(TypeKind.Extension, boundProperty!.ContainingType.TypeKind);
        Assert.Equal(0, boundProperty.GetMethod!.Arity);
        Assert.Equal("int", boundProperty.ContainingType.TypeArguments[0].ToDisplayString());
    }

    /// <summary>Explicit type-argument spelling on type-name qualified static calls.</summary>
    [Fact]
    public void Probe4b_ExplicitTypeArgumentForms_Matrix()
    {
        const string template = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<TSource>(IEnumerable<TSource>)
                    {
                        public static IEnumerable<TSource> Generate(int low, int n)
                        {
                            yield break;
                        }
                    }
                }

                public class Site
                {
                    public static void Run()
                    {
                        __SITE__;
                    }
                }
            }
            """;

        var forms = new (string Label, string Site)[]
        {
            ("omitted", "var a = IEnumerable<int>.Generate(0, 10)"),
            ("explicit-full", "var a = IEnumerable<int>.Generate<int>(0, 10)"),
            ("explicit-mismatched", "var a = IEnumerable<int>.Generate<string>(0, 10)"),
        };

        foreach (var (label, site) in forms)
        {
            var source = template.Replace("__SITE__", site);
            var (compilation, _, tree) = Compile(source, name: "explicit-forms");
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id).Distinct().OrderBy(x => x).ToList();
            _output.WriteLine($"  [{label}] -> {(errors.Count == 0 ? "OK" : string.Join(",", errors))}");
            if (errors.Count == 0)
            {
                var invocation = tree.GetCompilationUnitRoot().DescendantNodes()
                    .OfType<InvocationExpressionSyntax>().Single();
                var bound = (IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol!;
                _output.WriteLine(
                    $"      bound typeArgs=[{string.Join(",", bound.TypeArguments.Select(t => t.ToDisplayString()))}], " +
                    $"name node={(invocation.Expression is MemberAccessExpressionSyntax ma ? ma.Name.Kind().ToString() : "?")}");
            }
        }

        Assert.True(true);
    }

    // ── Probe ⑤: inference boundary (§4.4-4) ───────────────────────────────

    [Fact]
    public void Probe5_BlockParameterOnlyInReceiver_InferenceBoundary()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<T>(IEnumerable<T>)
                    {
                        public static int Describe() => 0;
                        public static int Marker
                        {
                            get { return 0; }
                            set { }
                        }
                        public static int Observe(T t) => 1;
                    }
                }

                public class Site
                {
                    public static void Run()
                    {
                        int a = IEnumerable<int>.Describe();
                        int b = IEnumerable<int>.Marker;
                        int c = IEnumerable<int>.Observe(1);
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        AssertZeroErrors(compilation, LanguageVersion.Latest);
        _output.WriteLine("  source with type-name qualified sites compiles (all four shapes).");

        // Candidate rewritten forms (receiver type name gone).
        const string candidates = """
            namespace N
            {
                public static class Holder
                {
                    public static int Describe<T>() => 0;
                    public static int get_Marker<T>() => 0;
                    public static int Observe<T>(T t) => 1;
                    public static U Map<T, U>(U u) => u;
                }

                public class Site
                {
                    public static void Omitted()
                    {
                        int a = N.Holder.Describe();
                        int b = N.Holder.get_Marker();
                        int c = N.Holder.Observe(1);
                    }

                    public static void Explicit()
                    {
                        int a = N.Holder.Describe<int>();
                        int b = N.Holder.get_Marker<int>();
                        string d = N.Holder.Map<int, string>("x");
                    }
                }
            }
            """;

        var (candidateCompilation, _, candidateTree) = Compile(candidates, name: "inference-candidate");
        var omittedMethod = candidateTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Omitted");
        var omittedErrors = candidateCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree == candidateTree
                && omittedMethod.FullSpan.Contains(d.Location.SourceSpan.Start))
            .Select(d => d.Id).Distinct().OrderBy(x => x).ToList();
        _output.WriteLine($"  omitted rewritten forms -> {string.Join(",", omittedErrors)}");

        var explicitMethod = candidateTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Explicit");
        var explicitErrors = candidateCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree == candidateTree
                && explicitMethod.FullSpan.Contains(d.Location.SourceSpan.Start))
            .Select(d => d.Id).Distinct().ToList();
        _output.WriteLine(
            $"  explicit rewritten forms -> {(explicitErrors.Count == 0 ? "OK" : string.Join(",", explicitErrors))}");

        Assert.Contains("CS0411", omittedErrors);
        Assert.Empty(explicitErrors);
    }

    // ── Probe ⑥: receiver modifier matrix ───────────────────────────────────

    private const string ModifierMatrixTemplate = """
        namespace N
        {
            public class Box { public int x; }

            public static class Holder
            {
                extension(__MODS__ Box r)
                {
                    __MEMBER__
                }
            }
        }
        """;

    [Fact]
    public void Probe6_ReceiverModifierMatrix_LegalSets()
    {
        var modifiers = new (string Label, string Mods)[]
        {
            ("none", ""),
            ("ref", "ref"),
            ("in", "in"),
            ("readonly-ref", "readonly ref"),
            ("scoped", "scoped"),
            ("scoped-ref", "scoped ref"),
        };

        var members = new (string Label, string Member)[]
        {
            ("method", "public void Touch() { r.x++; }"),
            ("property-get", "public int G => r.x;"),
            ("property-set", "public int S { get => r.x; set { r.x = value; } }"),
            ("operator", "public static Box operator +(Box a, Box b) => a;"),
            ("static-method", "public static int StaticOne() => 1;"),
            ("static-property", "public static int StaticP => 1;"),
        };

        foreach (var (modifierLabel, mods) in modifiers)
        {
            foreach (var (memberLabel, member) in members)
            {
                var source = ModifierMatrixTemplate
                    .Replace("__MODS__", mods)
                    .Replace("__MEMBER__", member);
                var (compilation, model, tree) = Compile(source, name: "mod-matrix");
                var errorIds = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.Id).Distinct().OrderBy(x => x).ToList();
                _output.WriteLine(
                    $"  [{modifierLabel,-13} x {memberLabel,-16}] -> " +
                    (errorIds.Count == 0 ? "OK" : string.Join(",", errorIds)));

                if (errorIds.Count == 0 && (modifierLabel is "ref" or "in" or "readonly-ref")
                    && memberLabel is "property-get" or "property-set" or "method")
                {
                    // Pin the first-parameter ref mapping of generated accessors.
                    var node = tree.GetCompilationUnitRoot().DescendantNodes()
                        .First(n => n is MethodDeclarationSyntax or PropertyDeclarationSyntax);
                    var accessorsOrMethod = node switch
                    {
                        PropertyDeclarationSyntax property => new[]
                        {
                            model.GetDeclaredSymbol(property)?.GetMethod,
                            model.GetDeclaredSymbol(property)?.SetMethod,
                        }.Where(m => m is not null).Cast<IMethodSymbol>(),
                        MethodDeclarationSyntax method => new[] { model.GetDeclaredSymbol(method)! },
                        _ => Array.Empty<IMethodSymbol>(),
                    };

                    foreach (var symbol in accessorsOrMethod)
                    {
                        _output.WriteLine(
                            $"      {symbol.Name}: firstParam={symbol.Parameters[0].RefKind}/" +
                            $"scoped={symbol.Parameters[0].ScopedKind}, readonlyRef={symbol.Parameters[0].RefKind == RefKind.In}");
                    }
                }
            }
        }

        Assert.True(true);
    }

    /// <summary>Pointer receiver has no legal classic extension form.</summary>
    [Fact]
    public void Probe6b_PointerReceiver_LegalSet()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int* p)
                    {
                        public int Get() => *p;
                    }
                }
            }
            """;

        var (compilation, _, _) = Compile(source, name: "pointer-receiver", allowUnsafe: true);
        var errorIds = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id).Distinct().OrderBy(x => x).ToList();
        _output.WriteLine($"  pointer receiver -> {string.Join(",", errorIds)}");
        Assert.NotEmpty(errorIds);
    }

    /// <summary>Static member inside an unnamed-receiver block (static-only block).</summary>
    [Fact]
    public void Probe6c_UnnamedReceiver_StaticOnly()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<T>(IEnumerable<T>)
                    {
                        public static int Only() => 0;
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        AssertZeroErrors(compilation, LanguageVersion.Latest);
        var block = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<ExtensionBlockDeclarationSyntax>().Single();
        _output.WriteLine(
            $"  unnamed receiver block: parameterList={block.ParameterList}, " +
            $"first parameter identifier='{block.ParameterList!.Parameters[0].Identifier.Text}'");
        Assert.Equal("", block.ParameterList.Parameters[0].Identifier.Text);
    }

    // ── Probe ⑦: version matrix for shapes assumed-legal by the design ─────

    [Fact]
    public void Probe7_VersionMatrix_ConversionAndRefReceivers()
    {
        var fragments = new (string Label, string Fragment)[]
        {
            ("conversion-implicit", """
                public static class H { extension(Vec v) {
                    public static implicit operator string(Vec a) => "";
                } }
                public readonly record struct Vec(int X);
                """),
            ("conversion-explicit", """
                public static class H { extension(Vec v) {
                    public static explicit operator int(Vec a) => 0;
                } }
                public readonly record struct Vec(int X);
                """),
            ("receiver-ref", """
                public class Box { public int x; }
                public static class H { extension(ref Box r) {
                    public void Touch() { r.x++; }
                } }
                """),
            ("receiver-in", """
                public class Box { public int x; }
                public static class H { extension(in Box r) {
                    public int G => r.x;
                } }
                """),
            ("receiver-readonly-ref", """
                public class Box { public int x; }
                public static class H { extension(readonly ref Box r) {
                    public int G => r.x;
                } }
                """),
            ("receiver-scoped", """
                public struct Box { public int x; }
                public static class H { extension(scoped Box r) {
                    public int G => r.x;
                } }
                """),
            ("receiver-scoped-ref", """
                public struct Box { public int x; }
                public static class H { extension(scoped ref Box r) {
                    public int G => r.x;
                } }
                """),
        };

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            _output.WriteLine($"── [{version}] ──");
            foreach (var (label, fragment) in fragments)
            {
                var source = "namespace N { " + fragment + " }";
                var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                    source, new CSharpParseOptions(version));
                var compilation = CSharpCompilation.Create(
                    "m-" + Guid.NewGuid().ToString("N"), new[] { tree },
                    References, Options());
                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                _output.WriteLine($"  {label,-24} -> " +
                    (errors.Count == 0 ? "OK" : string.Join("; ",
                        errors.Select(d => $"{d.Id}:{d.GetMessage()}"))));
            }
        }

        Assert.True(true);
    }

    // ── Probe ⑧: where block type parameters actually live ─────────────────

    [Fact]
    public void Probe8_GenericShape_BlockParamsCarriedByContainer()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<TSource>(IEnumerable<TSource>)
                    {
                        public static IEnumerable<TSource> Generate(int low, int n)
                        {
                            yield break;
                        }
                    }
                }

                public class Site
                {
                    public static void Run()
                    {
                        var a = IEnumerable<int>.Generate(0, 10);
                        var b = IEnumerable<int>.Generate<int>(0, 10);
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        AssertZeroErrors(compilation, LanguageVersion.Latest);

        var declared = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Generate");
        var declaredSymbol = model.GetDeclaredSymbol(declared)!;
        DumpContainer("declared Generate", declaredSymbol, _output);
        DumpBoolProperties(declaredSymbol, _output);

        var invocations = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().ToList();
        foreach (var invocation in invocations)
        {
            var bound = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            _output.WriteLine($"── site {invocation.ToString().Replace("\n", " ")} ──");
            DumpContainer("bound", bound!, _output);
            var operation = model.GetOperation(invocation);
            _output.WriteLine($"  operation kind={operation?.Kind}, type={operation?.GetType().Name}");
            DumpBoolProperties(bound!, _output);
        }

        Assert.True(true);
    }

    private static void DumpContainer(string label, IMethodSymbol method, ITestOutputHelper output)
    {
        var container = method.ContainingType;
        output.WriteLine(
            $"  [{label}] method name={method.Name}, arity={method.Arity}, " +
            $"typeArgs=[{string.Join(",", method.TypeArguments.Select(t => t.ToDisplayString()))}], " +
            $"originalDef={method.OriginalDefinition.ToDisplayString()}, " +
            $"methodDefName={method.OriginalDefinition.Name}, " +
            $"constructedFrom={method.ConstructedFrom?.ToDisplayString()}, " +
            $"reducedFrom={method.ReducedFrom?.ToDisplayString() ?? "<null>"}");
        output.WriteLine(
            $"      container={container.ToDisplayString()}, typeKind={container.TypeKind}, " +
            $"container arity={container.Arity}, " +
            $"container typeParams=[{string.Join(",", container.TypeParameters.Select(t => t.Name))}], " +
            $"container typeArgs=[{string.Join(",", container.TypeArguments.Select(t => t.ToDisplayString()))}], " +
            $"container originalDef={container.OriginalDefinition.ToDisplayString()}");
    }

    // ── Probe ⑨: member-level type parameters × call spelling matrix ───────

    [Fact]
    public void Probe9_MemberTypeParameter_CallSpellingMatrix()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<T>(IEnumerable<T>)
                    {
                        public static U StaticMap<U>(U u) => u;
                        public U InstanceMap<U>(U u) => u;
                    }
                }

                public class Site
                {
                    public static void Run(IEnumerable<int> seq)
                    {
                        var a = IEnumerable<int>.StaticMap<string>("x");
                        var b = IEnumerable<int>.StaticMap("x");
                        var c = seq.StaticMap<string>("x");
                        var d = seq.InstanceMap<string>("x");
                        var e = seq.InstanceMap("x");
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .GroupBy(d => d.Location.GetLineSpan().StartLinePosition.Line + 1)
            .OrderBy(g => g.Key)
            .ToList();
        foreach (var group in errors)
        {
            _output.WriteLine($"  line {group.Key}: " +
                string.Join("; ", group.Select(d => $"{d.Id}:{d.GetMessage()}")));
        }

        // Declaration shapes for the two Map members.
        foreach (var method in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<MethodDeclarationSyntax>()
                     .Where(m => m.Identifier.Text is "StaticMap" or "InstanceMap"))
        {
            var symbol = model.GetDeclaredSymbol(method)!;
            DumpContainer(symbol.Name, symbol, _output);
            _output.WriteLine($"      isStatic={symbol.IsStatic}, params=[{string.Join(", ",
                symbol.Parameters.Select(p => p.Type.ToDisplayString()))}]");
        }

        // Which sites bound (operations present)?
        foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            var info = model.GetSymbolInfo(invocation);
            _output.WriteLine($"  site {invocation.ToString().Replace("\n", " ")} -> " +
                $"symbol={info.Symbol?.Name ?? "<null>"}, candidateCount={info.CandidateSymbols.Length}");
        }

        Assert.True(true);
    }

    /// <summary>Member type params in a non-generic block (no block params).</summary>
    [Fact]
    public void Probe9b_MemberTypeParameter_NonGenericBlock()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int x)
                    {
                        public static U StaticMap<U>(U u) => u;
                        public U InstanceMap<U>(U u) => u;
                    }
                }

                public class Site
                {
                    public static void Run(int x)
                    {
                        var a = int.StaticMap<string>("x");
                        var b = int.StaticMap("x");
                        var c = x.StaticMap<string>("x");
                        var d = x.InstanceMap<string>("x");
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        foreach (var group in compilation.GetDiagnostics()
                     .Where(d => d.Severity == DiagnosticSeverity.Error)
                     .GroupBy(d => d.Location.GetLineSpan().StartLinePosition.Line + 1)
                     .OrderBy(g => g.Key))
        {
            _output.WriteLine($"  line {group.Key}: " +
                string.Join("; ", group.Select(d => $"{d.Id}:{d.GetMessage()}")));
        }

        foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            var info = model.GetSymbolInfo(invocation);
            _output.WriteLine($"  site {invocation.ToString().Replace("\n", " ")} -> " +
                $"symbol={info.Symbol?.Name ?? "<null>"}");
        }

        Assert.True(true);
    }

    // ── Probe ⑩: generic operators inside generic blocks ──────────────────

    [Fact]
    public void Probe10_GenericOperators_DeclarationShape()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<T>(IEnumerable<T> source)
                    {
                        public static IEnumerable<T> operator +(
                            IEnumerable<T> a, IEnumerable<T> b) => a;
                    }

                    extension(Vec v)
                    {
                        public static Vec operator +<T>(Vec a, Vec b) => a;
                    }
                }

                public readonly record struct Vec(int X);

                public class Site
                {
                    public static void Run(IEnumerable<int> a, IEnumerable<int> b)
                    {
                        var c = a + b;
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        foreach (var group in compilation.GetDiagnostics()
                     .Where(d => d.Severity == DiagnosticSeverity.Error)
                     .GroupBy(d => d.Location.GetLineSpan().StartLinePosition.Line + 1)
                     .OrderBy(g => g.Key))
        {
            _output.WriteLine($"  line {group.Key}: " +
                string.Join("; ", group.Select(d => $"{d.Id}:{d.GetMessage()}")));
        }

        foreach (var op in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<OperatorDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(op);
            _output.WriteLine($"  operator {op.OperatorToken}: symbol name={symbol?.Name}, " +
                $"arity={symbol?.Arity}, container arity={symbol?.ContainingType.Arity}");
        }

        var site = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<BinaryExpressionSyntax>().FirstOrDefault();
        if (site is not null)
        {
            var bound = model.GetSymbolInfo(site).Symbol as IMethodSymbol;
            _output.WriteLine($"  use site a+b -> {bound?.Name}, arity={bound?.Arity}");
            if (bound is not null)
            {
                DumpContainer("bound-op", bound, _output);
            }
        }

        Assert.True(true);
    }

    // ── Probe ⑪: value-type receiver modifier full matrix ─────────────────

    [Fact]
    public void Probe11_ValueTypeReceiverModifier_Matrix()
    {
        const string template = """
            namespace N
            {
                public struct S { public int x; }
                public readonly struct RS { public readonly int x; }
                public ref struct SP { public int x; }

                public static class H
                {
                    extension(__MODS__ __TYPE__ r)
                    {
                        __MEMBER__
                    }
                }
            }
            """;

        var typeMods = new (string Label, string Type, string Mods)[]
        {
            ("ref-S", "S", "ref"),
            ("in-S", "S", "in"),
            ("ref-readonly-S", "S", "ref readonly"),
            ("scoped-ref-S", "S", "scoped ref"),
            ("scoped-in-S", "S", "scoped in"),
            ("ref-RS", "RS", "ref"),
            ("in-RS", "RS", "in"),
            ("ref-readonly-RS", "RS", "ref readonly"),
            ("ref-SP", "SP", "ref"),
            ("in-SP", "SP", "in"),
            ("scoped-SP", "SP", "scoped"),
            ("scoped-ref-SP", "SP", "scoped ref"),
            ("scoped-in-SP", "SP", "scoped in"),
        };

        var members = new (string Label, string Member)[]
        {
            ("method", "public void Touch() { r.x++; }"),
            ("property", "public int G { get => r.x; set { r.x = value; } }"),
            ("operator", "public static S operator +(S a, S b) => a;"),
            ("static", "public static int One() => 1;"),
        };

        foreach (var (typeLabel, type, mods) in typeMods)
        {
            foreach (var (memberLabel, member) in members)
            {
                var source = template
                    .Replace("__TYPE__", type).Replace("__MODS__", mods)
                    .Replace("__MEMBER__", member);
                var (compilation, model, tree) = Compile(source, name: "vt-matrix");
                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                _output.WriteLine($"  [{typeLabel,-17} x {memberLabel,-8}] -> " +
                    (errors.Count == 0 ? "OK" : string.Join(", ",
                        errors.Select(d => $"{d.Id}:{d.GetMessage()}"))));

                if (errors.Count == 0 && memberLabel is "method" or "property"
                    && typeLabel is "ref-S" or "in-S" or "ref-readonly-S"
                        or "scoped-ref-S" or "scoped-in-S" or "scoped-SP")
                {
                    var node = tree.GetCompilationUnitRoot().DescendantNodes()
                        .First(n => n is MethodDeclarationSyntax or PropertyDeclarationSyntax);
                    var symbols = node switch
                    {
                        PropertyDeclarationSyntax p => new[]
                        {
                            model.GetDeclaredSymbol(p)?.GetMethod,
                            model.GetDeclaredSymbol(p)?.SetMethod,
                        }.Where(m => m is not null).Cast<IMethodSymbol>(),
                        MethodDeclarationSyntax m => new[] { model.GetDeclaredSymbol(m)! },
                        _ => Array.Empty<IMethodSymbol>(),
                    };
                    foreach (var symbol in symbols)
                    {
                        var first = symbol.Parameters.Length > 0
                            ? $"firstParam refKind={symbol.Parameters[0].RefKind}, " +
                              $"scoped={symbol.Parameters[0].ScopedKind}, " +
                              $"type={symbol.Parameters[0].Type.ToDisplayString()}"
                            : "<no parameters>";
                        _output.WriteLine(
                            $"      {symbol.Name} (kind={symbol.MethodKind}, " +
                            $"isStatic={symbol.IsStatic}): {first}, " +
                            $"allParams=[{string.Join("; ", symbol.Parameters.Select(
                                p => $"{p.RefKind} {p.Type.ToDisplayString()}"))}]");
                    }
                }
            }
        }

        Assert.True(true);
    }

    /// <summary>Generic block receivers with ref/struct constraints.</summary>
    [Fact]
    public void Probe11b_GenericReceiver_ModifiersAndConstraints()
    {
        const string template = """
            namespace N
            {
                public static class H
                {
                    extension<T>(__RECEIVER__ r)
                    {
                        public void Touch() { }
                        public static int One() => 1;
                    }
                }
            }
            """;

        var receivers = new (string Label, string Receiver)[]
        {
            ("plain-T", "T"),
            ("ref-T", "ref T"),
            ("in-T", "in T"),
            ("ref-readonly-T", "ref readonly T"),
            ("scoped-T-refstruct", "scoped T"),
            ("scoped-ref-T", "scoped ref T"),
        };

        foreach (var (label, receiver) in receivers)
        {
            // Try the receiver both with struct constraint on the block and without.
            foreach (var constrained in new[] { false, true })
            {
                var source = template.Replace("__RECEIVER__", receiver);
                if (constrained)
                {
                    source = source.Replace(
                        "extension<T>(__RECEIVER__ r)",
                        "extension<T>(__RECEIVER__ r) where T : struct");
                }

                var (compilation, _, _) = Compile(source, name: "generic-ref");
                var ids = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.Id).Distinct().OrderBy(x => x).ToList();
                _output.WriteLine($"  [{label,-20} struct-constraint={constrained,-5}] -> " +
                    (ids.Count == 0 ? "OK" : string.Join(",", ids)));
            }
        }

        Assert.True(true);
    }

    /// <summary>Use-site spelling for members of a ref-receiver block.</summary>
    [Fact]
    public void Probe11c_RefReceiver_UseSiteSpelling()
    {
        const string source = """
            namespace N
            {
                public struct S { public int x; public static S Make() => default; }
                public static class H
                {
                    extension(ref S r)
                    {
                        public void Touch() { r.x++; }
                        public int G => r.x;
                    }
                }

                public class Site
                {
                    public static void Run()
                    {
                        S s = default;
                        s.Touch();
                        int g = s.G;
                        S.Make().Touch();
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        foreach (var group in compilation.GetDiagnostics()
                     .Where(d => d.Severity == DiagnosticSeverity.Error)
                     .GroupBy(d => d.Location.GetLineSpan().StartLinePosition.Line + 1)
                     .OrderBy(g => g.Key))
        {
            _output.WriteLine($"  line {group.Key}: " +
                string.Join("; ", group.Select(d => $"{d.Id}:{d.GetMessage()}")));
        }

        foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            var bound = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            _output.WriteLine($"  invocation {invocation} -> {bound?.Name ?? "<null>"}, " +
                $"container={bound?.ContainingType.ToDisplayString()}");
        }

        foreach (var access in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<MemberAccessExpressionSyntax>()
                     .Where(a => a.Name.Identifier.Text == "G"))
        {
            var bound = model.GetSymbolInfo(access).Symbol as IPropertySymbol;
            _output.WriteLine($"  property access {access} -> {bound?.Name ?? "<null>"}");
        }

        Assert.True(true);
    }

    // ── Probe ⑫: combined n+m explicit type arguments spell fully ─────────

    [Fact]
    public void Probe12_CombinedExplicitArguments_NThenM()
    {
        const string source = """
            using System.Collections.Generic;
            namespace N
            {
                public static class Holder
                {
                    extension<T>(IEnumerable<T>)
                    {
                        public static U StaticMap<U>(U u) => u;
                        public static int Marker { get { return 0; } set { } }
                    }
                }

                public class Site
                {
                    public static void Run()
                    {
                        var a = IEnumerable<int>.StaticMap<int, string>("x");
                        var b = IEnumerable<int>.StaticMap<string>("x");
                        int c = IEnumerable<int>.get_Marker<int>();
                    }
                }
            }
            """;

        var (compilation, model, tree) = Compile(source);
        foreach (var group in compilation.GetDiagnostics()
                     .Where(d => d.Severity == DiagnosticSeverity.Error)
                     .GroupBy(d => d.Location.GetLineSpan().StartLinePosition.Line + 1)
                     .OrderBy(g => g.Key))
        {
            _output.WriteLine($"  line {group.Key}: " +
                string.Join("; ", group.Select(d => $"{d.Id}:{d.GetMessage()}")));
        }

        foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            var bound = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            _output.WriteLine($"  site {invocation.ToString().Replace("\n", " ")} -> " +
                $"name={bound?.Name ?? "<null>"}");
            if (bound is not null)
            {
                DumpContainer("  ", bound, _output);
            }
        }

        Assert.True(true);
    }

    // ── Probe ⑬: exact bool API surface on symbol interfaces ──────────────

    [Fact]
    public void Probe13_InterfaceBoolApis_ExtensionMembers()
    {
        foreach (var interfaceType in new[]
                 {
                     typeof(IMethodSymbol), typeof(IPropertySymbol), typeof(ITypeSymbol),
                     typeof(INamedTypeSymbol), typeof(IParameterSymbol), typeof(ISymbol),
                 })
        {
            var bools = interfaceType.GetProperties()
                .Where(p => p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0)
                .Select(p => p.Name)
                .Where(n => n.Contains("Extension", StringComparison.Ordinal)
                    || n.Contains("Ref", StringComparison.Ordinal)
                    || n.Contains("Scoped", StringComparison.Ordinal)
                    || n.Contains("Read", StringComparison.Ordinal))
                .ToList();
            _output.WriteLine($"  {interfaceType.Name}: {string.Join(", ", bools)}");
        }

        Assert.True(true);
    }
}
