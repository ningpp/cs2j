using System.IO;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Finalizers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C39 终结器消除 M1 Core 单测矩阵（设计 §7.1）：内联源码 → CSharpCompilation →
/// MemberDesugarPreprocessor（Finalizer 规则经程序集扫描自动生效）→ RewriteFile。
/// M1 A 档：有诊断、零树改动、输出可编译、幂等。单文件用例自带 RewriteFn（多返回
/// Changed/Plans）；编译断言复用 MemberDesugarPropertyUnitTests.AssertOutputCompiles；
/// 多文件用例 RewriteAll（TPA 引用、allowUnsafe、LanguageVersion.Preview）。
/// </summary>
public class MemberDesugarFinalizerUnitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static (string Output, bool Changed, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags, IReadOnlyList<MemberDesugarPlan> Plans)
        RewriteFn(string src, MemberDesugarOptions? options = null, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "md-fn-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var pp = new MemberDesugarPreprocessor(options ?? new MemberDesugarOptions());
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, result.Changed, stats, diags, pp.Plans);
    }

    /// <summary>多文件重写：每树一个路径，同一编译、同一 preprocessor；返回每个文件的输出与 Changed。</summary>
    private static (string[] Outputs, bool[] Changed, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags, IReadOnlyList<MemberDesugarPlan> Plans)
        RewriteAll(MemberDesugarOptions? options, params (string Path, string Src)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Src, ParseOptions, path: f.Path)).ToArray();
        var compilation = CSharpCompilation.Create("md-fn-multi",
            trees, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new MemberDesugarPreprocessor(options ?? new MemberDesugarOptions());
        preprocessor.BindCompilation(compilation);
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var outputs = new List<string>();
        var changed = new List<bool>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var src = tree.GetCompilationUnitRoot().ToFullString();
            var result = preprocessor.RewriteFile(
                src, compilation.GetSemanticModel(tree), stats, diags);
            outputs.Add(result.OutputCode);
            changed.Add(result.Changed);
        }
        return (outputs.ToArray(), changed.ToArray(), stats, diags, preprocessor.Plans);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static string Normalize(string code)
        => new(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static bool HasCode(
        List<MemberDesugarDiagnostic> diags, MemberDesugarDiagnosticCode code,
        MemberDesugarSeverity? severity = null)
        => diags.Any(d => d.Code == code && (severity == null || d.Severity == severity));

    private static FinalizerPlan? Plan(IReadOnlyList<MemberDesugarPlan> plans, string typeFqn)
        => plans.OfType<FinalizerPlan>()
            .FirstOrDefault(p => string.Equals(p.TypeFqn, typeFqn, StringComparison.Ordinal));

    private const string ExtraFile = "class Extra { }";

    // ---- 1) 基本形态：A 档 5200 + 零改动 + 可编译；strict 升 Error ----

    [Fact]
    public void BasicFinalizer_RejectedByDefault_Warning_ZeroChange_Compiles()
    {
        var src = "class C { ~C() { } }";
        var (output, changed, stats, diags, plans) = RewriteFn(src);

        Assert.False(changed, "M1 A 档不得改动树");
        Assert.Equal(Normalize(src), Normalize(output));
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected,
            MemberDesugarSeverity.Warning));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected,
            MemberDesugarSeverity.Error));
        Assert.Equal(1, stats.FinalizersDetected);
        Assert.Equal(1, stats.FinalizersReported);
        Assert.Single(plans.OfType<FinalizerPlan>());
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);
    }

    [Fact]
    public void BasicFinalizer_Strict_Promotes5200ToError()
    {
        var (_, _, _, diags, _) = RewriteFn(
            "class C { ~C() { } }", new MemberDesugarOptions { Strict = true });
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected,
            MemberDesugarSeverity.Error));
    }

    // ---- 2) 基类/派生各带终结器：两个 plan、5207、两文件零改动 ----

    [Fact]
    public void BaseAndDerivedFinalizers_TwoPlans_ChainInfo_BothFilesUnchanged()
    {
        var (outputs, changed, stats, diags, plans) = RewriteAll(null,
            ("Base.cs", "class Base { ~Base() { } }"),
            ("Derived.cs", "class Derived : Base { ~Derived() { } }"));

        Assert.Equal(2, plans.OfType<FinalizerPlan>().Count());
        var derived = Plan(plans, "Derived");
        Assert.NotNull(derived);
        Assert.True(derived!.HasBaseFinalizer);
        Assert.Equal("Base", derived.BaseFinalizerFqn);
        Assert.Equal(1, stats.FinalizerChainsWithBaseFinalizer);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerBaseChain));
        Assert.All(changed, c => Assert.False(c));
        Assert.Contains(Normalize("~Base()"), Normalize(outputs[0]));
        Assert.Contains(Normalize("~Derived()"), Normalize(outputs[1]));
    }

    // ---- 3) Dispose 模式配对 ----

    [Fact]
    public void DisposePattern_Paired_5202Info()
    {
        var src = """
            using System;

            class T : IDisposable
            {
                ~T() { Dispose(false); }
                public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
                protected virtual void Dispose(bool disposing) { }
            }
            """;
        // 第二棵树避免单文件降级（分类本身不受影响，保持与其余多树用例一致）。
        var (output, changed, stats, diags, plans) = RewriteFn(src, null, ExtraFile);

        var plan = Plan(plans, "T");
        Assert.NotNull(plan);
        Assert.Equal(FinalizerBodyKind.DisposePattern, plan!.BodyKind);
        Assert.Equal(SuppressFinalizePairing.Paired, plan.Pairing);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerDisposePattern,
            MemberDesugarSeverity.Info));
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected));
        Assert.False(changed);
        Assert.Equal(1, stats.FinalizerDisposePattern);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void FinalizerWithoutDispose_Unpaired_MessageSaysUnpaired()
    {
        var (_, _, _, diags, plans) = RewriteFn("class T { ~T() { } }", null, ExtraFile);
        var plan = Plan(plans, "T");
        Assert.NotNull(plan);
        Assert.Equal(SuppressFinalizePairing.Unpaired, plan!.Pairing);
        var rejected = diags.First(d => d.Code == MemberDesugarDiagnosticCode.FinalizerRejected);
        Assert.Contains("unpaired", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 4) SafeHandleOnly 与混合普通字段 ----

    private const string SafeHandleSibling = """
        using System;
        using System.Runtime.InteropServices;

        sealed class MyHandle : SafeHandle
        {
            public MyHandle() : base(IntPtr.Zero, true) { }
            public override bool IsInvalid => handle == IntPtr.Zero;
            protected override bool ReleaseHandle() => true;
        }
        """;

    [Fact]
    public void SafeHandleFieldOnly_SafeHandleOnlyKind_5203Info()
    {
        var src = """
            class C
            {
                private MyHandle _handle = new MyHandle();
                ~C() { _handle.Dispose(); }
            }
            """;
        var (output, changed, stats, diags, plans) = RewriteFn(src, null, SafeHandleSibling);

        var plan = Plan(plans, "C");
        Assert.NotNull(plan);
        Assert.Equal(FinalizerBodyKind.SafeHandleOnly, plan!.BodyKind);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRedundant,
            MemberDesugarSeverity.Info));
        Assert.Equal(1, stats.FinalizerSafeHandleOnly);
        Assert.False(changed);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, SafeHandleSibling);
    }

    [Fact]
    public void MixedPlainFieldAccess_ClassifiedOther()
    {
        var src = """
            using System;

            class C
            {
                private MyHandle _handle = new MyHandle();
                private object _other = new object();
                ~C() { _handle.Dispose(); _other.ToString(); }
            }
            """;
        var (_, _, stats, _, plans) = RewriteFn(src, null, SafeHandleSibling);
        Assert.Equal(FinalizerBodyKind.Other, Plan(plans, "C")!.BodyKind);
        Assert.Equal(1, stats.FinalizerOther);
    }

    // ---- 5) NativeRelease（M1 仅分类断言）----

    [Fact]
    public void MarshalFreeHGlobal_NativeReleaseKind()
    {
        var src = """
            using System;
            using System.Runtime.InteropServices;

            class C
            {
                private IntPtr _p;
                ~C() { Marshal.FreeHGlobal(_p); }
            }
            """;
        var (_, _, stats, _, plans) = RewriteFn(src, null, ExtraFile);
        Assert.Equal(FinalizerBodyKind.NativeRelease, Plan(plans, "C")!.BodyKind);
        Assert.Equal(1, stats.FinalizerNativeRelease);
    }

    // ---- 6) CriticalFinalizerObject → 5205 Error 且无 5200、零改动 ----

    [Fact]
    public void CriticalFinalizer_5205Error_No5200_ZeroChange()
    {
        var src = """
            using System.Runtime.ConstrainedExecution;

            class C : CriticalFinalizerObject
            {
                ~C() { }
            }
            """;
        var (output, changed, stats, diags, _) = RewriteFn(src, null, ExtraFile);

        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerCriticalBlocked,
            MemberDesugarSeverity.Error));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected));
        Assert.Equal(1, stats.FinalizerCriticalBlocked);
        Assert.False(changed);
        Assert.Equal(Normalize(src), Normalize(output));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    // ---- 7) 空体 / 仅 Debug.Assert / extern ----

    [Fact]
    public void EmptyBody_ClassifiedEmpty()
    {
        var (_, _, stats, _, plans) = RewriteFn("class C { ~C() { } }", null, ExtraFile);
        Assert.Equal(FinalizerBodyKind.Empty, Plan(plans, "C")!.BodyKind);
        Assert.Equal(1, stats.FinalizerEmpty);
    }

    [Fact]
    public void DebugAssertOnly_IsNoise_ClassifiedEmpty()
    {
        var src = """
            using System.Diagnostics;

            class C
            {
                ~C()
                {
                    // 仅注释 + 白名单噪声调用
                    Debug.Assert(true);
                }
            }
            """;
        var (output, _, _, _, plans) = RewriteFn(src, null, ExtraFile);
        Assert.Equal(FinalizerBodyKind.Empty, Plan(plans, "C")!.BodyKind);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void ExternFinalizer_ClassifiedExtern()
    {
        var src = "class C { extern ~C(); }";
        var (output, _, stats, diags, plans) = RewriteFn(src, null, ExtraFile);
        Assert.Equal(FinalizerBodyKind.Extern, Plan(plans, "C")!.BodyKind);
        Assert.Equal(1, stats.FinalizerExtern);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    // ---- 8) partial 第二部件 / 嵌套类：按符号一个 plan ----

    [Fact]
    public void PartialType_SecondPartDestructor_SinglePlan()
    {
        var (_, _, _, _, plans) = RewriteAll(null,
            ("C1.cs", "partial class C { }"),
            ("C2.cs", "partial class C { ~C() { } }"));
        Assert.Single(plans.OfType<FinalizerPlan>());
    }

    [Fact]
    public void NestedClassDestructor_SinglePlanWithFqn()
    {
        var src = """
            class Outer
            {
                class Inner
                {
                    ~Inner() { }
                }
            }
            """;
        var (_, _, _, diags, plans) = RewriteFn(src, null, ExtraFile);
        var plan = Plan(plans, "Outer.Inner");
        Assert.NotNull(plan);
        Assert.Single(plans.OfType<FinalizerPlan>());
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected));
    }

    // ---- 9) 零误伤：名为 Finalize/Dispose 的普通成员不被识别 ----

    [Fact]
    public void OrdinaryMethodsNamedFinalizeOrDispose_No52xx_CleanFile()
    {
        // 仅普通方法（无属性，避免 C11 属性规则自身的表达式体边界介入）：文件须 clean。
        var src = """
            class C
            {
                public void Dispose() { }
                public void Finalize(string reason) { }
            }
            """;
        var (_, changed, stats, diags, plans) = RewriteFn(src);
        Assert.False(changed);
        Assert.Empty(plans.OfType<FinalizerPlan>());
        Assert.DoesNotContain(diags, d => (int)d.Code is >= 5200 and < 5220);
        Assert.Equal(0, stats.FinalizersDetected);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(src);
    }

    [Fact]
    public void PropertiesNamedFinalizeOrDispose_NotRecognizedAsFinalizer()
    {
        // 同名属性归 C11 处理；C39 不得产出任何终结器 plan / 52xx 诊断。
        var src = """
            class C
            {
                public int Finalize { get; set; }
                public int Dispose { get; set; }
            }
            """;
        var (_, _, _, diags, plans) = RewriteFn(src);
        Assert.Empty(plans.OfType<FinalizerPlan>());
        Assert.DoesNotContain(diags, d => (int)d.Code is >= 5200 and < 5220);
    }

    // ---- 10) 幂等 + 干净文件零诊断 ----

    [Fact]
    public void Idempotent_TwoRunsSameOutput()
    {
        var src = "class C { ~C() { } }";
        var (first, _, _, _, _) = RewriteFn(src);
        var (second, _, _, diags, _) = RewriteFn(first);
        Assert.Equal(Normalize(src), Normalize(second));
        Assert.Equal(Normalize(first), Normalize(second));
        // 第二次仍稳定报同一组主码（不翻倍、不消失）。
        Assert.Single(diags, d => d.Code == MemberDesugarDiagnosticCode.FinalizerRejected);
    }

    [Fact]
    public void CleanFileWithoutFinalizer_ZeroDiagnostics_NotChanged()
    {
        var (output, changed, stats, diags, plans) =
            RewriteFn("class C { public void M() { } }");
        Assert.False(changed);
        Assert.Empty(diags);
        Assert.Empty(plans.OfType<FinalizerPlan>());
        Assert.Equal(0, stats.FinalizersDetected);
        Assert.Equal(1, stats.FilesSkippedClean);
        Assert.Equal("class C { public void M() { } }", output);
    }

    // ---- 11) Policy=CleanerGuide → 5208 ----

    [Fact]
    public void CleanerGuidePolicy_5208Error()
    {
        var options = new MemberDesugarOptions
        {
            Finalizers = new FinalizerOptions { Policy = FinalizerPolicy.CleanerGuide },
        };
        var (_, _, _, diags, _) = RewriteFn("class C { ~C() { } }", options, ExtraFile);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerCleanerGuideUnsupported,
            MemberDesugarSeverity.Error));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected));
    }

    // ---- 12) ReRegisterForFinalize 站点：高风险 + 5206 ----

    [Fact]
    public void ReRegisterForFinalizeSite_HighRisk_5206Info()
    {
        var src = """
            using System;

            class C
            {
                ~C() { }
                public void Revive() { GC.ReRegisterForFinalize(this); }
            }
            """;
        var (_, _, stats, diags, plans) = RewriteFn(src, null, ExtraFile);
        var plan = Plan(plans, "C");
        Assert.NotNull(plan);
        Assert.True(plan!.HasResurrectionSite);
        Assert.Equal(1, plan.ReRegisterSiteCount);
        Assert.Equal(1, stats.FinalizerResurrectionSites);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerGcLifecycleSite,
            MemberDesugarSeverity.Info));
        var gc = diags.First(d => d.Code == MemberDesugarDiagnosticCode.FinalizerGcLifecycleSite);
        Assert.Contains("ReRegisterForFinalize x1", gc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressFinalizeSite_CountedInInventory()
    {
        var src = """
            using System;

            class C
            {
                ~C() { }
                public void Done() { GC.SuppressFinalize(this); }
            }
            """;
        var (_, _, stats, _, plans) = RewriteFn(src, null, ExtraFile);
        Assert.Equal(1, Plan(plans, "C")!.SuppressFinalizeSiteCount);
        Assert.Equal(1, stats.FinalizerSuppressFinalizeSites);
    }

    // ---- 13) 单文件模式降级：SafeHandle 场景退化 Other + 5211 ----

    [Fact]
    public void SingleFileMode_SafeHandleBody_DegradesToOther_5211()
    {
        var src = """
            using System.Runtime.InteropServices;

            sealed class MyHandle : SafeHandle
            {
                public MyHandle() : base(System.IntPtr.Zero, true) { }
                public override bool IsInvalid { get { return false; } }
                protected override bool ReleaseHandle() => true;
            }

            class C
            {
                private MyHandle _handle = new MyHandle();
                ~C() { _handle.Dispose(); }
            }
            """;
        // 单树编译（无 extra）：触发单文件降级。
        var (_, _, _, diags, plans) = RewriteFn(src);
        var plan = Plan(plans, "C");
        Assert.NotNull(plan);
        Assert.True(plan!.BaseInfoDegraded);
        Assert.Equal(FinalizerBodyKind.Other, plan.BodyKind);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerSingleFileDegraded,
            MemberDesugarSeverity.Warning));
    }
}
