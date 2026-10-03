using CSharpToJava.Core.ExtensionMethodResolve;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// M1 Core tests for extension-method explicitness (design §7.1):
/// EM-BIND/EM-CALL/EM-GEN/EM-GEN-HOST/EM-CHAIN/EM-IDEAL/EM-LINQ/EM-EXT14 plus
/// residual gates 1/3/6. Every rewrite is asserted textually, recompiled, and
/// rebound.
/// </summary>
public sealed class ExtensionMethodResolveM1Tests
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
        string fileName = "Test.cs",
        LanguageVersion version = LanguageVersion.Latest)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(version), path: fileName);
        var compilation = CSharpCompilation.Create(
            "em-m1-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private static (CSharpCompilation Compilation, IReadOnlyDictionary<string, SemanticModel> Models) CompileMany(
        params (string FileName, string Source)[] files)
    {
        var trees = files
            .Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(
                f.Source, new CSharpParseOptions(LanguageVersion.Latest), path: f.FileName))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "em-m1-" + Guid.NewGuid().ToString("N"),
            trees,
            References,
            CompilationOptions);
        return (compilation, trees.ToDictionary(t => t.FilePath, t => compilation.GetSemanticModel(t)));
    }

    private static (ExtensionFileResult Result, ExtensionMethodResolvePreprocessor Preprocessor) RunRewrite(
        CSharpCompilation compilation,
        SemanticModel model,
        ExtensionMethodResolveOptions? options = null)
    {
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options ?? new ExtensionMethodResolveOptions());
        return (preprocessor.RewriteFile(model), preprocessor);
    }

    private static CSharpCompilation Recompile(
        string code,
        string fileName = "Test.cs",
        bool assertClean = true)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "em-verify-" + Guid.NewGuid().ToString("N"),
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

    private static IMethodSymbol FindStaticCall(CSharpCompilation compilation, string methodName)
    {
        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(n => n.Expression is MemberAccessExpressionSyntax ma
                && ma.Name switch
                {
                    GenericNameSyntax generic => generic.Identifier.Text == methodName,
                    _ => ma.Name.ToString() == methodName,
                });
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);
        return symbol;
    }

    // ── EM-CALL basics ──────────────────────────────────────────────────────

    [Fact]
    public void SameNamespace_BasicRewrite_ToGlobalQualifiedStaticCall()
    {
        const string source = """
            namespace N
            {
                public static class StringExtensions
                {
                    public static string Reverse(this string s) => s;
                }

                public class Program
                {
                    public string Run() => "abc".Reverse();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.CallsitesRewritten);
        Assert.Equal(1, result.Statistics.InCompilationHostSites);
        Assert.Equal(0, result.Statistics.ExternalHostSites);
        Assert.Contains("global::N.StringExtensions.Reverse(\"abc\")", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var symbol = FindStaticCall(rewritten, "Reverse");
        Assert.Equal(MethodKind.Ordinary, symbol.MethodKind);
        Assert.True(symbol.IsExtensionMethod);
        Assert.Null(symbol.ReducedFrom);
        Assert.Equal("N.StringExtensions", symbol.ContainingType.ToDisplayString());
    }

    [Fact]
    public void MultipleArguments_AllCarriedAfterReceiver()
    {
        const string source = """
            public static class Ext
            {
                public static string Concat(this string a, string b, string c) => a + b + c;
            }

            public class Program
            {
                public string Run() => "x".Concat("y", "z");
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::Ext.Concat(\"x\",\"y\",\"z\")", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void UsingImport_BringsExtensionIntoScope()
    {
        const string source = """
            using N;

            namespace N
            {
                public static class Ext
                {
                    public static int F(this int i) => i;
                }
            }

            namespace M
            {
                public class Program
                {
                    public int Run() => 1.F();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::N.Ext.F(1)", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void UsingTypeAlias_StaticFormStaysUntouched_SugarStillRewritten()
    {
        const string source = """
            using N;
            using A = N.Ext;

            namespace N
            {
                public static class Ext
                {
                    public static int F(this int i) => i;
                }
            }

            public class Program
            {
                public int Run() => A.F(1) + 2.F();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // The alias-qualified static call is ordinary (EM-IDEAL): untouched.
        Assert.Contains("A.F(1)", compact);
        // The sugar is explicitized.
        Assert.Contains("global::N.Ext.F(2)", compact);
        Recompile(result.OutputCode);
    }

    [Fact]
    public void UsingNamespaceAlias_StaticFormIsNotSelected()
    {
        const string source = """
            using Q = N;

            namespace N
            {
                public static class Ext
                {
                    public static int F(this int i) => i;
                }
            }

            public class Program
            {
                public int Run() => Q.Ext.F(1);
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        // Ordinary static call through a namespace alias is idempotent.
        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.CallsitesRewritten);
    }

    [Fact]
    public void GlobalUsing_SynthesizedTree_BringsExtensionIntoScope()
    {
        var (compilation, models) = CompileMany(
            ("g.cs", "global using N;"),
            ("lib.cs", """
                namespace N
                {
                    public static class Ext
                    {
                        public static int F(this int i) => i;
                    }
                }
                """),
            ("app.cs", """
                public class Program
                {
                    public int Run() => 1.F();
                }
                """));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());
        var result = preprocessor.RewriteFile(models["app.cs"]);

        Assert.True(result.Changed);
        Assert.Contains("global::N.Ext.F(1)", Compact(result.OutputCode));

        // Recompile the rewritten app tree together with its sibling trees
        // (lib defines N; g.cs carries the global using).
        var verify = CompileMany(
            ("g.cs", "global using N;"),
            ("lib.cs", """
                namespace N
                {
                    public static class Ext
                    {
                        public static int F(this int i) => i;
                    }
                }
                """),
            ("app.cs", result.OutputCode));
        Assert.Empty(verify.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void NestedNamespace_OuterNamespaceExtensionsVisible()
    {
        const string source = """
            namespace Outer
            {
                public static class Ext
                {
                    public static int F(this int i) => i;
                }

                namespace Inner
                {
                    public class Program
                    {
                        public int Run() => 1.F();
                    }
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::Outer.Ext.F(1)", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void ThisReceiver_InsideNestedType_RewritesToGlobalTopLevelHost()
    {
        // Pre-C#14 extension classes must be TOP-LEVEL static classes (CS1109
        // bans nested extension classes). The legal "outer/inner" shape is
        // therefore a top-level host plus a deeply nested call site using the
        // 'this' receiver.
        const string source = """
            public static class OuterEntityExt
            {
                public static int F(this Outer.Entity entity) => 1;
            }

            public class Outer
            {
                public class Entity
                {
                    public int Run() => this.F();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::OuterEntityExt.F(this)", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    // ── EM-BIND ambiguity / unbound ─────────────────────────────────────────

    [Fact]
    public void TwoNamespacesSameSignature_Ambiguous_Reports1201_AndLeavesSite()
    {
        const string source = """
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
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ExtensionMethodCodes.Ambiguous);
        Assert.Equal(ExtensionSeverity.Warning, diagnostic.Severity);
        Assert.Contains("A.E1", diagnostic.Message);
        Assert.Contains("B.E2", diagnostic.Message);
        Assert.Contains("namespace A", diagnostic.Message);
        Assert.Contains("namespace B", diagnostic.Message);
        Assert.Equal(1, result.Statistics.SitesSkippedByReason[ExtensionMethodCodes.Ambiguous]);
    }

    [Fact]
    public void Ambiguous_UnderStrict_IsError()
    {
        const string source = """
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
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model, new ExtensionMethodResolveOptions { Strict = true });

        Assert.All(result.Diagnostics.Where(d => d.Code == ExtensionMethodCodes.Ambiguous),
            d => Assert.Equal(ExtensionSeverity.Error, d.Severity));
    }

    // ── EM-GEN / EM-GEN-HOST ─────────────────────────────────────────────────

    [Fact]
    public void GenericMethod_TypeArgumentsOmitted_ReInferredAfterRewrite()
    {
        const string source = """
            public static class Gen
            {
                public static T Shuffle<T>(this T value) => value;
            }

            public class Program
            {
                public string Run() => "x".Shuffle();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // Omitted type arguments stay omitted at the CALL SITE (the declaration
        // still reads Shuffle<T>, which is why the check is anchored to the
        // rewritten invocation, not a whole-text search).
        Assert.Contains("global::Gen.Shuffle(\"x\")", compact);
        Assert.DoesNotContain("global::Gen.Shuffle<", compact);
        var rewritten = Recompile(result.OutputCode);
        var symbol = FindStaticCall(rewritten, "Shuffle");
        Assert.Equal("string", symbol.TypeArguments[0].ToDisplayString());
    }

    [Fact]
    public void GenericMethod_ExplicitTypeArguments_PreservedVerbatim()
    {
        const string source = """
            public static class Gen
            {
                public static T Shuffle<T>(this T value) => value;
            }

            public class Program
            {
                public string Run() => "x".Shuffle<string>();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::Gen.Shuffle<string>(\"x\")", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void GenericMethod_MultipleTypeArguments_Preserved()
    {
        const string source = """
            public static class Gen
            {
                public static V Map<T, V>(this T first, V second) => second;
            }

            public class Program
            {
                public int Run() => "s".Map<string, int>(2);
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::Gen.Map<string,int>(\"s\",2)", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void GenericInterfaceReceiver_IEnumerableOfT()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class Coll
            {
                public static int CountIt<T>(this IEnumerable<T> source) => source.Count();
            }

            public class Program
            {
                public int Run() => new List<int> { 1, 2 }.CountIt();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::Coll.CountIt(newList<int>{1,2})", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void CSharp14ExtensionBlock_InsideGenericClass_SyntaxGateStillFires1206()
    {
        // The ONLY way a "nested generic host" (Outer<T>.Ext) can legally
        // exist is a C#14 extension block nested in a generic class. The
        // syntax-only EM-EXT14 gate must catch that shape too: the reduced
        // selector would otherwise miss it because block members bind
        // Ordinary with ReducedFrom == null.
        const string source = """
            namespace N
            {
                public class Outer<T>
                {
                    extension(Outer<T> outer)
                    {
                        public U Get<U>(U value) => value;
                    }
                }

                public class Site
                {
                    public int Call() => new Outer<string>().Get(5);
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            d => d.Code == ExtensionMethodCodes.CSharp14ExtensionBlock);
        Assert.Equal(ExtensionSeverity.Error, diagnostic.Severity);
        Assert.Equal(1, result.Statistics.SitesSkippedByReason[ExtensionMethodCodes.CSharp14ExtensionBlock]);
        Assert.Equal(0, result.Statistics.CallsitesRewritten);
    }

    // ── EM-CHAIN ─────────────────────────────────────────────────────────────

    [Fact]
    public void AllExtensionChain_BottomUpConverges()
    {
        const string source = """
            public static class Chain
            {
                public static int A(this int i) => i;
                public static int B(this int i) => i;
                public static int C(this int i) => i;
            }

            public class Program
            {
                public int Run() => 1.A().B().C();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.CallsitesRewritten);
        Assert.Contains(
            "global::Chain.C(global::Chain.B(global::Chain.A(1)))",
            Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    [Fact]
    public void MixedChain_OnlyReducedNodesChange_InstanceAndPropertyPreserved()
    {
        const string source = """
            public class Box
            {
                public Box Inner() => this;
                public int Value => 42;
            }

            public static class BoxExt
            {
                public static Box Ext(this Box box) => box;
            }

            public class Program
            {
                public int Run() => new Box().Inner().Ext().Value;
                public int Run2() => new Box().Ext().Inner().Value;
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.CallsitesRewritten);
        var compact = Compact(result.OutputCode);
        // Extension surrounded by instance method + property.
        Assert.Contains("global::BoxExt.Ext(newBox().Inner()).Value", compact);
        // Extension return value followed by an instance call.
        Assert.Contains("global::BoxExt.Ext(newBox()).Inner().Value", compact);
        Recompile(result.OutputCode);
    }

    // ── Instance/extension priority ─────────────────────────────────────────

    [Fact]
    public void ApplicableInstanceMethodSameName_Wins_SiteUntouched()
    {
        const string source = """
            public class C
            {
                public int M() => 1;
            }

            public static class CExt
            {
                public static int M(this C c) => 2;
            }

            public class Program
            {
                public int Run() => new C().M();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.CallsitesRewritten);
        Assert.Equal(0, result.Statistics.CallsitesBoundReduced);
    }

    [Fact]
    public void InapplicableInstanceMethod_FallsBackToExtension_AndRewrites()
    {
        const string source = """
            public class C
            {
                public int M() => 1;
            }

            public static class CExt
            {
                public static int M(this C c, int x) => x;
            }

            public class Program
            {
                public int Run() => new C().M(7);
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains("global::CExt.M(newC(),7)", Compact(result.OutputCode));
        Recompile(result.OutputCode);
    }

    // ── EM-LINQ ──────────────────────────────────────────────────────────────

    [Fact]
    public void LinqEnumerableAndQueryableSites_ZeroRewrite_CountedAsOwned()
    {
        const string source = """
            using System.Linq;

            public class Program
            {
                public int Run()
                {
                    var src = new[] { 1, 2, 3 };
                    return src.Where(x => x > 0).Select(x => x + 1).Count()
                        + src.AsQueryable().Where(x => x > 0).Count();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.CallsitesRewritten);
        // Where/Select/Count (Enumerable) + AsQueryable/Where/Count (Queryable).
        Assert.Equal(6, result.Statistics.LinqOwnedSkipped);
    }

    [Fact]
    public void UserExtensionInsideLinqChain_OnlyUserNodeRewritten()
    {
        const string source = """
            using System.Linq;
            using System.Collections.Generic;

            public static class UserChain
            {
                public static IEnumerable<int> MyUser(this IEnumerable<int> source) => source;
            }

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
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.CallsitesRewritten);
        Assert.Equal(3, result.Statistics.LinqOwnedSkipped);
        var compact = Compact(result.OutputCode);
        Assert.Contains("global::UserChain.MyUser(src.Where(x=>x>0))", compact);
        // LINQ sugar stays reduced around the explicitized user node.
        Assert.Contains(".Select(x=>x+1).Count()", compact);
        Recompile(result.OutputCode);
    }

    // ── EM-EXT14 ────────────────────────────────────────────────────────────

    [Fact]
    public void CSharp14ExtensionBlock_SyntaxOnlySample_Reports1206Error()
    {
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
        var (compilation, model, _) = CompileOne(source);
        // Latest: block parses/compiles/binds with zero errors — the gate must
        // be purely syntax-driven.
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, _) = RunRewrite(compilation, model);

        Assert.False(result.Changed);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ExtensionMethodCodes.CSharp14ExtensionBlock);
        Assert.Equal(ExtensionSeverity.Error, diagnostic.Severity);
        Assert.Equal(ExtensionRule.Ext14, diagnostic.Rule);
        Assert.Contains("C49", diagnostic.Message);
        Assert.Equal(1, result.Statistics.SitesSkippedByReason[ExtensionMethodCodes.CSharp14ExtensionBlock]);
    }

    // ── EM-IDEAL idempotence ─────────────────────────────────────────────────

    [Fact]
    public void SecondRun_IsByteStable_AndSelectsNothing()
    {
        const string source = """
            public static class Ext
            {
                public static int F(this int i) => i;
            }

            public class Program
            {
                public int Run() => 1.F().F();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (first, _) = RunRewrite(compilation, model);
        Assert.True(first.Changed);

        var (secondCompilation, secondModel, _) = CompileOne(first.OutputCode);
        Assert.Empty(secondCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (second, _) = RunRewrite(secondCompilation, secondModel);

        Assert.False(second.Changed);
        Assert.Equal(0, second.Statistics.CallsitesRewritten);
        Assert.Equal(0, second.Statistics.CallsitesBoundReduced);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    // ── Residual gates 1/3/6 ─────────────────────────────────────────────────

    [Fact]
    public void Residual_FullRewrite_AllGatesEmpty()
    {
        const string source = """
            public static class Ext
            {
                public static int F(this int i) => i;
            }

            public class Program
            {
                public int Run() => 1.F();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (result, preprocessor) = RunRewrite(compilation, model);
        var rewritten = Recompile(result.OutputCode);

        var residuals = preprocessor.ResidualScan(rewritten);

        Assert.Empty(residuals);
    }

    [Fact]
    public void Residual_Gate1_RemainingReducedCall_LinqExempt()
    {
        const string source = """
            using System.Linq;

            public static class Ext
            {
                public static int F(this int i) => i;
            }

            public class Program
            {
                public int Run() => 1.F() + new[] { 1 }.Count();
            }
            """;
        var (compilation, _, _) = CompileOne(source);
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());

        // Simulating "nothing rewritten": F residual counts, LINQ Count does not.
        var residuals = ExtensionMethodResidualScanner.Scan(
            compilation,
            Array.Empty<ExtensionRewrittenSiteKey>(),
            new ExtensionMethodResolveOptions());

        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleRemainingReducedInvocation]);
    }

    [Fact]
    public void Residual_Gate6_UnresolvableGlobalHost_Counts()
    {
        const string source = """
            public class Program
            {
                public int Run() => global::NoSuchNamespace.NoSuchHost.M(1);
            }
            """;
        var (compilation, _, _) = CompileOne(source);
        var residuals = ExtensionMethodResidualScanner.Scan(
            compilation,
            Array.Empty<ExtensionRewrittenSiteKey>(),
            new ExtensionMethodResolveOptions());

        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleUnresolvedStaticHost]);
    }

    [Fact]
    public void Residual_Gate3_ReboundDefinitionMismatch_Counts()
    {
        const string source = """
            namespace N
            {
                public static class StringExtensions
                {
                    public static string Reverse(this string s) => s;
                }

                public static class Wrong
                {
                    public static int Ping(int value) => value;
                }

                public class Program
                {
                    public string Run() => "abc".Reverse();
                }
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        var (_, preprocessor) = RunRewrite(compilation, model);

        // The rewrite key expects Reverse(string); the intermediate tree
        // instead binds a different host/signature → identity drift.
        const string drifted = """
            namespace N
            {
                public static class StringExtensions
                {
                    public static string Reverse(this string s) => s;
                }

                public static class Wrong
                {
                    public static int Ping(int value) => value;
                }

                public class Program
                {
                    public string Run() => global::N.Wrong.Ping(1).ToString();
                }
            }
            """;
        var driftedCompilation = Recompile(drifted, assertClean: false);
        var residuals = preprocessor.ResidualScan(driftedCompilation);

        Assert.Equal(1, residuals[ExtensionMethodResidualScanner.RuleRewriteIdentity]);
    }

    [Fact]
    public void NullConditionalReducedSite_M2_LiftedToSingleEvaluationTemps()
    {
        const string source = """
            public static class Ext
            {
                public static int? Shout(this string s) => s.Length;
            }

            public class Program
            {
                public int? Run() => "abc"?.Shout();
            }
            """;
        var (compilation, model, _) = CompileOne(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var (result, preprocessor) = RunRewrite(compilation, model);

        // M2 lifts the chain: receiver temp + null-guarded static call.
        Assert.True(result.Changed,
            string.Join("\n", result.Diagnostics) + "\n---\n" + result.OutputCode
            + "\nstats lifted=" + result.Statistics.ConditionalChainsLifted
            + " rewritten=" + result.Statistics.CallsitesRewritten);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == ExtensionMethodCodes.NullConditional);
        Assert.Equal(1, result.Statistics.ConditionalChainsLifted);
        Assert.Equal(0, result.Statistics.ConditionalChainsDeferred);
        Assert.Equal(1, result.Statistics.CallsitesRewritten);
        Assert.Contains("_cs2j_emc", result.OutputCode);
        Assert.Contains("global::Ext.Shout", Compact(result.OutputCode));
        Assert.Contains("default(int?)", Compact(result.OutputCode));

        var rewritten = Recompile(result.OutputCode);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
    }
}
