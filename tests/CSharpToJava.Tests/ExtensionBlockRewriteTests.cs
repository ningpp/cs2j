using System.Text.RegularExpressions;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.ExtensionMethodResolve.Declarations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests;

/// <summary>
/// C49 M1 rewrite tests (design §7.2, M1 subset): EMD-METHOD materializes
/// block instance extension methods as classic <c>this</c> static methods,
/// EMD-PROP materializes block instance extension properties as strict
/// <c>get_X</c>/<c>set_X</c> static method pairs. Every positive case is
/// textually asserted, recompiled clean, rebound for host identity, and run a
/// second time with zero diff. Rejection cases assert CS2J1209 / CS2J1208 /
/// CS2J1213 / CS2J1206 and unchanged files.
/// </summary>
public sealed class ExtensionBlockRewriteTests
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
        string fileName = "Test.cs")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "c49m1-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private static (ExtensionFileResult Result, ExtensionMethodResolvePreprocessor Preprocessor) RunBlockRewrite(
        CSharpCompilation compilation,
        SemanticModel model,
        ExtensionBlockOptions? blockOptions = null,
        bool chainAvailable = true)
    {
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(
            new ExtensionMethodResolveOptions(),
            blockOptions ?? new ExtensionBlockOptions { MaterializeExtensionBlocks = true },
            chainAvailable);
        return (preprocessor.RewriteFile(model), preprocessor);
    }

    private static CSharpCompilation Recompile(string code, string fileName = "Test.cs")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "c49verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten code must compile: " + string.Join("; ", errors.Select(e => e.ToString())));
        return compilation;
    }

    /// <summary>Recompile rewritten code, then assert a second pass makes no change.</summary>
    private static void AssertSecondRunStable(string code, string fileName = "Test.cs")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "c49second-" + Guid.NewGuid().ToString("N"),
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

    /// <summary>
    /// Every invocation in the rewritten tree that targets the host must be
    /// one of the expected generated member names; all expected members must
    /// actually be referenced.
    /// </summary>
    private static void AssertHostCalls(
        CSharpCompilation compilation,
        SyntaxTree tree,
        string hostDisplay,
        IReadOnlySet<string> expectedMembers)
    {
        var model = compilation.GetSemanticModel(tree);
        var found = new HashSet<string>();
        foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || method.ContainingType.ToDisplayString() != hostDisplay)
            {
                continue;
            }

            found.Add(method.Name);
        }

        Assert.Equal(expectedMembers, found);
    }

    private static void AssertPositiveCase(
        string source,
        string hostDisplay,
        IReadOnlySet<string> expectedMembers,
        params string[] compactFragments)
    {
        var (compilation, model, tree) = CompileOne(source);
        var (result, _) = RunBlockRewrite(compilation, model);

        Assert.True(result.Changed, "source should be transformed");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);

        var compact = Compact(result.OutputCode);
        foreach (var fragment in compactFragments)
        {
            if (!compact.Contains(Compact(fragment)))
            {
                Assert.Fail($"fragment not found: {fragment}\nactual output:\n{result.OutputCode}");
            }
        }

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, hostDisplay, expectedMembers);
        AssertNoExtensionSugar(verifyCompilation, verifyTree);
        AssertSecondRunStable(result.OutputCode);
    }

    // ── EMD-METHOD ──────────────────────────────────────────────────────────

    [Fact]
    public void Method_Basic_BecomesThisStaticCall()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public string Echo(string s) => s + v.ToString();
                    }
                }

                public class User
                {
                    public string Go() => 3.Echo("x");
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Echo" },
            "public static string Echo(this int v, string s)",
            "global::N.Holder.Echo(3, \"x\")");
    }

    [Fact]
    public void Method_GenericBlock_BlockTypeParameterPrepended()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            namespace N
            {
                public static class Holder
                {
                    extension<T>(List<T> xs)
                    {
                        public int Count2() => xs.Count;
                        public List<R> Map<R>(Func<T, R> f)
                        {
                            var result = new List<R>();
                            foreach (var item in xs)
                            {
                                result.Add(f(item));
                            }

                            return result;
                        }
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var xs = new List<int>();
                        int c = xs.Count2();
                        List<string> mapped = xs.Map(item => item.ToString());
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Count2", "Map" },
            "public static int Count2<T>(this global::System.Collections.Generic.List<T> xs)",
            "public static List<R> Map<T, R>(this global::System.Collections.Generic.List<T> xs, Func<T, R> f)",
            "global::N.Holder.Count2(xs)",
            "global::N.Holder.Map(xs, item => item.ToString())");
    }

    [Fact]
    public void Method_ConstraintsAndFullTypeArguments_ArePreserved()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension<T>(T v) where T : struct
                    {
                        public R Echo<R>(R r) where R : class => r;
                    }
                }

                public class User
                {
                    public string Go() => 3.Echo<int, string>("x");
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Echo" },
            "public static R Echo<T, R>(this T v, R r) where T : struct where R : class",
            "global::N.Holder.Echo<int, string>(3, \"x\")");
    }

    // ── EMD-PROP ────────────────────────────────────────────────────────────

    [Fact]
    public void Property_GetOnlyExpressionBody_BecomesGetter()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int ExtProp => v;
                    }
                }

                public class User
                {
                    public int Go() => 3.ExtProp;
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "get_ExtProp" },
            "public static int get_ExtProp(this int v) => v;",
            "global::N.Holder.get_ExtProp(3)");
    }

    [Fact]
    public void Property_GetSetBlocks_BecomeStrictMethodPair()
    {
        const string source = """
            namespace N
            {
                public class Cc
                {
                    public int _q;
                }

                public static class Holder
                {
                    extension(Cc receiver)
                    {
                        public int Q
                        {
                            get { return receiver._q; }
                            set { receiver._q = value; }
                        }
                    }
                }

                public class User
                {
                    public int Go()
                    {
                        var c = new Cc();
                        c.Q = 5;
                        return c.Q;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "get_Q", "set_Q" },
            "public static int get_Q(this global::N.Cc receiver)",
            "public static void set_Q(this global::N.Cc receiver, int value)",
            "global::N.Holder.set_Q(c, 5)",
            "global::N.Holder.get_Q(c)");
    }

    [Fact]
    public void Property_ChainedAccess_GetterStaysInChain()
    {
        const string source = """
            namespace N
            {
                public class Node
                {
                    public int B;
                }

                public static class Holder
                {
                    extension(Node v)
                    {
                        public Node Child => v;
                    }
                }

                public class User
                {
                    public int Go() => new Node().Child.B;
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "get_Child" },
            "global::N.Holder.get_Child(new Node()).B");
    }

    [Fact]
    public void Property_CompoundAndIncDec_UseReadModifyWriteRecipe()
    {
        const string source = """
            namespace N
            {
                public class Cc
                {
                    public int _q;
                }

                public static class Holder
                {
                    extension(Cc receiver)
                    {
                        public int Q
                        {
                            get { return receiver._q; }
                            set { receiver._q = value; }
                        }
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var c = new Cc();
                        c.Q += 2;
                        c.Q++;
                        ++c.Q;
                        c.Q--;
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunBlockRewrite(compilation, model);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);

        var compact = Compact(result.OutputCode);
        Assert.Contains(
            Compact("global::N.Holder.set_Q(c, global::N.Holder.get_Q(c) + 2)"),
            compact);

        var incrementCalls = Regex.Matches(
            compact,
            Regex.Escape(Compact("global::N.Holder.set_Q(c, global::N.Holder.get_Q(c) + 1")));
        Assert.Equal(2, incrementCalls.Count);

        Assert.Contains(
            Compact("global::N.Holder.set_Q(c, global::N.Holder.get_Q(c) - 1"),
            compact);

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, "N.Holder",
            new HashSet<string> { "get_Q", "set_Q" });
        AssertNoExtensionSugar(verifyCompilation, verifyTree);
        AssertSecondRunStable(result.OutputCode);
    }

    [Fact]
    public void Property_ComplexReceiver_IsHoistedIntoTemp()
    {
        const string source = """
            namespace N
            {
                public class Cc
                {
                    public int _q;
                }

                public static class Holder
                {
                    extension(Cc receiver)
                    {
                        public int Q
                        {
                            get { return receiver._q; }
                            set { receiver._q = value; }
                        }
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        GetCc().Q += 3;
                    }

                    public static Cc GetCc() => new Cc();
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunBlockRewrite(compilation, model);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);

        var compact = Compact(result.OutputCode);
        var hoist = Regex.Match(compact, @"var(_cs2j_emrecv\d)=GetCc\(\)");
        Assert.True(hoist.Success, "complex receiver must be hoisted: " + compact);
        var temp = hoist.Groups[1].Value;
        Assert.Contains(
            $"global::N.Holder.set_Q({temp},global::N.Holder.get_Q({temp})+3)",
            compact);

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, "N.Holder",
            new HashSet<string> { "get_Q", "set_Q" });
        AssertNoExtensionSugar(verifyCompilation, verifyTree);
        AssertSecondRunStable(result.OutputCode);
    }

    // ── Ordering / naming / coexistence ─────────────────────────────────────

    [Fact]
    public void MaterializedMembers_PreserveSourceOrder()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public void M1() { }
                        public int P1 => v;
                        public void M2() { }
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        3.M1();
                        int x = 3.P1;
                        3.M2();
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunBlockRewrite(compilation, model);
        Assert.True(result.Changed);

        var compact = Compact(result.OutputCode);
        int i1 = compact.IndexOf("publicstaticvoidM1(", StringComparison.Ordinal);
        int ip = compact.IndexOf("publicstaticintget_P1(", StringComparison.Ordinal);
        int i2 = compact.IndexOf("publicstaticvoidM2(", StringComparison.Ordinal);
        Assert.True(i1 >= 0 && ip > i1 && i2 > ip,
            $"materialized members must keep source order: M1={i1}, get_P1={ip}, M2={i2}");

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, "N.Holder",
            new HashSet<string> { "M1", "M2", "get_P1" });
        AssertNoExtensionSugar(verifyCompilation, verifyTree);
        AssertSecondRunStable(result.OutputCode);
    }

    [Fact]
    public void ReceiverName_IsNeverRenamed()
    {
        const string source = """
            namespace N
            {
                public class Cc
                {
                    public int _q;
                }

                public static class Holder
                {
                    extension(Cc receiver)
                    {
                        public int Get() => receiver._q;
                        public void Set(int value) { receiver._q = value; }
                    }
                }

                public class User
                {
                    public int Go()
                    {
                        var c = new Cc();
                        c.Set(c.Get());
                        return c.Get();
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Get", "Set" },
            "public static int Get(this global::N.Cc receiver)",
            "public static void Set(this global::N.Cc receiver, int value)");
    }

    [Fact]
    public void BlockMaterialization_CoexistsWithClassicExtensions()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    public static int Classic(this int v, int k) => v + k;

                    extension(int v)
                    {
                        public int Modern(int k) => v + k;
                    }
                }

                public class User
                {
                    public int Go() => 3.Classic(1) + 3.Modern(2);
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Classic", "Modern" },
            "global::N.Holder.Classic(3, 1)",
            "global::N.Holder.Modern(3, 2)");
    }

    // ── Null-conditional sites (synthetic C19 plans) ───────────────────────

    [Fact]
    public void NullConditional_MethodAndProperty_AreLowered()
    {
        const string source = """
            namespace N
            {
                public class Cc
                {
                    public int _q;
                }

                public static class Holder
                {
                    extension(Cc receiver)
                    {
                        public int Q
                        {
                            get { return receiver._q; }
                            set { receiver._q = value; }
                        }

                        public string Say(string s) => s + receiver._q.ToString();
                    }
                }

                public class User
                {
                    public void Go(bool condition)
                    {
                        Cc? r = condition ? new Cc() : null;
                        int? q = r?.Q;
                        string? s = r?.Say("x");
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var (result, _) = RunBlockRewrite(compilation, model);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);

        var compact = Compact(result.OutputCode);

        var pc = Regex.Match(compact, @"var(_cs2j_empc\d)=r");
        Assert.True(pc.Success, "property conditional receiver temp missing: " + compact);
        var pcTemp = pc.Groups[1].Value;
        Assert.Contains(
            $"{pcTemp}!=null?global::N.Holder.get_Q({pcTemp}):default(int)",
            compact);

        var mc = Regex.Match(compact, @"var(_cs2j_emmc\d)=r");
        Assert.True(mc.Success, "method conditional receiver temp missing: " + compact);
        var mcTemp = mc.Groups[1].Value;
        Assert.Contains(
            $"{mcTemp}!=null?global::N.Holder.Say({mcTemp},\"x\"):default(string)",
            compact);

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, "N.Holder",
            new HashSet<string> { "get_Q", "Say" });
        AssertNoExtensionSugar(verifyCompilation, verifyTree);
        AssertSecondRunStable(result.OutputCode);
    }

    // ── Rejection surfaces ──────────────────────────────────────────────────

    [Fact]
    public void Reject_BlockIndexer_UnconditionalCS2J1209()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int this[int i] => i;
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var (result, _) = RunBlockRewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Error && d.Code == ExtensionBlockCodes.Unsupported);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1206");
    }

    [Fact]
    public void Reject_PropertyChainMissing_CS2J1208()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int P => v;
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var (result, _) = RunBlockRewrite(compilation, model, chainAvailable: false);

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Error && d.Code == ExtensionBlockCodes.PropertyChainGate);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1206");
    }

    [Fact]
    public void ParseProbe_DefaultPasses_OldLanguageVersionFails()
    {
        Assert.True(ExtensionBlockParseProbe.Passes());
        Assert.False(ExtensionBlockParseProbe.Passes(
            ExtensionBlockParseProbe.MinimalFragment, LanguageVersion.CSharp13));
    }

    [Fact]
    public void Default_MaterializationOff_KeepsCS2J1206AndFileUnchanged()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int P => v;
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());
        var result = preprocessor.RewriteFile(model);

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Error && d.Code == "CS2J1206");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == ExtensionBlockCodes.Unsupported);
    }

    // ── Illegal source shapes (Roslyn gates before materialization) ────────

    [Fact]
    public void IllegalSource_AutoProperty_IsCS9282()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int P { get; set; }
                    }
                }
            }
            """;

        var (compilation, _, _) = CompileOne(source);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS9282");
    }

    [Fact]
    public void IllegalSource_InitAccessor_IsCS9304()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public int P { get; init; }
                    }
                }
            }
            """;

        var (compilation, _, _) = CompileOne(source);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS9304");
    }
}
