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
        var result = RunOne(source, new ThreadStaticOptions
        {
            InitializerPolicy = ThreadStaticInitializerPolicy.EveryThread,
        });
        Assert.True(result.Changed);
        Assert.Contains(
            "= new global::System.Threading.ThreadLocal<int>(() => Compute() + 1);",
            result.OutputCode);
        Assert.Contains("static int Get() => f.Value;", result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.InitializerNormalized);
        Assert.Equal(1, result.Statistics.InitializersNormalized);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void NoInitializer_EveryThread_StillUsesParameterlessHolder_NoEmptyLambda()
    {
        // Design §2 decision table: with NO inline initializer BOTH policies must
        // emit the parameterless ThreadLocal holder — the EveryThread policy must
        // never synthesize a parameterized holder carrying an empty lambda.
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;

                static int Get() => f;
            }
            """;
        var result = RunOne(source, new ThreadStaticOptions
        {
            InitializerPolicy = ThreadStaticInitializerPolicy.EveryThread,
        });
        Assert.True(result.Changed);
        Assert.Contains(
            "static readonly global::System.Threading.ThreadLocal<int> f = new global::System.Threading.ThreadLocal<int>();",
            result.OutputCode);
        Assert.DoesNotContain("ThreadLocal<int>(()", result.OutputCode);
        Assert.DoesNotContain("static C()", result.OutputCode);
        Assert.Equal(0, result.Statistics.InitializersNormalized);
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

    // ---- TS-5: mutable struct statement-level temp lifting ----

    private const string MutableStructSource = """
        using System;

        struct S
        {
            public int X;
            public int Y;

            public void Mut() { }

            public int Inc() => 1;
        }
        """;

    [Fact]
    public void MutableStruct_AllMutationShapes_AreTempLiftedWithWriteback()
    {
        var source = MutableStructSource + """

            class C
            {
                [ThreadStatic] static S s;

                static void M()
                {
                    s.X = 1;
                    s.Mut();
                    s.X += 1;
                    s.X++;
                    ++s.X;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("var __cs2j_ts_s = s.Value;", result.OutputCode);
        Assert.Contains("__cs2j_ts_s.X = 1;", result.OutputCode);
        Assert.Contains("__cs2j_ts_s.Mut();", result.OutputCode);
        Assert.Contains("__cs2j_ts_s.X += 1;", result.OutputCode);
        Assert.Contains("__cs2j_ts_s.X++;", result.OutputCode);
        Assert.Contains("++__cs2j_ts_s.X;", result.OutputCode);
        Assert.Equal(5, result.OutputCode.Split("s.Value = __cs2j_ts_s;").Length - 1);
        Assert.Equal(5, result.Statistics.StructTempLifts);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void MutableStruct_AllSitesInAnchor_ExecuteOnTheTemp()
    {
        var source = MutableStructSource + """

            class C
            {
                [ThreadStatic] static S s;

                static void M(int z)
                {
                    s.X = z + s.Y;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("__cs2j_ts_s.X = z + __cs2j_ts_s.Y;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void MutableStruct_EmbeddedStatementAnchor_IsWrappedInBlock()
    {
        var source = MutableStructSource + """

            class C
            {
                [ThreadStatic] static S s;

                static void M(bool b)
                {
                    if (b)
                        s.Mut();
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("if (b)", result.OutputCode);
        Assert.Contains("__cs2j_ts_s.Mut();", result.OutputCode);
        Assert.Equal(1, result.Statistics.StructTempLifts);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void MutableStruct_TempNameCollision_GetsNumericSuffix()
    {
        var source = MutableStructSource + """

            class C
            {
                [ThreadStatic] static S s;

                static int M()
                {
                    var __cs2j_ts_s = 0;
                    s.X = 1;
                    return __cs2j_ts_s;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("var __cs2j_ts_s_2 = s.Value;", result.OutputCode);
        Assert.Contains("__cs2j_ts_s_2.X = 1;", result.OutputCode);
        Assert.Contains("s.Value = __cs2j_ts_s_2;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Theory]
    [InlineData("                var x = s.Inc();\r\n")]
    [InlineData("                Take(s.Inc());\r\n")]
    [InlineData("                var q = new[] { 1 }.Where(n => s.Inc() == n).ToList();\r\n")]
    public void MutableStruct_NonLiftableContext_SkippedWith1203_BytesUntouched(string statement)
    {
        var source = MutableStructSource + """

            using System.Collections.Generic;
            using System.Linq;

            class C
            {
                [ThreadStatic] static S s;

                static void Take(int x) { }

                static void M()
                {
            """ + "\r\n" + statement + """
                }
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.StructMutationNotLiftable);
    }

    [Fact]
    public void MutableStruct_FieldInitializerContext_SkippedWith1203_BytesUntouched()
    {
        var source = MutableStructSource + """

            class C
            {
                [ThreadStatic] static S s;

                static int z = s.Inc();
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.StructMutationNotLiftable);
    }

    [Fact]
    public void MutableStruct_AttributeArgumentContext_SkippedWith1203_BytesUntouched()
    {
        var source = MutableStructSource + """

            class C
            {
                [ThreadStatic] static S s;

                [Obsolete(s.Inc().ToString())]
                static void M()
                {
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

    // ---- TS-1 / TS-10: FirstThreadFaithful (default): cctor injection ----

    [Fact]
    public void FirstThreadFaithful_NoCctor_SynthesizesCctorWithAssignmentAndParameterlessHolder()
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
            "static readonly global::System.Threading.ThreadLocal<int> f = new global::System.Threading.ThreadLocal<int>();",
            result.OutputCode);
        Assert.DoesNotContain("ThreadLocal<int>(() =>", result.OutputCode);
        Assert.Contains("static C()", result.OutputCode);
        Assert.Contains("f.Value = Compute() + 1;", result.OutputCode);
        Assert.Contains("static int Get() => f.Value;", result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.InitializerNormalized
                && d.Message.Contains("FirstThreadFaithful"));
        Assert.Equal(1, result.Statistics.InitializersNormalized);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void FirstThreadFaithful_ExistingCctor_AssignmentPrependedToBody()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f = Compute() + 1;

                static int Compute() => 41;

                static C()
                {
                    Other();
                }

                static void Other() { }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        var injectedIndex = result.OutputCode.IndexOf("f.Value = Compute() + 1;", StringComparison.Ordinal);
        var otherIndex = result.OutputCode.IndexOf("Other();", StringComparison.Ordinal);
        Assert.True(injectedIndex >= 0);
        Assert.True(otherIndex >= 0);
        Assert.True(injectedIndex < otherIndex);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void FirstThreadFaithful_MultipleDeclarations_AssignmentsFollowGlobalFieldOrder()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int first = 1;
                [ThreadStatic] static int second = 2;

                static int Get() => first + second;
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        var firstIndex = result.OutputCode.IndexOf("first.Value = 1;", StringComparison.Ordinal);
        var secondIndex = result.OutputCode.IndexOf("second.Value = 2;", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex >= 0 && firstIndex < secondIndex);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void FirstThreadFaithful_MultiVariableSplit_EachFieldGetsCctorAssignment()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int a = 1, b = 2;
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains(
            "ThreadLocal<int> a = new global::System.Threading.ThreadLocal<int>();",
            result.OutputCode);
        Assert.Contains(
            "ThreadLocal<int> b = new global::System.Threading.ThreadLocal<int>();",
            result.OutputCode);
        var aIndex = result.OutputCode.IndexOf("a.Value = 1;", StringComparison.Ordinal);
        var bIndex = result.OutputCode.IndexOf("b.Value = 2;", StringComparison.Ordinal);
        Assert.True(aIndex >= 0 && bIndex >= 0 && aIndex < bIndex);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void FirstThreadFaithful_SiteInsideInitializer_IsRewrittenInCctor()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int seed = 40;
                [ThreadStatic] static int derived = seed + 2;
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("derived.Value = seed.Value + 2;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-13: iterator ----

    [Fact]
    public void IteratorBody_SiteIsRewritten()
    {
        var source = """
            using System;
            using System.Collections.Generic;

            class C
            {
                [ThreadStatic] static int f;

                static IEnumerable<int> It()
                {
                    yield return f;
                    f++;
                    yield break;
                }
            }
            """;
        var result = RunOne(source);
        Assert.True(result.Changed);
        Assert.Contains("yield return f.Value;", result.OutputCode);
        Assert.Contains("f.Value++;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // ---- TS-14: user-owned ThreadLocal<T> is invisible to the pass ----

    [Fact]
    public void UserOwnedThreadLocal_ZeroChangesZeroDiagnostics()
    {
        var source = """
            using System;
            using System.Threading;

            class C
            {
                static readonly ThreadLocal<int> t = new(() => 5);

                static int Get() => t.Value;

                static void Set(int v) => t.Value = v;
            }
            """;
        var result = RunOne(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.FieldsDetected);
    }

    // ---- residual gate (reconciliation) ----

    [Fact]
    public void ResidualScan_ActivePlanAttributeIsUnowned_ThenZeroAfterRewrite()
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
        Assert.Equal(1, before.Total);
        Assert.Equal(0, before.Owned.Count);
        Assert.Equal(1, before.Unowned.Count);

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
        Assert.Equal(0, after.Total);
        Assert.False(after.HasUnowned);
    }

    [Fact]
    public void ResidualScan_SkippedPlanAttribute_IsOwnedAndDoesNotRaise1220()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] int instanceField;
            }
            """;
        var compilation = Compile(("Test.cs", source));
        var preprocessor = new ThreadStaticPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ThreadStaticOptions());

        var report = preprocessor.ResidualScan(compilation);
        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Owned.Count);
        Assert.Equal(0, report.Unowned.Count);
        Assert.Equal(ThreadStaticCodes.UnsupportedAttributePosition, report.Owned[0].OwnerCode);
    }

    [Fact]
    public void ResidualScan_AttributeWithoutAnyPlan_IsUnowned()
    {
        var source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;
            }
            """;
        var compilation = Compile(("Test.cs", source));
        var report = ThreadStaticResidualScanner.Scan(compilation, Array.Empty<ThreadStaticFieldPlan>());
        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Unowned.Count);
        Assert.Null(report.Unowned[0].OwnerPlanId);
    }
}
