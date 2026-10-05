using CSharpToJava.Core.ThreadStaticRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class ThreadStaticRewriteTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.Thread).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.ThreadLocal<int>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException).Assembly.Location),
    };

    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);

    private static CSharpCompilation Compile(params (string Name, string Source)[] files)
        => CSharpCompilation.Create(
            "ts-test",
            files.Select(f => CSharpSyntaxTree.ParseText(
                f.Source,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: f.Name)).ToArray(),
            References,
            CompilationOptions);

    private static ThreadStaticFileResult RunOne(
        string source,
        ThreadStaticOptions? options = null,
        string fileName = "Test.cs")
    {
        var compilation = Compile((fileName, source));
        var preprocessor = new ThreadStaticPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options);
        return preprocessor.RewriteFile(compilation.GetSemanticModel(compilation.SyntaxTrees[0]));
    }

    private static (ThreadStaticFileResult[] Results, CSharpCompilation Compilation) RunMany(
        params (string Name, string Source)[] files)
    {
        var compilation = Compile(files);
        var preprocessor = new ThreadStaticPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ThreadStaticOptions());
        var results = compilation.SyntaxTrees
            .OrderBy(t => t.FilePath, StringComparer.Ordinal)
            .Select(t => preprocessor.RewriteFile(compilation.GetSemanticModel(t)))
            .ToArray();
        return (results, compilation);
    }

    private static void AssertCompiles(params string[] sources)
    {
        var trees = sources.Select((s, i) => CSharpSyntaxTree.ParseText(
            s,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: $"Verify{i}.cs")).ToArray();
        var compilation = CSharpCompilation.Create("ts-verify", trees, References, CompilationOptions);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    // ---- TS-1..TS-4: basic static field, no initializer ----

    [Fact]
    public void StaticField_NoInitializer_DeclarationAndAllSiteShapesRewritten()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] private static int _counter;

                public void M(int v)
                {
                    var a = _counter;
                    _counter = v;
                    _counter += 1;
                    _counter++;
                    --_counter;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains(
            "private static readonly global::System.Threading.ThreadLocal<int> _counter = new global::System.Threading.ThreadLocal<int>();",
            result.OutputCode);
        Assert.DoesNotContain("[ThreadStatic]", result.OutputCode);
        Assert.Contains("var a = _counter.Value;", result.OutputCode);
        Assert.Contains("_counter.Value = v;", result.OutputCode);
        Assert.Contains("_counter.Value += 1;", result.OutputCode);
        Assert.Contains("_counter.Value++;", result.OutputCode);
        Assert.Contains("--_counter.Value;", result.OutputCode);
        Assert.Equal(1, result.Statistics.FieldsRewritten);
        Assert.Equal(1, result.Statistics.ReadsRewritten);
        Assert.Equal(1, result.Statistics.WritesRewritten);
        Assert.Equal(1, result.Statistics.CompoundSitesRewritten);
        Assert.Equal(2, result.Statistics.IncrementSitesRewritten);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void QualifiedAndGlobalQualifiedAccess_IsRewritten()
    {
        var source = """
            using System;

            namespace Ns
            {
                class C
                {
                    [ThreadStatic] static int f;

                    static int Get() => C.f + global::Ns.C.f;

                    static void Set(int v)
                    {
                        C.f = v;
                        global::Ns.C.f += 1;
                    }
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("=> C.f.Value + global::Ns.C.f.Value;", result.OutputCode);
        Assert.Contains("C.f.Value = v;", result.OutputCode);
        Assert.Contains("global::Ns.C.f.Value += 1;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-1: inline initializer (EveryThread) ----

    [Fact]
    public void InlineInitializer_EveryThread_FactoryFormWithExpressionVerbatim()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f = Compute() + 1;

                static int Compute() => 41;

                static int Get() => f;
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains(
            "= new global::System.Threading.ThreadLocal<int>(() => Compute() + 1);",
            result.OutputCode);
        Assert.Contains("static int Get() => f.Value;", result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.InitializerNormalized);
        Assert.Equal(1, result.Statistics.InitializersNormalized);
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-10: explicit static constructor ----

    [Fact]
    public void ExplicitStaticConstructor_AssignmentBecomesValueWrite()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;

                static C()
                {
                    f = 42;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("f.Value = 42;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-1: multi-variable split ----

    [Fact]
    public void MultipleVariables_AreSplitIntoHolderDeclarations()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int a, b;

                static void M()
                {
                    a = 1;
                    b = 2;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.DoesNotContain("static int a, b", result.OutputCode);
        Assert.Contains(
            "static readonly global::System.Threading.ThreadLocal<int> a = new global::System.Threading.ThreadLocal<int>();",
            result.OutputCode);
        Assert.Contains(
            "static readonly global::System.Threading.ThreadLocal<int> b = new global::System.Threading.ThreadLocal<int>();",
            result.OutputCode);
        Assert.Equal(2, result.OutputCode.Split("new global::System.Threading.ThreadLocal<int>()").Length - 1);
        Assert.Contains("a.Value = 1;", result.OutputCode);
        Assert.Contains("b.Value = 2;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-1: readonly / attributes / doc / trivia ----

    [Fact]
    public void ReadOnlyModifier_OtherAttributes_XmlDoc_Trivia_ArePreserved()
    {
        var source = """
            using System;

            class C
            {
                /// <summary>keep-doc</summary>
                [Obsolete("keep-attr")]
                [ThreadStatic]
                public static string? name; // keep-trivia
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("keep-doc", result.OutputCode);
        Assert.Contains("[Obsolete(\"keep-attr\")]", result.OutputCode);
        Assert.Contains("keep-trivia", result.OutputCode);
        Assert.Contains("public static readonly global::System.Threading.ThreadLocal<string?> name",
            result.OutputCode);
        Assert.DoesNotContain("ThreadStatic", result.OutputCode.Replace("ThreadLocal", string.Empty));
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-11: generic container ----

    [Fact]
    public void GenericContainer_FieldReferencingTypeParameter_SkippedWith1204_OutputIdentical()
    {
        var source = """
            using System;

            class Box<T> where T : class
            {
                [ThreadStatic] static T? latest;

                static T? Get() => latest;
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.GenericContainerUnsupported
                && d.Severity == ThreadStaticSeverity.Warning);
    }

    [Fact]
    public void GenericContainer_FieldNotReferencingTypeParameter_SkippedWith1204()
    {
        var source = """
            using System;

            class Box<T>
            {
                [ThreadStatic] static int count;

                static int Get() => count;
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.GenericContainerUnsupported);
    }

    // ---- TS-9 / TS-12: instance / const / volatile ----

    [Fact]
    public void InstanceField_SkippedWith1201()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] int f;

                void M() { f = 1; }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.UnsupportedAttributePosition && d.Rule == ThreadStaticRule.Instance);
    }

    [Fact]
    public void ConstField_SkippedWith1201()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] const int f = 1;
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.UnsupportedAttributePosition && d.Rule == ThreadStaticRule.Declaration);
    }

    [Fact]
    public void VolatileField_SkippedWith1201()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static volatile int f;
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.UnsupportedAttributePosition);
    }

    // ---- TS-6: ref / out / & ----

    [Fact]
    public void RefArgument_1202_AtomicNoSiteTouched()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;

                static void Take(ref int x) { x = 1; }

                static void M()
                {
                    Take(ref f);
                    var a = f;
                }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.InfeasibleSite && d.Rule == ThreadStaticRule.RefOut);
        Assert.DoesNotContain(".Value", result.OutputCode);
    }

    [Fact]
    public void OutArgument_1202_AtomicNoSiteTouched()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;

                static void Take(out int x) { x = 1; }

                static void M()
                {
                    Take(out f);
                }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.InfeasibleSite);
    }

    [Fact]
    public void AddressOf_1202_AtomicNoSiteTouched()
    {
        var source = """
            using System;

            unsafe class C
            {
                [ThreadStatic] static int f;

                static void M()
                {
                    int* p = &f;
                    *p = 2;
                }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.InfeasibleSite);
    }

    // ---- TS-8: expression trees ----

    [Fact]
    public void ExpressionTreeSite_SkippedWith1205()
    {
        var source = """
            using System;
            using System.Linq.Expressions;

            class C
            {
                [ThreadStatic] static int f;

                static Expression<Func<int>> E() => () => f + 1;
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.ExpressionTreeSite && d.Severity == ThreadStaticSeverity.Warning);
    }

    [Fact]
    public void ExpressionTreePolicy_Error_Reports1205AsError()
    {
        var source = """
            using System;
            using System.Linq.Expressions;

            class C
            {
                [ThreadStatic] static int f;

                static Expression<Func<int>> E() => () => f + 1;
            }
            """;
        var result = RunOne(source, new ThreadStaticOptions
        {
            ExpressionTreePolicy = ThreadStaticExpressionTreePolicy.Error,
        });
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.ExpressionTreeSite && d.Severity == ThreadStaticSeverity.Error);
    }

    // ---- TS-7: dynamic ----

    [Fact]
    public void DynamicField_1202_PlanSkipped()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static dynamic d;

                static void M()
                {
                    d = 1;
                    int x = d;
                }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.InfeasibleSite && d.Rule == ThreadStaticRule.Dynamic);
    }

    // ---- TS-2: nameof ----

    [Fact]
    public void NameOf_IsUntouched_ReadsStillRewritten()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;

                static string Name() => nameof(f) + nameof(C.f);

                static int Get() => f;
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("nameof(f)", result.OutputCode);
        Assert.Contains("nameof(C.f)", result.OutputCode);
        Assert.Contains("=> f.Value;", result.OutputCode);
        Assert.Equal(1, result.Statistics.ReadsRewritten);
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-13: lambda / local function / async ----

    [Fact]
    public void Lambda_LocalFunction_Async_SitesAreRewritten()
    {
        var source = """
            using System;
            using System.Threading.Tasks;

            class C
            {
                [ThreadStatic] static int f;

                static void M()
                {
                    Action a = () => f++;
                    void Local() { f = 2; }
                    Local();
                    a();
                }

                static async Task<int> A()
                {
                    f += 1;
                    await Task.Yield();
                    return f;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("() => f.Value++", result.OutputCode);
        Assert.Contains("f.Value = 2;", result.OutputCode);
        Assert.Contains("f.Value += 1;", result.OutputCode);
        Assert.Contains("return f.Value;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // ---- cross-file ----

    [Fact]
    public void CrossFile_DeclarationAndReferences_BothRewrittenAndCompileTogether()
    {
        var sourceA = """
            using System;

            internal class A
            {
                [ThreadStatic] internal static int shared;
            }
            """;
        var sourceB = """
            class B
            {
                static int Get() => A.shared;

                static void Set(int v) => A.shared = v;
            }
            """;
        var (results, _) = RunMany(("A.cs", sourceA), ("B.cs", sourceB));
        Assert.Equal(2, results.Length);
        var resultA = results.Single(r => r.OutputCode.Contains("class A"));
        var resultB = results.Single(r => r.OutputCode.Contains("class B"));
        Assert.True(resultA.Changed);
        Assert.True(resultB.Changed);
        Assert.Contains("global::System.Threading.ThreadLocal<int> shared", resultA.OutputCode);
        Assert.Contains("A.shared.Value", resultB.OutputCode);
        AssertCompiles(resultA.OutputCode, resultB.OutputCode);
    }

    // ---- clean file ----

    [Fact]
    public void CleanFile_OutputIsIdenticalToInput()
    {
        var source = """
            class C
            {
                void M()
                {
                    int a = 1;
                    System.Console.WriteLine(a);
                }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    // ---- TS-5: struct value types ----

    [Fact]
    public void MutableStruct_MemberMutation_SkippedWith1203()
    {
        var source = """
            using System;

            struct S
            {
                public int X;

                public void Mut() { }
            }

            class C
            {
                [ThreadStatic] static S s;

                static void M()
                {
                    s.X = 1;
                    s.Mut();
                    var v = s;
                }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.StructMutationNotLiftable);
    }

    [Fact]
    public void ReadOnlyStruct_ReadsAndWrites_NormalRewrite()
    {
        var source = """
            using System;

            readonly struct R
            {
                public readonly int X;

                public R(int x) { X = x; }

                public int Get() => X;
            }

            class C
            {
                [ThreadStatic] static R r;

                static void M()
                {
                    var x = r.X;
                    var y = r.Get();
                    r = new R(3);
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("var x = r.Value.X;", result.OutputCode);
        Assert.Contains("var y = r.Value.Get();", result.OutputCode);
        Assert.Contains("r.Value = new R(3);", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // ---- residual gate (M1 minimal) ----

    [Fact]
    public void ResidualScan_CountsRemainingAttributes()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;
            }
            """;
        var compilation = Compile(("Test.cs", source));
        var preprocessor = new ThreadStaticPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ThreadStaticOptions());

        var before = preprocessor.ResidualScan(compilation);
        Assert.Equal(1, before.Count);

        var result = preprocessor.RewriteFile(compilation.GetSemanticModel(compilation.SyntaxTrees[0]));
        var rewrittenTree = CSharpSyntaxTree.ParseText(
            result.OutputCode,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: "Test.cs");
        var rewrittenCompilation = CSharpCompilation.Create(
            "ts-residual",
            new[] { rewrittenTree },
            References,
            CompilationOptions);
        var after = preprocessor.ResidualScan(rewrittenCompilation);
        Assert.Equal(0, after.Count);
    }
}
