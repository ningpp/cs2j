using System.Reflection;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C19 M0 probes against Roslyn 5.0.0 (Microsoft.CodeAnalysis.CSharp 5.0.x),
/// pinning facts the extension-methods design relies on (conclusions mirrored
/// in docs/superpowers/specs/2026-09-29-c19-extension-methods-design.md,
/// "M0 探针结论（2026-10-03）"):
///
/// ① r?.M() is legal and binds MethodKind.ReducedExtension (EM-COND can reuse
///    the ordinary reduced binder; tree shape is ConditionalAccess/MemberBinding).
/// ② C# 14 `extension` blocks are a fully shipping C#14 feature in Roslyn
///    5.0.0 (LanguageVersion.Latest, no version gate): ExtensionBlockDeclaration
///    nodes nest in top-level non-generic static classes; the compiler models
///    them as synthetic TypeKind.Extension containers whose members are
///    MethodKind.Ordinary, and call sites bind DIRECTLY to those Ordinary
///    methods (ReducedFrom == null) — so a ReducedExtension-keyed binder
///    silently misses them; v1 reports CS2J1206 and C49 owns the lowering.
/// ④ LinqRewriter's method table is keyed by the REDUCED display string; the
///    explicit static form (what C19 emits) misses the table and is not
///    rewritten — EM-LINQ exclusion is mandatory and LINQ reordering after C19
///    is impossible without reworking LinqRewriter.
///
/// ③ The D:\csharp-sdk measurement round is env-gated (CS2J_RUN_SDK_PROBE=1);
/// without the flag it is a passing no-op so CI stays deterministic.
/// </summary>
public sealed class ExtensionMethodProbeTests
{
    private readonly ITestOutputHelper _output;

    public ExtensionMethodProbeTests(ITestOutputHelper output) => _output = output;

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

    private static (CSharpCompilation Compilation, SemanticModel Model) Compile(string source, LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(version));
        var compilation = CSharpCompilation.Create(
            "em-probe-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    // ── Probe ① ──────────────────────────────────────────────────────────────

    [Fact]
    public void Probe1_NullConditionalExtensionCall_BindsAsReducedExtension()
    {
        const string source = """
            public static class StringExt
            {
                public static int Shout(this string s) => s.Length;
            }

            public class Program
            {
                public static int? Run() => "abc"?.Shout();
            }
            """;

        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var (compilation, model) = Compile(source, version);
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0,
                $"version={version} produced errors: {string.Join("; ", errors.Select(e => e.Id))}");

            var invocation = model.SyntaxTree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Single(n => n.Expression is MemberBindingExpressionSyntax);

            // Tree shape: ConditionalAccessExpression ("abc" ?. .Shout())
            // wrapping a WhenNotNull invocation whose expression is a MemberBinding.
            var conditional = invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Single();
            Assert.Equal("\"abc\"", conditional.Expression.ToFullString().Trim());
            Assert.IsType<MemberBindingExpressionSyntax>(invocation.Expression);

            var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            Assert.NotNull(symbol);
            Assert.Equal(MethodKind.ReducedExtension, symbol!.MethodKind);
            Assert.Equal("Shout", symbol.Name);
            Assert.NotNull(symbol.ReducedFrom);
            Assert.Equal(MethodKind.Ordinary, symbol.ReducedFrom!.MethodKind);
            Assert.Equal(MethodKind.ReducedExtension, symbol.OriginalDefinition.MethodKind);
            Assert.Equal("StringExt", symbol.ReducedFrom.ContainingType.Name);

            _output.WriteLine($"[{version}] null-conditional reduced symbol: {symbol.ToDisplayString()}");
            _output.WriteLine($"[{version}] reduced-from: {symbol.ReducedFrom.ToDisplayString()}");
        }
    }

    // ── Probe ② ──────────────────────────────────────────────────────────────

    [Fact]
    public void Probe2_Discovery()
    {
        var candidates = new (string Label, string Source)[]
        {
            ("A-named-static-only", """
                namespace N;
                public extension E
                {
                    public static void F() { }
                }
                """),
            ("B-unnamed-no-receiver-static-only", """
                namespace N;
                public extension
                {
                    public static void F() { }
                }
                """),
            ("C-unnamed-int-receiver", """
                namespace N;
                public extension(int value)
                {
                    public int Double() => value * 2;
                }
                """),
            ("D-unnamed-generic-receiver", """
                using System.Collections.Generic;
                namespace N;
                public extension<T>(List<T> items)
                {
                    public int Count2() => items.Count;
                }
                """),
            ("E-block-namespace-no-member-modifier", """
                namespace N
                {
                    public extension(int value)
                    {
                        int Double() => value * 2;
                    }
                }
                """),
            ("F-block-inside-static-class", """
                namespace N
                {
                    public static class Holder
                    {
                        extension(int value)
                        {
                            int Double() => value * 2;
                        }
                    }
                }
                """),
            ("F2-public-member", """
                namespace N
                {
                    public static class Holder
                    {
                        extension(int value)
                        {
                            public int Double() => value * 2;
                        }
                    }
                    public class Site2
                    {
                        public static int Call() => 3.Double();
                    }
                }
                """),
            ("G-file-scoped-no-member-modifier", """
                namespace N;
                public extension(int value)
                {
                    int Double() => value * 2;
                }
                """),
        };

        foreach (var (label, source) in candidates)
        {
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
            var parseDiags = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var compilation = CSharpCompilation.Create(
                "ext14-discovery-" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var compileDiags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _output.WriteLine($"--- {label} [Preview] parse: " +
                (parseDiags.Count == 0 ? "(none)" : string.Join(" | ", parseDiags.Select(d => $"{d.Id}: {d.GetMessage()}"))));
            _output.WriteLine($"    compile: " +
                (compileDiags.Count == 0 ? "(none)" : string.Join(" | ", compileDiags.Select(d => $"{d.Id}: {d.GetMessage()}"))));
        }

        Assert.True(true);
    }

    [Fact]
    public void Probe2_CSharp14ExtensionBlock_ParsesAndBindsReducedAtLatest()
    {
        // Roslyn 5.0 shape (pinned by Probe2_Discovery): extension blocks are
        // unnamed ExtensionBlockDeclaration nodes carrying a receiver
        // parameter, nested in a top-level non-generic static class; the block
        // itself takes no accessibility modifier while its members DO (default
        // class-member accessibility is private, so members need `public`).
        // The feature is fully effective at LanguageVersion.Latest (= C#14 RTM
        // in Roslyn 5.0.0): parse, compile and reduced binding all succeed
        // identically under Latest and Preview — there is no version gate.
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int value)
                    {
                        public int Double() => value * 2;
                    }
                }

                public class Site
                {
                    public static int Call() => 3.Double();
                }
            }
            """;

        // Both language versions are compared because M0 ran before the RTM
        // status was known. Result (Roslyn 5.0.0): the feature is fully
        // effective at Latest — no version gate of any kind exists.
        foreach (var version in new[] { LanguageVersion.Latest, LanguageVersion.Preview })
        {
            var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                source, new CSharpParseOptions(version));
            Assert.DoesNotContain(tree.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

            var compilation = CSharpCompilation.Create(
                "em-probe-ext14-" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var compileErrors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _output.WriteLine($"[{version}] compile diagnostics: " +
                string.Join(" | ", compileErrors.Select(d => $"{d.Id}: {d.GetMessage()}")));
            Assert.Empty(compileErrors);

            // Pin the actual SyntaxKind/SyntaxNode shape Roslyn 5.0 exposes.
            var extensionKinds = tree.GetCompilationUnitRoot().DescendantNodes()
                .Select(n => new
                {
                    Kind = ((SyntaxKind)n.RawKind).ToString(),
                    Node = n,
                })
                .Where(x => x.Kind.Contains("Extension", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.NotEmpty(extensionKinds);
            foreach (var item in extensionKinds)
            {
                _output.WriteLine($"[{version}] extension node: kind={item.Kind}, type={item.Node.GetType().FullName}");
            }

            // Symbol shape of an extension-block member and its call site —
            // the decisive C19 question. Answer in Roslyn 5.0.0: the block is a
            // synthetic TypeKind.Extension container named "Holder.extension(int)";
            // its members are MethodKind.Ordinary (IsExtensionMethod=false) and
            // the 3.Double() call site binds DIRECTLY to that Ordinary method
            // — NOT MethodKind.ReducedExtension, ReducedFrom is null. So an M1
            // binder keyed on ReducedExtension silently misses every C#14 block
            // site: CS2J1206 (Error in v1) plus residual detection must
            // classify via ContainingType.TypeKind == Extension, not MethodKind.
            var model = compilation.GetSemanticModel(tree);
            var doubleSymbol = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Select(m => model.GetDeclaredSymbol(m))
                .Single(s => s is { Name: "Double" });
            _output.WriteLine($"[{version}] Double: methodKind={doubleSymbol!.MethodKind}, " +
                $"container={doubleSymbol.ContainingType.ToDisplayString()} (typeKind={doubleSymbol.ContainingType.TypeKind}), " +
                $"isExtension={doubleSymbol.IsExtensionMethod}, reducedFrom={doubleSymbol.ReducedFrom}");
            Assert.Equal(MethodKind.Ordinary, doubleSymbol.MethodKind);
            Assert.False(doubleSymbol.IsExtensionMethod);
            Assert.Null(doubleSymbol.ReducedFrom);
            Assert.Equal(TypeKind.Extension, doubleSymbol.ContainingType.TypeKind);

            var callSite = tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Single(n => n.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Double" });
            var callSymbol = (IMethodSymbol)model.GetSymbolInfo(callSite).Symbol!;
            _output.WriteLine($"[{version}] call site 3.Double() binds: methodKind={callSymbol.MethodKind}, " +
                $"isExtension={callSymbol.IsExtensionMethod}, " +
                $"reducedFrom={callSymbol.ReducedFrom?.ToDisplayString() ?? "<null>"}, " +
                $"containerTypeKind={callSymbol.ContainingType.TypeKind}");
            Assert.Equal(MethodKind.Ordinary, callSymbol.MethodKind);
            Assert.Null(callSymbol.ReducedFrom);
            Assert.False(callSymbol.IsExtensionMethod);
            Assert.Equal(TypeKind.Extension, callSymbol.ContainingType.TypeKind);
        }
    }

    // ── Probe ④ ──────────────────────────────────────────────────────────────

    private const string LinqReducedSource = """
        using System.Linq;

        public class P
        {
            public int M()
            {
                var src = new[] { 1, 2, 3 };
                var q = src.Where(x => x > 0).Select(x => x + 1);
                return q.Count();
            }
        }
        """;

    private const string LinqStaticSource = """
        using System.Linq;

        public class P
        {
            public int M()
            {
                var src = new[] { 1, 2, 3 };
                var q = Enumerable.Select(Enumerable.Where(src, x => x > 0), x => x + 1);
                return Enumerable.Count(q);
            }
        }
        """;

    private static (LinqRewriter Rewriter, List<InvocationExpressionSyntax> LinqInvocations, SemanticModel Model)
        BuildRewriter(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "em-probe-linq-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.ToString())));
        var model = compilation.GetSemanticModel(tree);
        var invocations = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(n => n.Expression is MemberAccessExpressionSyntax)
            .Where(n => ((MemberAccessExpressionSyntax)n.Expression).Name.Identifier.Text
                is "Where" or "Select" or "Count")
            .ToList();
        return (new LinqRewriter(model, new ConversionOptions()), invocations, model);
    }

    private static string InvokeGetMethodFullName(LinqRewriter rewriter, InvocationExpressionSyntax invocation)
    {
        var method = typeof(LinqRewriter).GetMethod("GetMethodFullName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method!.Invoke(rewriter, new object[] { invocation }) ?? "<null>";
    }

    [Fact]
    public void Probe4_LinqRewriter_ReducedShapeRewrites_StaticShapeMisses()
    {
        var (reducedRewriter, reducedInvocations, reducedModel) = BuildRewriter(LinqReducedSource);
        var reducedNames = reducedInvocations
            .Select(i => InvokeGetMethodFullName(reducedRewriter, i))
            .ToList();
        _output.WriteLine("REDUCED GetMethodFullName results:");
        reducedNames.ForEach(n => _output.WriteLine("  " + n));

        // The reduced symbols key the LINQ method table (IEnumerable<T> receiver display).
        Assert.Contains(reducedNames, n => n.StartsWith("System.Collections.Generic.IEnumerable", StringComparison.Ordinal));

        var reducedRoot = reducedModel.SyntaxTree.GetCompilationUnitRoot();
        reducedRewriter.Visit(reducedRoot);
        _output.WriteLine($"REDUCED RewrittenLinqQueries={reducedRewriter.RewrittenLinqQueries}");
        Assert.True(reducedRewriter.RewrittenLinqQueries > 0,
            "reduced LINQ sugar must be recognized by LinqRewriter (baseline behavior)");

        var (staticRewriter, staticInvocations, staticModel) = BuildRewriter(LinqStaticSource);
        var staticNames = staticInvocations
            .Select(i => InvokeGetMethodFullName(staticRewriter, i))
            .ToList();
        _output.WriteLine("STATIC GetMethodFullName results:");
        staticNames.ForEach(n => _output.WriteLine("  " + n));

        // Explicit static calls resolve to the UNREDUCED definition display
        // (System.Linq.Enumerable.Where<TSource>(IEnumerable<TSource>, Func<...>)),
        // which is not an entry of LinqRewriter.KnownMethods (whose literals are
        // the reduced IEnumerable<T>.Where shapes).
        Assert.All(staticNames, n => Assert.StartsWith("System.Linq.Enumerable", n));
        Assert.DoesNotContain(staticNames, n => n.StartsWith("System.Collections.Generic.IEnumerable", StringComparison.Ordinal));

        var staticRoot = staticModel.SyntaxTree.GetCompilationUnitRoot();
        staticRewriter.Visit(staticRoot);
        _output.WriteLine($"STATIC RewrittenLinqQueries={staticRewriter.RewrittenLinqQueries}");
        Assert.Equal(0, staticRewriter.RewrittenLinqQueries);

        // Symbol-level confirmation of the shape difference.
        var reducedWhere = reducedModel.SyntaxTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(n => ((MemberAccessExpressionSyntax)n.Expression).Name.Identifier.Text == "Where");
        var reducedSymbol = (IMethodSymbol)reducedModel.GetSymbolInfo(reducedWhere.Expression).Symbol!;
        Assert.Equal(MethodKind.ReducedExtension, reducedSymbol.MethodKind);

        var staticWhere = staticModel.SyntaxTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(n => ((MemberAccessExpressionSyntax)n.Expression).Name.Identifier.Text == "Where");
        var staticSymbol = (IMethodSymbol)staticModel.GetSymbolInfo(staticWhere.Expression).Symbol!;
        Assert.Equal(MethodKind.Ordinary, staticSymbol.MethodKind);
    }

    // ── Probe ③ (env-gated D:\csharp-sdk measurement) ────────────────────────

    [Fact]
    public void Probe3_SdkSamples_MeasureReducedExtensionUsage()
    {
        if (Environment.GetEnvironmentVariable("CS2J_RUN_SDK_PROBE") != "1"
            || !Directory.Exists(@"D:\csharp-sdk"))
        {
            _output.WriteLine("Skipped: set CS2J_RUN_SDK_PROBE=1 and ensure D:\\csharp-sdk exists.");
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

        var grand = new SdkMeasurement();
        foreach (var sample in samples)
        {
            var root = Path.Combine(@"D:\csharp-sdk", sample);
            if (!Directory.Exists(root))
            {
                _output.WriteLine($"sample missing: {sample}");
                continue;
            }

            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
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
                "sdk-probe-" + sample,
                trees,
                tpa,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var measurement = new SdkMeasurement { FilesParsed = files.Count };
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                var rootNode = tree.GetCompilationUnitRoot();

                foreach (var invocation in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var info = model.GetSymbolInfo(invocation);
                    ClassifySymbol(measurement, info.Symbol, info.CandidateReason, invocation, model, inConditional: false);
                    if (info.CandidateSymbols.Length > 0 && info.Symbol is null)
                    {
                        foreach (var candidate in info.CandidateSymbols)
                        {
                            ClassifySymbol(measurement, candidate, info.CandidateReason, invocation, model, inConditional: false);
                        }
                    }

                    if (invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any()
                        && info.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
                    {
                        measurement.NullConditionalReducedSites++;
                    }
                }

                // Reduced extension method groups: member access naming an
                // extension method without invoking it.
                foreach (var access in rootNode.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (access.Parent is InvocationExpressionSyntax)
                    {
                        continue;
                    }

                    if (model.GetSymbolInfo(access).Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
                    {
                        measurement.ReducedMethodGroups++;
                    }
                }
            }

            _output.WriteLine($"[{sample}] {measurement}");
            Console.WriteLine($"[C19 probe][{sample}] {measurement}");
            grand.Add(measurement);
        }

        _output.WriteLine($"[TOTAL] {grand}");
        Console.WriteLine($"[C19 probe][TOTAL] {grand}");

        // Framework assertion: the measurement harness itself must not crash
        // and the effective sample set must not be empty.
        Assert.True(grand.FilesParsed > 0, "expected at least one parsable sdk sample");
    }

    private static void ClassifySymbol(
        SdkMeasurement m,
        ISymbol? symbol,
        CandidateReason reason,
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        bool inConditional)
    {
        if (symbol is not IMethodSymbol method)
        {
            if (reason == CandidateReason.Ambiguous)
            {
                m.AmbiguousSites++;
            }

            return;
        }

        if (method.MethodKind != MethodKind.ReducedExtension)
        {
            return;
        }

        m.ReducedExtensionCalls++;
        var host = method.ReducedFrom?.ContainingType ?? method.ContainingType;
        var hostName = host.ToDisplayString();
        if (hostName is "System.Linq.Enumerable" or "System.Linq.Queryable")
        {
            m.LinqOwnedSites++;
            return;
        }

        if (host.Locations.Length > 0 && host.Locations[0].IsInSource)
        {
            m.InCompilationHostSites++;
        }
        else
        {
            m.ExternalMetadataHostSites++;
        }
    }

    private sealed class SdkMeasurement
    {
        public int FilesParsed;
        public int ReducedExtensionCalls;
        public int LinqOwnedSites;
        public int InCompilationHostSites;
        public int ExternalMetadataHostSites;
        public int AmbiguousSites;
        public int ReducedMethodGroups;
        public int NullConditionalReducedSites;

        public void Add(SdkMeasurement other)
        {
            FilesParsed += other.FilesParsed;
            ReducedExtensionCalls += other.ReducedExtensionCalls;
            LinqOwnedSites += other.LinqOwnedSites;
            InCompilationHostSites += other.InCompilationHostSites;
            ExternalMetadataHostSites += other.ExternalMetadataHostSites;
            AmbiguousSites += other.AmbiguousSites;
            ReducedMethodGroups += other.ReducedMethodGroups;
            NullConditionalReducedSites += other.NullConditionalReducedSites;
        }

        public override string ToString()
            => $"files={FilesParsed}, reducedCalls={ReducedExtensionCalls} " +
               $"(linqOwned={LinqOwnedSites}, inCompilation={InCompilationHostSites}, " +
               $"externalMetadata={ExternalMetadataHostSites}), ambiguous={AmbiguousSites}, " +
               $"reducedMethodGroups={ReducedMethodGroups}, nullConditionalReduced={NullConditionalReducedSites}";
    }
}
