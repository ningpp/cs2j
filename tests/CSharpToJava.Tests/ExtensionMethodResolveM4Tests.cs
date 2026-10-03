using CSharpToJava.Core.Context;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests;

/// <summary>
/// M4 Core tests (design §9 closing milestone):
/// 1. EM-LINQ finalization — LinqHostSet is extend-only and mixed
///    user/LINQ fluent chains are conservatively kept as sugar (EM001);
/// 2. host force/skip overrides + additional LINQ hosts;
/// 3. strict per-code matrix (CS2J1201/1202/1203/1204/1205/1207/1210/1220);
/// 4. the pure build-failure rollback planner + the broken-rewrite injection
///    hook and byte-replay attribution;
/// 5. residual gates 1-6 including the new gate 5 (this-modifier
///    preservation) and the rolled-back-file exclusion back door;
/// 6. the LinqRewriter probe lock (M0④ extension): a pre-explicitized
///    non-member-access child stops receiver backtracking — the exact fact
///    that mandates the conservative mixed-chain skip.
/// </summary>
public sealed class ExtensionMethodResolveM4Tests
{
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

    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);

    private static string Compact(string code)
        => string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

    private static (CSharpCompilation Compilation, SemanticModel Model, SyntaxTree Tree) CompileOne(
        string source,
        string fileName = "Test.cs")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "em-m4-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private static CSharpCompilation CompileMany(
        out IReadOnlyDictionary<string, SemanticModel> models,
        params (string FileName, string Source)[] files)
    {
        var trees = files
            .Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(
                f.Source, new CSharpParseOptions(LanguageVersion.Latest), path: f.FileName))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "em-m4-" + Guid.NewGuid().ToString("N"),
            trees,
            References,
            CompilationOptions);
        models = trees.ToDictionary(t => t.FilePath, t => compilation.GetSemanticModel(t));
        return compilation;
    }

    private static CSharpCompilation Recompile(
        string code,
        string fileName = "Test.cs",
        bool assertClean = true)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "em-m4-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        if (assertClean)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.True(errors.Count == 0,
                "rewritten code must compile: " + string.Join("; ", errors.Select(e => e.ToString())));
        }

        return compilation;
    }

    private static (ExtensionFileResult Result, ExtensionMethodResolvePreprocessor Preprocessor) Rewrite(
        CSharpCompilation compilation,
        SemanticModel model,
        ExtensionMethodResolveOptions? options = null,
        Action<ExtensionMethodResolvePreprocessor>? configure = null)
    {
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options ?? new ExtensionMethodResolveOptions());
        configure?.Invoke(preprocessor);
        return (preprocessor.RewriteFile(model), preprocessor);
    }

    /// <summary>Runs the same site under default (Warning) and strict (Error) and asserts the code matrix.</summary>
    private static void AssertWarningAndError(
        string source,
        string code,
        Action<ExtensionMethodResolvePreprocessor>? configure = null)
    {
        // Note: these sources intentionally fail plain csc (CS0121/CS0452/…):
        // the binder grades the extension candidate symbols out of the failed
        // resolution, so no clean-compile assertion here.
        var (compilation, model, _) = CompileOne(source);
        var (lenient, _) = Rewrite(compilation, model, new ExtensionMethodResolveOptions
        {
            Strict = false,
            ReportInfo = false,
        }, configure);
        var lenientDiagnostic = Assert.Single(lenient.Diagnostics, d => d.Code == code);
        Assert.Equal(ExtensionSeverity.Warning, lenientDiagnostic.Severity);

        var (compilation2, model2, _) = CompileOne(source);
        var (strict, _) = Rewrite(compilation2, model2, new ExtensionMethodResolveOptions
        {
            Strict = true,
            ReportInfo = false,
        }, configure);
        var strictDiagnostic = Assert.Single(strict.Diagnostics, d => d.Code == code);
        Assert.Equal(ExtensionSeverity.Error, strictDiagnostic.Severity);
    }

    // ── 1. LinqHostSet extend-only invariant ─────────────────────────────────

    [Fact]
    public void Analyze_LinqHostSetMissingProtectedHost_Throws()
    {
        const string source = """
            using System.Linq;

            public class Program
            {
                public int Run()
                {
                    var src = new[] { 1, 2, 3 };
                    return src.Where(x => x > 0).Count();
                }
            }
            """;
        var (compilation, _, _) = CompileOne(source);
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);

        var ex = Assert.Throws<InvalidOperationException>(() => preprocessor.Analyze(
            new ExtensionMethodResolveOptions
            {
                LinqHostSet = new HashSet<string>(StringComparer.Ordinal)
                {
                    "System.Linq.Queryable", // Enumerable removed
                },
            }));
        Assert.Contains("System.Linq.Enumerable", ex.Message);
        Assert.Contains("extend-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_AdditionalLinqHostsViaOverrides_TreatedAsLinqOwned()
    {
        const string source = """
            using Custom;

            namespace Custom
            {
                public static class Linqish
                {
                    public static int[] Idle(this int[] s) => s;
                }
            }

            public class Program
            {
                public int Run()
                {
                    var src = new[] { 1, 2, 3 };
                    return src.Idle().Length;
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);

        // Without the extension: ordinary user host → rewritten.
        var (plain, _) = Rewrite(compilation, model);
        Assert.True(plain.Changed);
        Assert.Contains("global::Custom.Linqish.Idle(src)", Compact(plain.OutputCode));

        var (compilation2, model2, _) = CompileOne(source);
        var (extended, _) = Rewrite(compilation2, model2, new ExtensionMethodResolveOptions
        {
            HostOverrides = new ExtensionHostOverrides(
                additionalLinqHosts: new HashSet<string>(StringComparer.Ordinal) { "Custom.Linqish" }),
        });
        Assert.False(extended.Changed);
        Assert.Equal(0, extended.Statistics.CallsitesRewritten);
        Assert.Equal(1, extended.Statistics.LinqOwnedSkipped);
        Assert.Contains(".Idle().Length", Compact(extended.OutputCode));
    }

    [Theory]
    [InlineData(ExtensionHostOverrideAction.Skip)]
    [InlineData(ExtensionHostOverrideAction.Force)]
    public void HostOverrides_ProtectedLinqHostsAreImmutable(ExtensionHostOverrideAction action)
    {
        foreach (var protectedHost in ExtensionHostOverrides.ProtectedLinqHosts)
        {
            var ex = Assert.Throws<ArgumentException>(() => new ExtensionHostOverrides(
                new Dictionary<string, ExtensionHostOverrideAction> { [protectedHost] = action }));
            Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void HostOverrides_AdditionalLinqHostOverlappingAction_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ExtensionHostOverrides(
            hostActions: new Dictionary<string, ExtensionHostOverrideAction>
            {
                ["Custom.Linqish"] = ExtensionHostOverrideAction.Skip,
            },
            additionalLinqHosts: new HashSet<string>(StringComparer.Ordinal) { "Custom.Linqish" }));
        Assert.Contains("Custom.Linqish", ex.Message);
    }

    [Fact]
    public void HostOverrides_Empty_HasNoActions()
    {
        Assert.Null(ExtensionHostOverrides.Empty.TryGetAction("System.Linq.Enumerable"));
        Assert.Empty(ExtensionHostOverrides.Empty.HostActions);
        Assert.Empty(ExtensionHostOverrides.Empty.AdditionalLinqHosts);
    }

    // ── 2. Mixed EM-LINQ chains: conservative whole-chain skip ───────────────

    private const string MixedChainHosts = """
        using System.Linq;
        using System.Collections.Generic;

        public static class UserChain
        {
            public static IEnumerable<int> MyUser(this IEnumerable<int> source) => source;
        }
        """;

    [Fact]
    public void MixedChain_LinqBelowUserNode_WholeChainStaysSugarWithInfo()
    {
        var source = MixedChainHosts + """

            public class Program
            {
                public int Run()
                {
                    var src = new[] { 1, 2, 3 };
                    return src.Where(x => x > 0).MyUser().Count();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = Rewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.CallsitesRewritten);
        Assert.Equal(2, result.Statistics.LinqOwnedSkipped);
        Assert.Equal(1, result.Statistics.MixedLinqChainSites);
        Assert.Equal(1, result.Statistics.SitesSkippedByReason[
            ExtensionCallBinder.MixedLinqChainReason]);
        var info = Assert.Single(result.Diagnostics, d => d.Code == ExtensionMethodCodes.Info);
        Assert.Equal(ExtensionSeverity.Info, info.Severity);
        Assert.Contains("mixed-linq-chain-conservative-skip", info.Message);
        Assert.Contains("MyUser", info.Message);
        Assert.Contains(".MyUser().Count()", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void MixedChain_LinqAboveUserNode_WholeChainStaysSugar()
    {
        var source = MixedChainHosts + """

            public class Program
            {
                public int Run()
                {
                    var src = new[] { 1, 2, 3 };
                    return src.MyUser().Where(x => x > 0).Count();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = Rewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.MixedLinqChainSites);
        Assert.Equal(2, result.Statistics.LinqOwnedSkipped);
        Assert.DoesNotContain("global::UserChain.MyUser", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void MixedChain_LinqInUnrelatedArgument_DoesNotPoisonStandaloneUserCall()
    {
        const string source = """
            using System.Linq;
            using System.Collections.Generic;

            public static class UserChain
            {
                public static int MyCount(this IEnumerable<int> source) => 1;
            }

            public class Program
            {
                private static void Consume(IEnumerable<int> items) { }

                public int Run()
                {
                    var linq = new[] { 4, 5, 6 };
                    var plain = new[] { 1, 2, 3 };
                    Consume(linq.Where(x => x > 0));
                    return plain.MyCount();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = Rewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.CallsitesRewritten);
        Assert.Equal(0, result.Statistics.MixedLinqChainSites);
        Assert.Contains("global::UserChain.MyCount(plain)", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void PureUserChain_AllNodesExplicitized()
    {
        // src.A().B().Tail(): no LINQ-owned host anywhere on the fluent chain.
        const string source = """
            using System.Collections.Generic;

            public static class UserChain
            {
                public static IEnumerable<int> A(this IEnumerable<int> s) => s;
                public static IEnumerable<int> B(this IEnumerable<int> s) => s;
                public static int Tail(this IEnumerable<int> s) => 0;
            }

            public class Program
            {
                public int Run()
                {
                    var src = new[] { 1, 2, 3 };
                    return src.A().B().Tail();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = Rewrite(compilation, model);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Equal(3, result.Statistics.CallsitesRewritten);
        Assert.Equal(0, result.Statistics.MixedLinqChainSites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("global::UserChain.A(src)", compact);
        Assert.Contains("global::UserChain.B(", compact);
        Assert.Contains("global::UserChain.Tail(", compact);
        Recompile(result.OutputCode);
    }

    [Fact]
    public void MixedChain_ForceOverride_ExplicitizesUserNodeAndCounts()
    {
        var source = MixedChainHosts + """

            public class Program
            {
                public int Run()
                {
                    var src = new[] { 1, 2, 3 };
                    return src.Where(x => x > 0).MyUser().Select(x => x + 1).Count();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = Rewrite(compilation, model, new ExtensionMethodResolveOptions
        {
            HostOverrides = new ExtensionHostOverrides(
                new Dictionary<string, ExtensionHostOverrideAction>
                {
                    ["UserChain"] = ExtensionHostOverrideAction.Force,
                }),
        });

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.CallsitesRewritten);
        Assert.Equal(1, result.Statistics.HostOverrideSitesForced);
        Assert.Equal(0, result.Statistics.MixedLinqChainSites);
        // LINQ nodes stay sugar; only the user node is explicitized (escape hatch).
        Assert.Contains("global::UserChain.MyUser(src.Where(x=>x>0))", Compact(result.OutputCode));
        Assert.Contains(".Select(x=>x+1).Count()", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    // ── 3. Host skip overrides ───────────────────────────────────────────────

    [Fact]
    public void SkipOverride_PlainReducedSiteStaysSugar_WithInfoAndResidualOwnership()
    {
        const string source = """
            using SkipMe;

            namespace SkipMe
            {
                public static class SkipHost
                {
                    public static int Shout(this int x) => x;
                }
            }

            public class Program
            {
                public int Run() => 1.Shout();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, preprocessor) = Rewrite(compilation, model, new ExtensionMethodResolveOptions
        {
            HostOverrides = new ExtensionHostOverrides(
                new Dictionary<string, ExtensionHostOverrideAction>
                {
                    ["SkipMe.SkipHost"] = ExtensionHostOverrideAction.Skip,
                }),
        });

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.CallsitesRewritten);
        Assert.Equal(1, result.Statistics.HostOverrideSitesSkipped);
        Assert.Equal(1, result.Statistics.SitesSkippedByReason[
            ExtensionCallBinder.HostOverrideSkipReason]);
        var info = Assert.Single(result.Diagnostics, d => d.Code == ExtensionMethodCodes.Info);
        Assert.Contains("skipped by host overrides", info.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SkipMe.SkipHost", info.Message);
        Assert.Contains("1.Shout()", Compact(result.OutputCode));

        // The skipped site pairs with residual gate 1 as an owned exempt:
        // no "leftover sugar" residual on the byte-copied tree.
        var candidate = Recompile(result.OutputCode);
        var residuals = preprocessor.ResidualScan(candidate);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
    }

    // ── 4. Strict per-code severity matrix ───────────────────────────────────

    private const string AmbiguousSource = """
        using A;
        using B;

        namespace A
        {
            public static class E1 { public static int X(this int i) => 1; }
        }

        namespace B
        {
            public static class E2 { public static int X(this int i) => 2; }
        }

        public class Program
        {
            public int Run() => 1.X();
        }
        """;

    [Fact]
    public void Strict_1201Ambiguous_WarningToError()
        => AssertWarningAndError(AmbiguousSource, ExtensionMethodCodes.Ambiguous);

    private const string UnboundSource = """
        namespace N
        {
            public static class ConstraintExtensions
            {
                public static string NeedsClass<T>(this T value) where T : class => "";
            }

            public class Program
            {
                public string Run()
                {
                    int x = 1;
                    return x.NeedsClass();
                }
            }
        }
        """;

    [Fact]
    public void Strict_1202Unbound_WarningToError()
        => AssertWarningAndError(UnboundSource, ExtensionMethodCodes.Unbound);

    [Fact]
    public void Strict_1203ExternalHostUnknown_WarningToError()
    {
        var hostTree = CSharpSyntaxTree.ParseText("""
            namespace ExtHosts
            {
                public static class StringMetadataExtensions
                {
                    public static string ShoutMetadata(this string s) => s;
                }
            }
            """, new CSharpParseOptions(LanguageVersion.Latest));
        var hostCompilation = CSharpCompilation.Create(
            "em-m4-host-" + Guid.NewGuid().ToString("N"),
            new[] { hostTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dllPath = Path.Combine(Path.GetTempPath(), "em-m4-host-" + Guid.NewGuid().ToString("N") + ".dll");
        var emit = hostCompilation.Emit(dllPath);
        Assert.True(emit.Success, string.Join("; ",
            emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var hostReference = MetadataReference.CreateFromFile(dllPath);

        const string caller = """
            using ExtHosts;

            namespace App
            {
                public class Program
                {
                    public string Run() => "hi".ShoutMetadata();
                }
            }
            """;

        foreach (var strict in new[] { false, true })
        {
            var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                caller, new CSharpParseOptions(LanguageVersion.Latest), path: "Program.cs");
            var compilation = CSharpCompilation.Create(
                "em-m4-app-" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                References.Append(hostReference),
                CompilationOptions);
            var (result, _) = Rewrite(compilation, compilation.GetSemanticModel(tree),
                new ExtensionMethodResolveOptions
                {
                    Strict = strict,
                    ReportInfo = false,
                    ExternalHostMapper = static _ => ExternalHostMappingKind.Unknown,
                });
            var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ExtensionMethodCodes.ExternalHostNoMapping);
            Assert.Equal(strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning, diagnostic.Severity);
        }
    }

    private const string RefMismatchSource = """
        public static class Ext
        {
            public static void Bump(this ref int x) => x++;
        }

        public class Program
        {
            public int Value { get; set; }

            public void Run()
            {
                Value.Bump();
            }
        }
        """;

    [Fact]
    public void Strict_1204RefMismatch_WarningToError()
        => AssertWarningAndError(RefMismatchSource, ExtensionMethodCodes.RefMismatch);

    private const string NullConditionalSource = """
        public static class CondExt
        {
            public static Box? Go(this Box? b) => b;
        }

        public class Box { }

        public class Site
        {
            public static Box? Consume(Box? b) => b;

            public Box? Run(Box? b) => Consume(b?.Go());
        }
        """;

    [Fact]
    public void Strict_1205NullConditional_WarningToError()
        => AssertWarningAndError(NullConditionalSource, ExtensionMethodCodes.NullConditional);

    private const string MethodGroupSource = """
        public static class HostA
        {
            public static void NoOp(this string s) { }
        }

        public class Site
        {
            public void Run()
            {
                var s = "abc";
                object o = s.NoOp;
            }
        }
        """;

    [Fact]
    public void Strict_1207ReducedMethodGroup_WarningToError()
        => AssertWarningAndError(MethodGroupSource, ExtensionMethodCodes.ReducedMethodGroup);

    private const string ExpressionTreeSource = """
        using System;
        using System.Linq.Expressions;

        public static class EtExt
        {
            public static int Twice(this int x) => x * 2;
        }

        public class Program
        {
            public static Expression<Func<int, int>> Build() => x => x.Twice();
        }
        """;

    [Fact]
    public void Strict_1210ExpressionTreeIdentity_WarningToError()
        => AssertWarningAndError(
            ExpressionTreeSource,
            ExtensionMethodCodes.ExpressionTreeIdentityFailed,
            pp => pp.ForceExpressionTreeMismatch = (_, _) => true);

    // ── 3b. Interpolation hole: `global::` at the hole's left edge is a
    // Roslyn parse error (CS0103 "global"); explicitization must parenthesize
    // exactly those sites. Found by Probe6 on D:\csharp-sdk\json
    // (valueSequence.ToArray() inside an interpolated assertion). ──────────

    [Theory]
    // left edge: bare / postfix chains / binary left / unary prefix / alignment / format
    [InlineData("$\"{x.Tick()}\"", "(global::Hole.Hole.Tick(x))", true)]
    [InlineData("$\"{x.Tick().ToString()}\"", "(global::Hole.Hole.Tick(x)).ToString()", true)]
    [InlineData("$\"{x.Tick().ToString()[0]}\"", "(global::Hole.Hole.Tick(x)).ToString()[0]", true)]
    [InlineData("$\"{x.Tick() + 1}\"", "(global::Hole.Hole.Tick(x))+1", true)]
    [InlineData("$\"{-x.Tick()}\"", "-(global::Hole.Hole.Tick(x))", true)]
    [InlineData("$\"{x.Tick() == 1}\"", "(global::Hole.Hole.Tick(x))==1", true)]
    [InlineData("$\"{x.Tick(),5}\"", "(global::Hole.Hole.Tick(x)),5", true)]
    [InlineData("$\"{x.Tick():x}\"", "(global::Hole.Hole.Tick(x)):x", true)]
    // not at the left edge: argument / conditional arm
    [InlineData("$\"{string.Concat(x.Tick())}\"", "string.Concat(global::Hole.Hole.Tick(x))", false)]
    [InlineData("$\"{(x == 1 ? x.Tick() : 0)}\"", "?global::Hole.Hole.Tick(x):0", false)]
    public void Rewrite_InterpolationHoleLeftEdge_ParenthesizedAndCompiles(
        string expression, string expectedCompact, bool wrapped)
    {
        var source = $$"""
            using System;

            namespace Hole
            {
                public static class Hole
                {
                    public static int Tick(this int x) => x;
                }

                public class Program
                {
                    public string Run(int x) => {{expression}};
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = Rewrite(compilation, model);
        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains(expectedCompact, compact);

        Recompile(result.OutputCode);
    }

    [Fact]
    public void Rewrite_PlainStatementCall_NotParenthesized()
    {
        const string source = """
            using System;

            namespace Hole2
            {
                public static class Hole2
                {
                    public static int Tick(this int x) => x;
                }

                public class Program
                {
                    public int Run(int x) => x.Tick();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = Rewrite(compilation, model);
        Assert.Contains("=>global::Hole2.Hole2.Tick(x);", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void Strict_PolicyTable_IsComplete()
    {
        string[] escalated =
        {
            ExtensionMethodCodes.Ambiguous,
            ExtensionMethodCodes.Unbound,
            ExtensionMethodCodes.ExternalHostNoMapping,
            ExtensionMethodCodes.RefMismatch,
            ExtensionMethodCodes.NullConditional,
            ExtensionMethodCodes.ReducedMethodGroup,
            ExtensionMethodCodes.ExpressionTreeIdentityFailed,
            ExtensionMethodCodes.ResidualFound,
        };
        Assert.Equal(escalated.OrderBy(x => x, StringComparer.Ordinal),
            ExtensionMethodResolvePolicy.StrictEscalatedCodes.OrderBy(x => x, StringComparer.Ordinal));

        string[] neverEscalated =
        {
            ExtensionMethodCodes.CSharp14ExtensionBlock, // CS2J1206 always Error anyway
            ExtensionMethodCodes.RestoreOrBuildFailed,
            ExtensionMethodCodes.TestsFailed,
            ExtensionMethodCodes.SingleFileIncompleteBinding, // CS2J1290 permanent Warning
        };
        Assert.All(neverEscalated, code => Assert.False(ExtensionMethodResolvePolicy.IsStrictEscalated(code)));

        Assert.Equal(ExtensionSeverity.Warning,
            ExtensionMethodResolvePolicy.Severity(ExtensionMethodCodes.RefMismatch, strict: false));
        Assert.Equal(ExtensionSeverity.Error,
            ExtensionMethodResolvePolicy.Severity(ExtensionMethodCodes.RefMismatch, strict: true));
        Assert.Equal(ExtensionSeverity.Warning,
            ExtensionMethodResolvePolicy.Severity(ExtensionMethodCodes.SingleFileIncompleteBinding, strict: true));

        Assert.Equal(ExtensionSeverity.Warning, ExtensionMethodResolvePolicy.ResidualSeverity(false));
        Assert.Equal(ExtensionSeverity.Error, ExtensionMethodResolvePolicy.ResidualSeverity(true));
    }

    // ── 5. Build-failure rollback planner (pure) ─────────────────────────────

    private static ExtensionRewriteErrorKey Key(string file, string id, string message)
        => new(file, id, message);

    [Fact]
    public void Rollback_NoNewErrors_Accepts()
    {
        var baseline = new[] { Key("A.cs", "CS0001", "old") };
        var candidate = new[] { Key("A.cs", "CS0001", "old") };
        var plan = ExtensionRewriteRollback.Plan(baseline, candidate, new[] { "A.cs" });
        Assert.Equal(ExtensionRollbackVerdict.NoNewErrors, plan.Verdict);
        Assert.Empty(plan.NewErrors);
        Assert.Empty(plan.FilesToRevert);
    }

    [Fact]
    public void Rollback_AllNewErrorsInOneRewrittenFile_ExcludesThatFile()
    {
        var candidate = new[]
        {
            Key("Inter/A.cs", "CS0246", "broken probe 1"),
            Key("Inter/A.cs", "CS0246", "broken probe 2"),
        };
        var plan = ExtensionRewriteRollback.Plan(
            Array.Empty<ExtensionRewriteErrorKey>(),
            candidate,
            new[] { "Inter/A.cs", "Inter/B.cs" });
        Assert.Equal(ExtensionRollbackVerdict.ExcludeFiles, plan.Verdict);
        Assert.Equal(new[] { "Inter/A.cs" }, plan.FilesToRevert);
        Assert.Equal(2, plan.NewErrors.Count);
    }

    [Fact]
    public void Rollback_ErrorsSpreadOverTwoFiles_WholeTree()
    {
        var candidate = new[]
        {
            Key("Inter/A.cs", "CS0246", "x"),
            Key("Inter/B.cs", "CS0246", "y"),
        };
        var plan = ExtensionRewriteRollback.Plan(
            Array.Empty<ExtensionRewriteErrorKey>(),
            candidate,
            new[] { "Inter/A.cs", "Inter/B.cs" });
        Assert.Equal(ExtensionRollbackVerdict.WholeTreeRollback, plan.Verdict);
        Assert.Empty(plan.FilesToRevert);
    }

    [Fact]
    public void Rollback_UnlocatedOrNonRewrittenFile_WholeTree()
    {
        var unlocated = ExtensionRewriteRollback.Plan(
            Array.Empty<ExtensionRewriteErrorKey>(),
            new[] { Key("", "CS0001", "nowhere") },
            new[] { "A.cs" });
        Assert.Equal(ExtensionRollbackVerdict.WholeTreeRollback, unlocated.Verdict);

        var foreign = ExtensionRewriteRollback.Plan(
            Array.Empty<ExtensionRewriteErrorKey>(),
            new[] { Key("Other/Generated.cs", "CS0001", "not ours") },
            new[] { "A.cs" });
        Assert.Equal(ExtensionRollbackVerdict.WholeTreeRollback, foreign.Verdict);
    }

    [Fact]
    public void Rollback_PathCompareIsCaseInsensitive()
    {
        var plan = ExtensionRewriteRollback.Plan(
            Array.Empty<ExtensionRewriteErrorKey>(),
            new[] { Key("inter/a.cs", "CS0246", "x") },
            new[] { "INTER/A.CS" });
        Assert.Equal(ExtensionRollbackVerdict.ExcludeFiles, plan.Verdict);
    }

    // ── 6. Broken-rewrite hook + byte-replay drill (Core level) ──────────────

    private const string DrillSourceA = """
        public static class AExt
        {
            public static int GoA(this int x) => x;
        }

        public class PA
        {
            public int Run() => 1.GoA();
        }
        """;

    private const string DrillSourceB = """
        public static class BExt
        {
            public static int GoB(this int x) => x;
        }

        public class PB
        {
            public int Run() => 2.GoB();
        }
        """;

    [Fact]
    public void BrokenRewriteHook_OneFileBreaks_PlannerExcludesFileAndReplayCompiles()
    {
        var compilation = CompileMany(out var models, ("A.cs", DrillSourceA), ("B.cs", DrillSourceB));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());
        preprocessor.ForceBrokenRewrite = (_, symbol) =>
            symbol.ContainingType.Name == "AExt";

        var resultA = preprocessor.RewriteFile(models["A.cs"]);
        var resultB = preprocessor.RewriteFile(models["B.cs"]);

        Assert.True(resultA.Changed);
        Assert.True(resultB.Changed);
        Assert.Contains("__CS2J_M4_ROLLBACK_PROBE__", resultA.OutputCode);
        Assert.DoesNotContain("__CS2J_M4_ROLLBACK_PROBE__", resultB.OutputCode);
        Assert.Equal(new[] { "A.cs", "B.cs" },
            preprocessor.RewrittenFiles.OrderBy(p => p, StringComparer.Ordinal).ToArray());

        // Candidate tree: both rewritten files.
        var candidateTrees = new[]
        {
            (SyntaxTree)CSharpSyntaxTree.ParseText(resultA.OutputCode,
                new CSharpParseOptions(LanguageVersion.Latest), path: "A.cs"),
            (SyntaxTree)CSharpSyntaxTree.ParseText(resultB.OutputCode,
                new CSharpParseOptions(LanguageVersion.Latest), path: "B.cs"),
        };
        var candidate = CSharpCompilation.Create(
            "em-m4-cand-" + Guid.NewGuid().ToString("N"), candidateTrees, References, CompilationOptions);
        var newErrors = candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => new ExtensionRewriteErrorKey(
                d.Location.SourceTree?.FilePath ?? string.Empty,
                d.Id,
                d.GetMessage() ?? string.Empty))
            .ToList();
        Assert.Contains(newErrors, e => e.File == "A.cs");
        Assert.DoesNotContain(newErrors, e => e.File == "B.cs");

        var plan = ExtensionRewriteRollback.Plan(
            Array.Empty<ExtensionRewriteErrorKey>(), newErrors, preprocessor.RewrittenFiles);
        Assert.Equal(ExtensionRollbackVerdict.ExcludeFiles, plan.Verdict);
        Assert.Equal(new[] { "A.cs" }, plan.FilesToRevert);

        // Byte replay of A: the reverted tree is exactly the original input.
        var replayedTree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            DrillSourceA, new CSharpParseOptions(LanguageVersion.Latest), path: "A.cs");
        var replayed = CSharpCompilation.Create(
            "em-m4-replay-" + Guid.NewGuid().ToString("N"),
            new SyntaxTree[] { replayedTree, candidateTrees[1] },
            References, CompilationOptions);
        Assert.Empty(replayed.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        // Byte-identical replay (the adapter uses File.Copy; at Core level the
        // content contract is exact original text).
        Assert.Equal(DrillSourceA.ReplaceLineEndings(), replayedTree.GetText().ToString().ReplaceLineEndings());
    }

    // ── 7. Residual gates 1-6 + gate 5 preservation + exclusion back door ────

    private static ExtensionMethodResolvePreprocessor AnalyzeOnly(
        CSharpCompilation compilation,
        ExtensionMethodResolveOptions? options = null)
    {
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options ?? new ExtensionMethodResolveOptions());
        return preprocessor;
    }

    [Fact]
    public void Residual_CleanFullRewrite_SixGatesEmpty_ThisBaselineCounted()
    {
        const string source = """
            public static class Host
            {
                public static void NoOp(this string s) { }
            }

            public class Site
            {
                public void Run()
                {
                    var s = "a";
                    s.NoOp();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, preprocessor) = Rewrite(compilation, model);
        Assert.True(result.Changed);
        Assert.Equal(1, preprocessor.ThisModifierBaseline);

        var candidate = Recompile(result.OutputCode);
        var residuals = preprocessor.ResidualScan(candidate);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedMethodGroup, residuals.Keys);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRewriteIdentity, residuals.Keys);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleUnresolvedStaticHost, residuals.Keys);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleExpressionTreeShape, residuals.Keys);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleThisModifierPreserved, residuals.Keys);
    }

    [Fact]
    public void Residual_Gate1_RemainingReducedInvocation()
    {
        const string source = """
            public static class E
            {
                public static int Go(this int x) => x;
            }

            public class P
            {
                public int R() => 1.Go();
            }
            """;
        var (compilation, _, _) = CompileOne(source);
        var preprocessor = AnalyzeOnly(compilation); // no RewriteFile → no keys, no exempts
        var residuals = preprocessor.ResidualScan(compilation);
        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleRemainingReducedInvocation]);
    }

    [Fact]
    public void Residual_Gate2_RemainingReducedMethodGroup()
    {
        const string source = """
            public static class HostA
            {
                public static void NoOp(this string s) { }
            }

            public class Site
            {
                public void R()
                {
                    var s = "abc";
                    object o = s.NoOp;
                }
            }
            """;
        var (compilation, _, _) = CompileOne(source);
        var preprocessor = AnalyzeOnly(compilation);
        var residuals = preprocessor.ResidualScan(compilation);
        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleRemainingReducedMethodGroup]);
    }

    [Fact]
    public void Residual_Gate3_RewrittenIdentityMissing()
    {
        const string source = """
            public static class Host
            {
                public static void NoOp(this string s) { }
                public static void Other(string s) { }
            }

            public class Site
            {
                public void Run()
                {
                    var s = "a";
                    s.NoOp();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, preprocessor) = Rewrite(compilation, model);
        var mutated = result.OutputCode.Replace("global::Host.NoOp(s)", "global::Host.Other(s)");
        var candidate = Recompile(mutated);
        var residuals = preprocessor.ResidualScan(candidate);
        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleRewriteIdentity]);
    }

    [Fact]
    public void Residual_Gate4_ReducedUserCallInsideExpressionTree()
    {
        const string source = """
            using System.Linq;

            public static class EtExt
            {
                public static bool Keep(this int x) => true;
            }

            public class P
            {
                public IQueryable<int> Q(IQueryable<int> q) => q.Where(x => x.Keep());
            }
            """;
        var (compilation, _, _) = CompileOne(source);
        var preprocessor = AnalyzeOnly(compilation);
        var residuals = preprocessor.ResidualScan(compilation);
        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleExpressionTreeShape]);
    }

    [Fact]
    public void Residual_Gate6_UnresolvedGlobalStaticHost()
    {
        const string source = """
            class P
            {
                void M()
                {
                    global::Missing.NamespaceHost.Plain();
                }
            }
            """;
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: "Broken.cs");
        var compilation = CSharpCompilation.Create(
            "em-m4-g6-" + Guid.NewGuid().ToString("N"),
            new[] { tree }, References, CompilationOptions);
        var preprocessor = AnalyzeOnly(compilation);
        var residuals = preprocessor.ResidualScan(compilation);
        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleUnresolvedStaticHost]);
    }

    [Fact]
    public void Residual_Gate5_MissingThisModifier_ReportedAsDelta()
    {
        const string baselineSource = """
            public static class E
            {
                public static int A(this int x) => x;
                public static int B(this int x) => x;
            }
            """;
        var (baseline, _, _) = CompileOne(baselineSource, "Decls.cs");
        var preprocessor = AnalyzeOnly(baseline);
        Assert.Equal(2, preprocessor.ThisModifierBaseline);

        const string candidateSource = """
            public static class E
            {
                public static int A(this int x) => x;
                public static int B(int x) => x;
            }
            """;
        var candidate = Recompile(candidateSource, "Decls.cs", assertClean: false);
        var residuals = preprocessor.ResidualScan(candidate);
        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleThisModifierPreserved]);
    }

    [Fact]
    public void Residual_ExcludedFile_OwnsGate1AndGate5Share()
    {
        const string sourceA = """
            public static class EA
            {
                public static int Go(this int x) => x;
            }

            public class PA
            {
                public int R() => 1.Go();
            }
            """;
        const string sourceB = """
            public static class EB
            {
                public static int To(this int x) => x;
            }

            public class PB
            {
                public int R() => 2.To();
            }
            """;
        var compilation = CompileMany(out _, ("A.cs", sourceA), ("B.cs", sourceB));
        var preprocessor = AnalyzeOnly(compilation);
        Assert.Equal(2, preprocessor.ThisModifierBaseline);

        var residuals = preprocessor.ResidualScan(compilation, new[] { "B.cs" });
        // Only A's sugar is "unowned" — B was reverted byte-for-byte.
        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleRemainingReducedInvocation]);
        // Baseline 2 − B share 1 == preserved (A) 1 → no gate 5 delta.
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleThisModifierPreserved, residuals.Keys);
    }

    // ── 8. LinqRewriter probe lock (M0④ extension, read-only observation) ────

    private static (string Output, LinqRewriter Rewriter) RunLinqRewriter(string source)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "em-m4-linqprobe-" + Guid.NewGuid().ToString("N"),
            new[] { tree }, References, CompilationOptions);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var model = compilation.GetSemanticModel(tree);
        var rewriter = new LinqRewriter(model, new ConversionOptions());
        return (rewriter.Visit(tree.GetRoot()).ToFullString(), rewriter);
    }

    [Fact]
    public void LinqRewriter_PureLinqChain_ReachesDownstreamSelect()
    {
        const string source = """
            using System.Linq;

            public class P
            {
                public int R()
                {
                    var s = new[] { 1, 2, 3 };
                    return s.Where(x => x > 1).Select(x => x + 1).Count();
                }
            }
            """;
        var (output, rewriter) = RunLinqRewriter(source);
        // The downstream Select is consumed by the LINQ rewriter (procedural form).
        Assert.True(rewriter.RewrittenLinqQueries > 0,
            $"pure reduced chain must be rewritten; output={Compact(output)}");
        Assert.DoesNotContain(".Select(x=>x+1)", Compact(output));
    }

    [Fact]
    public void LinqRewriter_PreExplicitizedMixedChain_ProbeLocksObservedDecomposition()
    {
        // M0④ extension (design M4 §1 evidence lock). The conservative
        // whole-chain skip was finalized before this probe existed; this lock
        // records what LinqRewriter ACTUALLY does with a pre-explicitized user
        // node, so an M5 review of the policy has hard evidence:
        //
        // Observed (Roslyn 5.0.0 / LinqRewriter read-only): in both mixed-chain
        // orientations the procedural DECOMPOSITION (helper count, helper
        // bodies, LINQ query extraction) is identical between the fully
        // reduced input and the C19-explicitized input — the ONLY difference
        // is the explicitized token itself (`global::UserChain.MyUser(...)` vs
        // `.MyUser()`). No structural break was reproduced on these shapes;
        // the skip therefore stays a DEFENSIVE default (unknown shapes in the
        // 1801-site SDK population), reversible per host with a force override.
        const string middleExplicit = """
            using System.Linq;
            using System.Collections.Generic;

            public static class UserChain
            {
                public static IEnumerable<int> MyUser(this IEnumerable<int> source) => source;
            }

            public class P
            {
                public int R()
                {
                    var s = new[] { 1, 2, 3 };
                    return global::UserChain.MyUser(s.Where(x => x > 1)).Select(x => x + 1).Count();
                }
            }
            """;
        const string middleReduced = """
            using System.Linq;
            using System.Collections.Generic;

            public static class UserChain
            {
                public static IEnumerable<int> MyUser(this IEnumerable<int> source) => source;
            }

            public class P
            {
                public int R()
                {
                    var s = new[] { 1, 2, 3 };
                    return s.Where(x => x > 1).MyUser().Select(x => x + 1).Count();
                }
            }
            """;
        const string bottomExplicit = """
            using System.Linq;
            using System.Collections.Generic;

            public static class UserChain
            {
                public static IEnumerable<int> MyUser(this IEnumerable<int> source) => source;
            }

            public class Q
            {
                public int R()
                {
                    var s = new[] { 1, 2, 3 };
                    return global::UserChain.MyUser(s).Where(x => x > 1).Select(x => x + 1).Count();
                }
            }
            """;
        const string bottomReduced = """
            using System.Linq;
            using System.Collections.Generic;

            public static class UserChain
            {
                public static IEnumerable<int> MyUser(this IEnumerable<int> source) => source;
            }

            public class Q
            {
                public int R()
                {
                    var s = new[] { 1, 2, 3 };
                    return s.MyUser().Where(x => x > 1).Select(x => x + 1).Count();
                }
            }
            """;

        var (midExOut, midExRewriter) = RunLinqRewriter(middleExplicit);
        var (midRedOut, midRedRewriter) = RunLinqRewriter(middleReduced);
        var (botExOut, botExRewriter) = RunLinqRewriter(bottomExplicit);
        var (botRedOut, botRedRewriter) = RunLinqRewriter(bottomReduced);

        // Same number of procedural LINQ queries extracted in each pair.
        Assert.Equal(midRedRewriter.RewrittenLinqQueries, midExRewriter.RewrittenLinqQueries);
        Assert.Equal(botRedRewriter.RewrittenLinqQueries, botExRewriter.RewrittenLinqQueries);
        Assert.True(midExRewriter.RewrittenLinqQueries >= 1);
        Assert.True(botExRewriter.RewrittenLinqQueries >= 1);

        // After normalizing the one intentional token difference, the outputs
        // are character-identical (helpers + query bodies unchanged).
        static string NormalizeUserNode(string code)
        {
            var compact = Compact(code);
            compact = compact.Replace("global::UserChain.MyUser(", "__USER__(");
            // Reduced tail `X.MyUser()` where X is a name/chain or one call
            // with at most one level of nested argument calls → __USER__(X).
            var receiver = @"[A-Za-z_][\w.]*(?:\((?:[^()]|\([^()]*\))*\))?";
            compact = System.Text.RegularExpressions.Regex.Replace(
                compact,
                "(" + receiver + @")\.MyUser\(\)",
                m => "__USER__(" + m.Groups[1].Value + ")");
            return compact;
        }
        Assert.Equal(NormalizeUserNode(midRedOut), NormalizeUserNode(midExOut));
        Assert.Equal(NormalizeUserNode(botRedOut), NormalizeUserNode(botExOut));
    }
}
