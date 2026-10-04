using CSharpToJava.Core.ExtensionMethodResolve;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C49 M3 probes against Roslyn 5.0.0 GA (pinned facts that the M3
/// implementation consumes; conclusions mirrored into
/// docs/superpowers/specs/2026-09-29-c49-extension-members-design.md,
/// "M3 探针结论（2026-10-05）"):
///
/// ① Extended null-conditional property chains: tree shapes and bindings of
///    <c>w?.Alias.Length</c>, <c>w?.Reply(1).Length</c> and the nested
///    <c>w?.Reply(1)?.Length</c>.
/// ② Null-conditional write/compound legality: <c>w?.Alias = "x"</c>,
///    <c>w?.Alias += "x"</c>, <c>w?.Alias++</c> — parse shape + semantic
///    diagnostics.
/// ③ Block method-group delegate conversion: <c>Func&lt;...&gt; f = w.Reply;</c>
///    binds to the Ordinary method inside the TypeKind.Extension container,
///    converted type is the target delegate; nameof/var contexts expose
///    CandidateReason.MemberGroup; failed conversions expose candidates.
/// ④ Static block member groups: <c>Func&lt;...&gt; f = Widget.Make;</c>.
/// ⑤ Expression-tree boundary: extension property / operator / method sites
///    inside <c>Expression&lt;&gt;</c> lambdas — original compile diagnostics
///    and inner-node symbol info.
/// ⑥ Metadata boundary: a separately compiled assembly declaring an extension
///    block — what consumers bind to (property vs get_X method, TypeKind).
/// ⑦ env-gated SDK hit-rate scan (CS2J_RUN_SDK_PROBE=1, D:\code\csharp-sdk).
/// </summary>
public sealed class ExtensionBlockM3ProbeTests
{
    private readonly ITestOutputHelper _output;

    public ExtensionBlockM3ProbeTests(ITestOutputHelper output) => _output = output;

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

    private static CSharpCompilationOptions CompilationOptions { get; } =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);

    private static (CSharpCompilation Compilation, SemanticModel Model, SyntaxTree Tree) Compile(
        string source, string name = "m3probe", IReadOnlyCollection<MetadataReference>? extraReferences = null)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: name + ".cs");
        var references = extraReferences is null
            ? References
            : References.Concat(extraReferences).ToArray();
        var compilation = CSharpCompilation.Create(
            name + "-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            CompilationOptions);
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private static ConditionalAccessExpressionSyntax FindConditional(SyntaxNode root, string fragmentMarker)
        => root.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>()
            .First(c => c.ToString().Contains(fragmentMarker, StringComparison.Ordinal));

    private void DumpDiagnostics(CSharpCompilation compilation)
    {
        foreach (var d in compilation.GetDiagnostics().OrderBy(d => d.Location.SourceSpan.Start))
        {
            var lineSpan = d.Location.GetLineSpan();
            _output.WriteLine(
                $"    diag {d.Id} [{d.Severity}] @line {lineSpan.StartLinePosition.Line + 1} " +
                $"'{d.Location.SourceTree?.GetRoot().FindNode(d.Location.SourceSpan).ToString() ?? "-"}' {d.GetMessage()}");
        }
    }

    private static List<Diagnostic> Errors(CSharpCompilation compilation)
        => compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToList();

    // ── Probe ①: extended null-conditional property chains ──────────────────

    private const string ChainSource = """
        using System;
        public class Widget { public int Core; }
        public static class Hosts
        {
            extension(Widget w)
            {
                public string Alias => "a";
                public string Reply(int x) => x.ToString();
            }
        }
        public static class Driver
        {
            public static void Run(Widget? w)
            {
                var a = w?.Alias.Length;
                var b = w?.Reply(1).Length;
            }
        }
        """;

    [Fact]
    public void Probe1_NullCond_ExtendedPropertyChain_TreeAndBinding()
    {
        var (compilation, model, tree) = Compile(ChainSource);
        var root = tree.GetCompilationUnitRoot();
        DumpDiagnostics(compilation);

        // w?.Alias.Length
        var cond1 = FindConditional(root, "Alias");
        var when1 = Assert.IsType<MemberAccessExpressionSyntax>(cond1.WhenNotNull);
        var prefix1 = Assert.IsType<MemberBindingExpressionSyntax>(when1.Expression);
        Assert.Equal("Length", when1.Name.Identifier.ValueText);
        Assert.Equal("Alias", prefix1.Name.Identifier.ValueText);

        var extProperty = model.GetSymbolInfo(prefix1).Symbol as IPropertySymbol;
        Assert.NotNull(extProperty);
        Assert.Equal(TypeKind.Extension, extProperty!.ContainingType.TypeKind);
        // Outer .Length binds to the classic string.Length property (minimal display).
        Assert.Equal("string.Length", model.GetSymbolInfo(when1).Symbol?.ToDisplayString());

        // w?.Reply(1).Length
        var cond2 = FindConditional(root, "Reply");
        var when2 = Assert.IsType<MemberAccessExpressionSyntax>(cond2.WhenNotNull);
        var invocation = Assert.IsType<InvocationExpressionSyntax>(when2.Expression);
        var binding2 = Assert.IsType<MemberBindingExpressionSyntax>(invocation.Expression);
        Assert.Equal("Reply", binding2.Name.Identifier.ValueText);

        var extMethod = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        Assert.NotNull(extMethod);
        Assert.Equal(MethodKind.Ordinary, extMethod!.MethodKind);
        Assert.Equal(TypeKind.Extension, extMethod.ContainingType.TypeKind);
        Assert.Equal("string.Length", model.GetSymbolInfo(when2).Symbol?.ToDisplayString());

        DumpDiagnostics(compilation);
        Assert.Empty(Errors(compilation));
    }

    [Fact]
    public void Probe2_NullCond_NestedConditional_TreeShape()
    {
        const string source = """
            using System;
            public class Widget { }
            public static class Hosts
            {
                extension(Widget w)
                {
                    public string? Reply(int x) => x.ToString();
                }
            }
            public static class Driver
            {
                public static void Run(Widget? w)
                {
                    var a = w?.Reply(1)?.Length;
                }
            }
            """;
        var (compilation, model, tree) = Compile(source);
        var root = tree.GetCompilationUnitRoot();

        var outer = root.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>()
            .First(c => c.Expression.ToString() == "w");
        var inner = Assert.IsType<ConditionalAccessExpressionSyntax>(outer.WhenNotNull);
        var invocation = Assert.IsType<InvocationExpressionSyntax>(inner.Expression);
        Assert.IsType<MemberBindingExpressionSyntax>(invocation.Expression);
        var tail = Assert.IsType<MemberBindingExpressionSyntax>(inner.WhenNotNull);
        Assert.Equal("Length", tail.Name.Identifier.ValueText);

        var extMethod = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        Assert.Equal(TypeKind.Extension, extMethod!.ContainingType.TypeKind);

        DumpDiagnostics(compilation);
        Assert.Empty(Errors(compilation));
    }

    // ── Probe ②: null-conditional write/compound legality matrix ─────────────

    [Fact]
    public void Probe3_NullCond_WriteCompound_LegalityMatrix()
    {
        const string source = """
            using System;
            public class Widget { public string Name = ""; public int Count; }
            public static class Hosts
            {
                extension(Widget w)
                {
                    public string Alias
                    {
                        get { return w.Name; }
                        set { w.Name = value; }
                    }
                    public int Count
                    {
                        get { return w.Count; }
                        set { w.Count = value; }
                    }
                }
            }
            public static class Driver
            {
                public static void Write(Widget? w) { w?.Alias = "x"; }
                public static void Compound(Widget? w) { w?.Alias += "x"; }
                public static void Increment(Widget? w) { w?.Count++; }
                public static void ChainRead(Widget? w) { w?.Alias.Length.ToString(); }
            }
            """;
        var (compilation, model, tree) = Compile(source);
        var root = tree.GetCompilationUnitRoot();

        // w?.Alias = "x" — legal (WhenNotNull simple assignment, member-binding left).
        var writeCond = FindConditional(root, "Alias =");
        var writeWhen = Assert.IsType<AssignmentExpressionSyntax>(writeCond.WhenNotNull);
        var writeBinding = Assert.IsType<MemberBindingExpressionSyntax>(writeWhen.Left);
        Assert.Equal(SyntaxKind.SimpleAssignmentExpression, writeWhen.Kind());
        var writeProperty = Assert.IsAssignableFrom<IPropertySymbol>(
            model.GetSymbolInfo(writeBinding).Symbol);
        Assert.Equal(TypeKind.Extension, writeProperty.ContainingType.TypeKind);

        // w?.Alias += "x" — legal (WhenNotNull add-assignment).
        var compoundCond = FindConditional(root, "Alias +=");
        var compoundWhen = Assert.IsType<AssignmentExpressionSyntax>(compoundCond.WhenNotNull);
        var compoundBinding = Assert.IsType<MemberBindingExpressionSyntax>(compoundWhen.Left);
        Assert.Equal(SyntaxKind.AddAssignmentExpression, compoundWhen.Kind());
        var compoundProperty = Assert.IsAssignableFrom<IPropertySymbol>(
            model.GetSymbolInfo(compoundBinding).Symbol);
        Assert.Equal(TypeKind.Extension, compoundProperty.ContainingType.TypeKind);

        // w?.Count++ — ILLEGAL in Roslyn 5.0: CS1059 on the conditional access.
        var postfix = root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
            .First(p => p.Operand.ToString().Contains("Count", StringComparison.Ordinal));
        var postCond = Assert.IsType<ConditionalAccessExpressionSyntax>(postfix.Operand);
        Assert.IsType<MemberBindingExpressionSyntax>(postCond.WhenNotNull);

        _output.WriteLine("diagnostics for the legality matrix:");
        var errors = Errors(compilation);
        DumpDiagnostics(compilation);

        var errorCounts = errors.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, errorCounts.GetValueOrDefault("CS1059"));
        Assert.False(errorCounts.ContainsKey("CS9282"));
    }

    // ── Probe ③: block method-group delegate conversion ─────────────────────

    [Fact]
    public void Probe4_MethodGroup_DelegateConversion_BindingShape()
    {
        const string source = """
            using System;
            public class Widget { }
            public static class Hosts
            {
                extension(Widget w)
                {
                    public string Reply(int x) => x.ToString();
                    public string Reply(string x) => x;
                }
            }
            public static class Driver
            {
                public static void Run(Widget w)
                {
                    Func<int, string> f = w.Reply;
                    var s = f(3);
                }
            }
            """;
        var (compilation, model, tree) = Compile(source);
        var root = tree.GetCompilationUnitRoot();

        var memberAccess = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .First(m => m.ToString() == "w.Reply");
        var info = model.GetSymbolInfo(memberAccess);
        var method = Assert.IsAssignableFrom<IMethodSymbol>(info.Symbol);
        Assert.Equal(MethodKind.Ordinary, method.MethodKind);
        Assert.False(method.IsExtensionMethod);
        Assert.Null(method.ReducedFrom);
        Assert.Equal(TypeKind.Extension, method.ContainingType.TypeKind);

        var converted = model.GetTypeInfo(memberAccess).ConvertedType as INamedTypeSymbol;
        Assert.Equal(TypeKind.Delegate, converted!.TypeKind);
        var invoke = converted.DelegateInvokeMethod!;
        Assert.Single(invoke.Parameters);
        Assert.Equal(SpecialType.System_Int32, invoke.Parameters[0].Type.SpecialType);
        Assert.Equal(SpecialType.System_String, invoke.ReturnType.SpecialType);

        // The block-method symbol carries only declared parameters; the
        // receiver is added by materialization (M1 pinned fact).
        Assert.Equal(invoke.Parameters.Length, method.Parameters.Length);

        DumpDiagnostics(compilation);
        Assert.Empty(Errors(compilation));
    }

    [Fact]
    public void Probe5_MethodGroup_NonConversionContexts()
    {
        const string source = """
            using System;
            public class Widget { }
            public static class Hosts
            {
                extension(Widget w)
                {
                    public string Reply(int x) => x.ToString();
                }
            }
            public static class Driver
            {
                public static void Run(Widget w)
                {
                    var n = nameof(w.Reply);
                    object o = w.Reply;
                }
            }
            """;
        var (compilation, model, tree) = Compile(source);
        var root = tree.GetCompilationUnitRoot();

        var accesses = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.ToString() == "w.Reply")
            .ToList();
        Assert.Equal(2, accesses.Count);

        foreach (var access in accesses)
        {
            var info = model.GetSymbolInfo(access);
            _output.WriteLine(
                $"site {access.Parent!.Kind()}: symbol={info.Symbol?.ToString() ?? "<null>"}, " +
                $"reason={info.CandidateReason}, candidates=[{string.Join("; ", info.CandidateSymbols.Select(c => c.ToString()))}]");
        }

        // nameof(w.Reply): nameof accepts the method group — pin reason.
        var nameofAccess = accesses.First(a => a.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression.ToString() == "nameof"));
        var nameofInfo = model.GetSymbolInfo(nameofAccess);
        _output.WriteLine("nameof reason = " + nameofInfo.CandidateReason);

        // object o = w.Reply: CS0428-style failed conversion; candidates expose
        // the Ordinary extension-container method.
        var objectAccess = accesses.First(a => a.Parent is EqualsValueClauseSyntax);
        var objectInfo = model.GetSymbolInfo(objectAccess);
        Assert.Contains(objectInfo.CandidateSymbols, c => c is IMethodSymbol
        {
            MethodKind: MethodKind.Ordinary,
        } m && m.ContainingType.TypeKind == TypeKind.Extension);

        DumpDiagnostics(compilation);
        var errorIds = Errors(compilation).Select(e => e.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
        _output.WriteLine("error ids: " + string.Join(",", errorIds));
    }

    [Fact]
    public void Probe6_MethodGroup_StaticBlockMembers()
    {
        const string source = """
            using System;
            public class Widget { }
            public static class Hosts
            {
                extension(Widget w)
                {
                    public static string Make(int x) => x.ToString();
                }
            }
            public static class Driver
            {
                public static void Run()
                {
                    Func<int, string> f = Widget.Make;
                    var s = f(3);
                }
            }
            """;
        var (compilation, model, tree) = Compile(source);
        var root = tree.GetCompilationUnitRoot();

        var memberAccess = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .First(m => m.ToString() == "Widget.Make");
        Assert.True(model.GetSymbolInfo(memberAccess.Expression).Symbol is ITypeSymbol);
        var method = model.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal(MethodKind.Ordinary, method.MethodKind);
        Assert.Equal(TypeKind.Extension, method.ContainingType.TypeKind);

        var converted = model.GetTypeInfo(memberAccess).ConvertedType as INamedTypeSymbol;
        Assert.Equal(TypeKind.Delegate, converted!.TypeKind);

        DumpDiagnostics(compilation);
        Assert.Empty(Errors(compilation));
    }

    // ── Probe ④: expression-tree boundary ───────────────────────────────────

    [Fact]
    public void Probe7_ExpressionTree_PropertyOperatorMethod_Matrix()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            public class Widget { public int V; }
            public static class Hosts
            {
                extension(Widget w)
                {
                    public string Alias => "a";
                    public string Reply(int x) => x.ToString();
                    public static Widget operator +(Widget a, Widget b) => a;
                }
            }
            public static class Driver
            {
                public static readonly Expression<Func<Widget, string>> PropTree = w => w.Alias;
                public static readonly Expression<Func<Widget, string>> MethodTree = w => w.Reply(1);
                public static readonly Expression<Func<Widget, Widget>> OpTree = w => w + w;
            }
            """;
        var (compilation, model, tree) = Compile(source);
        var root = tree.GetCompilationUnitRoot();

        var lambdas = root.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().ToList();
        Assert.Equal(3, lambdas.Count);

        foreach (var lambda in lambdas)
        {
            var body = lambda.Body;
            var bodySymbol = model.GetSymbolInfo(body).Symbol;
            _output.WriteLine(
                $"lambda '{body}': inEt={ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(body, model)}, " +
                $"symbol={bodySymbol?.ToString() ?? "<null>"}, symbolKind={bodySymbol?.Kind.ToString() ?? "-"}, " +
                $"container={bodySymbol?.ContainingType.ToDisplayString() ?? "-"}, " +
                $"containerTypeKind={bodySymbol?.ContainingType.TypeKind.ToString() ?? "-"}");

            if (body is MemberAccessExpressionSyntax memberAccess)
            {
                _output.WriteLine($"    member converted type: {model.GetTypeInfo(memberAccess).ConvertedType}");
            }
        }

        _output.WriteLine("diagnostics:");
        DumpDiagnostics(compilation);
        var errorIds = Errors(compilation).Select(e => e.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
        _output.WriteLine("error ids: " + string.Join(",", errorIds));
    }

    // ── Probe ⑤: external assembly metadata boundary ────────────────────────

    [Fact]
    public void Probe8_ExternalAssembly_MetadataShape()
    {
        const string libSource = """
            using System;
            namespace LibNs;
            public class Widget { public int V; }
            public static class Host
            {
                extension(Widget w)
                {
                    public string Alias => "a";
                    public string Reply(int x) => x.ToString();
                    public static string Make(int x) => x.ToString();
                    public static Widget operator +(Widget a, Widget b) => a;
                }
            }
            """;

        var libTree = CSharpSyntaxTree.ParseText(
            libSource, new CSharpParseOptions(LanguageVersion.Latest), path: "Lib.cs");
        var libCompilation = CSharpCompilation.Create(
            "c49m3lib-" + Guid.NewGuid().ToString("N"),
            new[] { libTree },
            References,
            CompilationOptions);

        using var pe = new MemoryStream();
        var emit = libCompilation.Emit(pe);
        _output.WriteLine("emit success = " + emit.Success);
        foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            _output.WriteLine($"    emit diag {d.Id}: {d.GetMessage()}");
        }

        Assert.True(emit.Success, "extension-block library must emit");
        pe.Position = 0;
        var libReference = MetadataReference.CreateFromImage(pe.ToArray());

        const string consumerSource = """
            using System;
            using LibNs;
            public static class App
            {
                public static void Run(Widget w)
                {
                    var a = w.Alias;
                    var b = w.Reply(1);
                    var c = Widget.Make(1);
                    var d = w + w;
                }
            }
            """;

        var consumerTree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            consumerSource, new CSharpParseOptions(LanguageVersion.Latest), path: "App.cs");
        var consumerCompilation = CSharpCompilation.Create(
            "c49m3consumer-" + Guid.NewGuid().ToString("N"),
            new[] { consumerTree },
            References.Append(libReference),
            CompilationOptions);
        var consumerModel = consumerCompilation.GetSemanticModel(consumerTree);
        var consumerRoot = consumerTree.GetCompilationUnitRoot();

        foreach (var access in consumerRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var info = consumerModel.GetSymbolInfo(access);
            _output.WriteLine(
                $"consumer access {access}: symbol={info.Symbol?.ToString() ?? "<null>"} " +
                $"kind={info.Symbol?.Kind.ToString() ?? "-"} " +
                $"containerTypeKind={info.Symbol?.ContainingType.TypeKind.ToString() ?? "-"} " +
                $"reason={info.CandidateReason}");
        }

        foreach (var binary in consumerRoot.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            var info = consumerModel.GetSymbolInfo(binary);
            _output.WriteLine(
                $"consumer binary {binary}: symbol={info.Symbol?.ToString() ?? "<null>"} " +
                $"containerTypeKind={info.Symbol?.ContainingType.TypeKind.ToString() ?? "-"}");
        }

        _output.WriteLine("consumer diagnostics:");
        foreach (var d in consumerCompilation.GetDiagnostics().OrderBy(d => d.Location.SourceSpan.Start))
        {
            _output.WriteLine($"    diag {d.Id} [{d.Severity}] {d.GetMessage()}");
        }
    }

    // ── Probe ⑥ (env-gated): SDK block hit-rate scan ────────────────────────

    [Fact]
    public void Probe9_SdkSamples_BlockHitRate()
    {
        if (Environment.GetEnvironmentVariable("CS2J_RUN_SDK_PROBE") != "1"
            || !Directory.Exists(@"D:\code\csharp-sdk"))
        {
            _output.WriteLine("Skipped: set CS2J_RUN_SDK_PROBE=1 and ensure D:\\code\\csharp-sdk exists.");
            return;
        }

        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var samples = new[]
        {
            "json",
            "regular-expressions",
            "uri",
            "xml-tiny",
        };

        var grand = new BlockMeasurement();
        foreach (var sample in samples)
        {
            var rootPath = Path.Combine(@"D:\code\csharp-sdk", sample);
            if (!Directory.Exists(rootPath))
            {
                _output.WriteLine($"sample missing: {sample}");
                continue;
            }

            var files = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    && !f.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var parseOptions = new CSharpParseOptions(
                LanguageVersion.Latest,
                preprocessorSymbols: new[] { "NETCOREAPP" });
            var trees = files
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: f))
                .ToList();
            var compilation = CSharpCompilation.Create(
                "sdk-c49-probe-" + sample,
                trees,
                tpa,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var measurement = new BlockMeasurement { FilesParsed = files.Count };
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                var rootNode = tree.GetCompilationUnitRoot();

                foreach (var block in rootNode.DescendantNodes()
                             .OfType<ExtensionBlockDeclarationSyntax>())
                {
                    measurement.Blocks++;
                    foreach (var member in block.Members)
                    {
                        ClassifyBlockMember(member, measurement);
                    }
                }

                foreach (var invocation in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
                        {
                            MethodKind: MethodKind.Ordinary,
                        } m
                        && m.ContainingType.TypeKind == TypeKind.Extension)
                    {
                        measurement.MethodCallSites++;
                    }
                }

                foreach (var access in rootNode.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(access).Symbol is IPropertySymbol p
                        && p.ContainingType.TypeKind == TypeKind.Extension)
                    {
                        measurement.PropertySites++;
                    }
                }
            }

            _output.WriteLine($"[{sample}] {measurement}");
            Console.WriteLine($"[C49 probe][{sample}] {measurement}");
            grand.Add(measurement);
        }

        _output.WriteLine($"[TOTAL] {grand}");
        Console.WriteLine($"[C49 probe][TOTAL] {grand}");
        Assert.True(grand.FilesParsed > 0, "expected at least one parsable sdk sample");
    }

    private static void ClassifyBlockMember(SyntaxNode member, BlockMeasurement measurement)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                if (method.Modifiers.Any(SyntaxKind.StaticKeyword))
                {
                    measurement.StaticMethods++;
                }
                else
                {
                    measurement.Methods++;
                }

                break;
            case PropertyDeclarationSyntax property:
                if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
                {
                    measurement.StaticProperties++;
                }
                else
                {
                    measurement.Properties++;
                }

                break;
            case OperatorDeclarationSyntax:
                measurement.Operators++;
                break;
            case IndexerDeclarationSyntax:
                measurement.BlockIndexers++;
                break;
            default:
                measurement.OtherMembers++;
                break;
        }
    }

    private sealed class BlockMeasurement
    {
        public int FilesParsed { get; set; }
        public int Blocks { get; set; }
        public int Methods { get; set; }
        public int Properties { get; set; }
        public int Operators { get; set; }
        public int StaticMethods { get; set; }
        public int StaticProperties { get; set; }
        public int BlockIndexers { get; set; }
        public int OtherMembers { get; set; }
        public int MethodCallSites { get; set; }
        public int PropertySites { get; set; }

        public void Add(BlockMeasurement other)
        {
            FilesParsed += other.FilesParsed;
            Blocks += other.Blocks;
            Methods += other.Methods;
            Properties += other.Properties;
            Operators += other.Operators;
            StaticMethods += other.StaticMethods;
            StaticProperties += other.StaticProperties;
            BlockIndexers += other.BlockIndexers;
            OtherMembers += other.OtherMembers;
            MethodCallSites += other.MethodCallSites;
            PropertySites += other.PropertySites;
        }

        public override string ToString()
            => $"files={FilesParsed}, blocks={Blocks}, methods={Methods}, properties={Properties}, "
               + $"operators={Operators}, staticMethods={StaticMethods}, staticProperties={StaticProperties}, "
               + $"blockIndexers={BlockIndexers}, otherMembers={OtherMembers}, "
               + $"methodCallSites={MethodCallSites}, propertySites={PropertySites}";
    }
}
