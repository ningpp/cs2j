using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.ExtensionMethodResolve.Declarations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests;

/// <summary>
/// C49 M3 tests (design §15 / decision 15):
/// 1. Null-conditional block sites — extended read chains
///    (<c>r?.P.B</c>, <c>r?.M().P</c>), statement writes
///    (<c>r?.P = v</c>) and compound writes (<c>r?.P += v</c>);
///    <c>r?.P++/--</c> is Roslyn CS1059 and gets a paired diagnostic.
/// 2. Block method-group delegate conversion (instance direct capture,
///    complex-receiver temp capture, static no-receiver); mismatched
///    signatures retain the sugar with a paired diagnostic.
/// 3. Expression-tree boundary: property/operator sites inside
///    <c>Expression&lt;&gt;</c> are skipped with paired diagnostics;
///    method sites are rewritten and identity-asserted.
/// 4. External-assembly extension members are report-only (CS2J1203);
///    C49 never synthesizes companion declarations from names.
/// 5. Multiple blocks / partial hosts coexist; strict escalates deferred
///    Warnings to Errors.
///
/// Every positive case recompiles clean and is stable on a second run.
/// </summary>
public sealed class ExtensionBlockM3Tests
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
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);

    private static string Compact(string code)
        => string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

    private static (CSharpCompilation Compilation, SemanticModel Model, SyntaxTree Tree) CompileOne(
        string source,
        string assemblyPrefix = "c49m3",
        IReadOnlyCollection<MetadataReference>? extraReferences = null)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: "Test.cs");
        var references = extraReferences is null
            ? References
            : References.Concat(extraReferences).ToArray();
        var compilation = CSharpCompilation.Create(
            assemblyPrefix + "-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            CompilationOptions);
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private static ExtensionFileResult RunBlockRewrite(
        CSharpCompilation compilation,
        SemanticModel model,
        ExtensionBlockOptions? blockOptions = null,
        ExtensionMethodResolveOptions? resolveOptions = null)
    {
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(
            resolveOptions ?? new ExtensionMethodResolveOptions(),
            blockOptions ?? new ExtensionBlockOptions { MaterializeExtensionBlocks = true },
            memberDesugarChainAvailable: true);
        return preprocessor.RewriteFile(model);
    }

    private static CSharpCompilation Recompile(string code, IReadOnlyCollection<MetadataReference>? extraReferences = null)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest));
        var references = extraReferences is null
            ? References
            : References.Concat(extraReferences).ToArray();
        var compilation = CSharpCompilation.Create(
            "c49m3verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            CompilationOptions);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten code must compile: " + string.Join("; ", errors.Select(e => e.ToString())));
        return compilation;
    }

    private static void AssertSecondRunStable(string code)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c49m3second-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        var model = compilation.GetSemanticModel(tree);

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(
            new ExtensionMethodResolveOptions(),
            new ExtensionBlockOptions { MaterializeExtensionBlocks = true },
            memberDesugarChainAvailable: true);
        var second = preprocessor.RewriteFile(model);

        if (second.Changed)
        {
            Assert.Fail(
                "a second run over materialized code must report Changed=false\n"
                + "diagnostics: " + string.Join("; ", second.Diagnostics.Select(d => d.Code + ":" + d.Message))
                + "\noutput:\n" + second.OutputCode);
        }

        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), second.OutputCode);
    }

    private static void AssertNoExtensionSugar(CSharpCompilation compilation, SyntaxTree tree)
    {
        var root = tree.GetCompilationUnitRoot();
        Assert.False(root.DescendantNodes().Any(n => n.IsKind(SyntaxKind.ExtensionBlockDeclaration)));

        foreach (var node in root.DescendantNodes()
                     .OfType<ExpressionSyntax>()
                     .Where(n => n.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                                 || n.IsKind(SyntaxKind.MemberBindingExpression)))
        {
            var symbol = compilation.GetSemanticModel(tree).GetSymbolInfo(node).Symbol;
            var container = symbol switch
            {
                IPropertySymbol property => property.ContainingType,
                IMethodSymbol method => method.ContainingType,
                _ => null,
            };

            Assert.NotEqual(TypeKind.Extension, container?.TypeKind);
        }
    }

    private static void AssertPositiveCase(
        string source,
        params string[] compactFragments)
    {
        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        Assert.True(result.Changed, "source should be transformed");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);

        var compact = Compact(result.OutputCode);
        foreach (var fragment in compactFragments)
        {
            Assert.True(compact.Contains(Compact(fragment)),
                $"fragment not found: {fragment}\n"
                + "diagnostics: " + string.Join("; ", result.Diagnostics.Select(d => d.Code + ":" + d.Message))
                + "\nactual output:\n" + result.OutputCode);
        }

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertNoExtensionSugar(verifyCompilation, verifyTree);
        AssertSecondRunStable(result.OutputCode);
    }

    // ── 1. Null-conditional read chains ────────────────────────────────────

    [Fact]
    public void NullCond_PropertyThenClassicProperty_ChainLowered()
    {
        const string source = """
            namespace N
            {
                public class Widget { public int Core; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Alias
                        {
                            get { return w.Core.ToString(); }
                        }
                    }
                }

                public class User
                {
                    public void Go(Widget? r)
                    {
                        int len = r?.Alias.Length;
                    }
                }
            }
            """;

        AssertPositiveCase(source,
            "global::N.Holder.get_Alias(_cs2j_empc1).Length");
    }

    [Fact]
    public void NullCond_MethodThenProperty_ChainLowered()
    {
        const string source = """
            namespace N
            {
                public class Widget { public int Core; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Reply(int x) => x.ToString() + w.Core.ToString();
                    }
                }

                public class User
                {
                    public void Go(Widget? r)
                    {
                        int len = r?.Reply(1).Length;
                    }
                }
            }
            """;

        AssertPositiveCase(source,
            "global::N.Holder.Reply(_cs2j_emmc1, 1).Length");
    }

    // ── 1b. Null-conditional statement writes ──────────────────────────────

    [Fact]
    public void NullCond_SimpleAndCompoundWrites_AreLowered()
    {
        const string source = """
            namespace N
            {
                public class Widget
                {
                    public string Name = "";
                }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Alias
                        {
                            get { return w.Name; }
                            set { w.Name = value; }
                        }
                    }
                }

                public class User
                {
                    public void Go(Widget? r)
                    {
                        r?.Alias = "x";
                        r?.Alias += "y";
                    }
                }
            }
            """;

        AssertPositiveCase(source,
            "global::N.Holder.set_Alias(_cs2j_empc1, \"x\")",
            "global::N.Holder.set_Alias(_cs2j_empc2, global::N.Holder.get_Alias(_cs2j_empc2) + \"y\")");
    }

    // ── 1c. r?.P++ is CS1059: sugar retained, paired diagnostic ────────────

    [Fact]
    public void NullCond_Increment_KeepsSugar_WithPairedDiagnostic()
    {
        const string source = """
            namespace N
            {
                public class Widget { public int Count; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public int Count
                        {
                            get { return w.Count; }
                            set { w.Count = value; }
                        }
                    }
                }

                public class User
                {
                    public void Go(Widget? r)
                    {
                        r?.Count++;
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        // The block declaration is still materialized; only the CS1059
        // site keeps its sugar with a paired Warning.
        Assert.True(result.Changed);
        Assert.Contains("r?.Count++", Compact(result.OutputCode));

        var pairing = Assert.Single(result.Diagnostics
            .Where(d => d.Code == ExtensionMethodCodes.NullConditional));
        Assert.Equal(ExtensionSeverity.Warning, pairing.Severity);
    }

    // ── 2. Block method-group delegate conversion ──────────────────────────

    [Fact]
    public void MethodGroup_Instance_SimpleReceiver_DirectCapture()
    {
        const string source = """
            using System;
            namespace N
            {
                public class Widget { public int Core; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Reply(int x) => x.ToString() + w.Core.ToString();
                    }
                }

                public class User
                {
                    public void Go(Widget w)
                    {
                        Func<int, string> f = w.Reply;
                        var s = f(3);
                    }
                }
            }
            """;

        AssertPositiveCase(source,
            "Func<int, string> f = (int _cs2j_emp1) => "
            + "global::N.Holder.Reply(w, _cs2j_emp1)");
    }

    [Fact]
    public void MethodGroup_Instance_ComplexReceiver_TempCapture()
    {
        const string source = """
            using System;
            namespace N
            {
                public class Widget { public int Core; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Reply(int x) => x.ToString() + w.Core.ToString();
                    }
                }

                public class User
                {
                    public static Widget Make() => new Widget();

                    public void Go()
                    {
                        Func<int, string> f = Make().Reply;
                        var s = f(3);
                    }
                }
            }
            """;

        AssertPositiveCase(source,
            "global::N.Holder.Reply(_cs2j_emgr1, _cs2j_emp2)");
    }

    [Fact]
    public void MethodGroup_Static_TypeQualified_NoReceiver()
    {
        const string source = """
            using System;
            namespace N
            {
                public class Widget { }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public static string Make(int x) => x.ToString();
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        Func<int, string> f = Widget.Make;
                        var s = f(3);
                    }
                }
            }
            """;

        AssertPositiveCase(source,
            "Func<int, string> f = (int _cs2j_emp1) => "
            + "global::N.Holder.Make(_cs2j_emp1)");
    }

    [Fact]
    public void MethodGroup_SignatureMismatch_KeepsSugar_WithPairedWarning()
    {
        const string source = """
            using System;
            namespace N
            {
                public class Widget { public int Core; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Reply(int x) => x.ToString() + w.Core.ToString();
                    }
                }

                public class User
                {
                    public void Go(Widget w)
                    {
                        Func<long, string> f = w.Reply;
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        // Atomic block materialization: when a site keeps its sugar
        // (HasDeferredSites), the whole block is retained too, so the
        // file is unchanged and the mismatched site pairs with a
        // Warning.
        Assert.False(result.Changed);
        Assert.Contains("Func<long,string>f=w.Reply;", Compact(result.OutputCode));

        var pairing = Assert.Single(result.Diagnostics
            .Where(d => d.Code == ExtensionMethodCodes.ReducedMethodGroup));
        Assert.Equal(ExtensionSeverity.Warning, pairing.Severity);
    }

    // ── 3. Expression-tree boundary ────────────────────────────────────────

    [Fact]
    public void ExpressionTree_PropertyAndOperator_AreSkipped_WithPairedWarnings()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            namespace N
            {
                public class Widget { public int V; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Alias => "a";
                        public static Widget operator +(Widget a, Widget b) => a;
                    }
                }

                public class Driver
                {
                    public static readonly Expression<Func<Widget, string>> PropTree = w => w.Alias;
                    public static readonly Expression<Func<Widget, Widget>> OpTree = w => w + w;
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);

        var skipDiagnostics = result.Diagnostics
            .Where(d => d.Severity == ExtensionSeverity.Warning
                        && d.Code == ExtensionMethodCodes.ReducedMethodGroup)
            .ToList();
        Assert.Equal(2, skipDiagnostics.Count);
    }

    [Fact]
    public void ExpressionTree_MethodCall_Rewritten_AndIdentityAsserted()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;
            namespace N
            {
                public class Widget { public int V; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Reply(int x) => x.ToString();
                    }
                }

                public class Driver
                {
                    public static readonly Expression<Func<Widget, string>> MethodTree = w => w.Reply(1);
                }
            }
            """;

        AssertPositiveCase(source,
            "Expression<Func<Widget, string>> MethodTree = w => global::N.Holder.Reply(w, 1)");
    }

    // ── 4. External-assembly metadata boundary ─────────────────────────────

    [Fact]
    public void ExternalAssembly_ExtensionSites_ReportedOnly_NoSynthesis()
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
        Assert.True(emit.Success, "extension-block library must emit: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
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

        var (compilation, model, tree) = CompileOne(
            consumerSource, "c49m3consumer", new[] { libReference });
        var result = RunBlockRewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);

        var externalDiagnostics = result.Diagnostics
            .Where(d => d.Code == ExtensionBlockCodes.ExternalMetadata)
            .ToList();
        Assert.Equal(4, externalDiagnostics.Count);
        Assert.All(externalDiagnostics, d => Assert.Equal(ExtensionSeverity.Warning, d.Severity));
    }

    // ── 5. Multiple blocks / partial hosts / strict ────────────────────────

    [Fact]
    public void MultipleBlocks_AcrossPartialAndSeparateHosts_AllLowered()
    {
        const string source = """
            namespace N
            {
                public class Widget { public int Core; }
                public class Gadget { public int Core; }

                public static partial class FirstHost
                {
                    extension(Widget w)
                    {
                        public int P
                        {
                            get { return w.Core; }
                        }
                    }
                }

                public static partial class FirstHost
                {
                    extension(Gadget g)
                    {
                        public int Q
                        {
                            get { return g.Core; }
                        }
                    }
                }

                public static class SecondHost
                {
                    extension(Widget w)
                    {
                        public string Say(string s) => s + w.Core.ToString();
                    }
                }

                public class User
                {
                    public void Go(Widget w, Gadget g)
                    {
                        var p = w.P;
                        var q = g.Q;
                        var s = w.Say("x");
                    }
                }
            }
            """;

        AssertPositiveCase(source,
            "global::N.FirstHost.get_P(",
            "global::N.FirstHost.get_Q(",
            "global::N.SecondHost.Say(");
    }

    [Fact]
    public void StrictMode_MismatchedMethodGroup_BecomesError()
    {
        const string source = """
            using System;
            namespace N
            {
                public class Widget { public int Core; }

                public static class Holder
                {
                    extension(Widget w)
                    {
                        public string Reply(int x) => x.ToString() + w.Core.ToString();
                    }
                }

                public class User
                {
                    public void Go(Widget w)
                    {
                        Func<long, string> f = w.Reply;
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var result = RunBlockRewrite(
            compilation,
            model,
            new ExtensionBlockOptions
            {
                MaterializeExtensionBlocks = true,
                Strict = true,
            },
            new ExtensionMethodResolveOptions { Strict = true });

        var pairing = Assert.Single(result.Diagnostics
            .Where(d => d.Code == ExtensionMethodCodes.ReducedMethodGroup));
        Assert.Equal(ExtensionSeverity.Error, pairing.Severity);
    }
}
