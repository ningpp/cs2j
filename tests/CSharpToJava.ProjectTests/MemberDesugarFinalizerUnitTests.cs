using System.IO;
using System.Text.Json;
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
        var (output, changed, stats, diags, plans, _) =
            RewriteFnTracked(src, options, extra);
        return (output, changed, stats, diags, plans);
    }

    /// <summary>单文件重写并暴露 preprocessor（M2：断言 sidecar 内存条目/路径）。</summary>
    private static (string Output, bool Changed, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags, IReadOnlyList<MemberDesugarPlan> Plans,
        MemberDesugarPreprocessor Preprocessor)
        RewriteFnTracked(string src, MemberDesugarOptions? options = null, params string[] extra)
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
        return (result.OutputCode, result.Changed, stats, diags, pp.Plans, pp);
    }

    /// <summary>多文件重写：每树一个路径，同一编译、同一 preprocessor；返回每个文件的输出与 Changed。</summary>
    private static (string[] Outputs, bool[] Changed, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags, IReadOnlyList<MemberDesugarPlan> Plans)
        RewriteAll(MemberDesugarOptions? options, params (string Path, string Src)[] files)
    {
        var (outputs, changed, stats, diags, plans, _) =
            RewriteAllTracked(options, files);
        return (outputs, changed, stats, diags, plans);
    }

    /// <summary>多文件重写并暴露 preprocessor（M2：sidecar 落盘/重置断言）。</summary>
    private static (string[] Outputs, bool[] Changed, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags, IReadOnlyList<MemberDesugarPlan> Plans,
        MemberDesugarPreprocessor Preprocessor)
        RewriteAllTracked(MemberDesugarOptions? options, params (string Path, string Src)[] files)
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
        return (outputs.ToArray(), changed.ToArray(), stats, diags,
            preprocessor.Plans, preprocessor);
    }

    /// <summary>B 档 Remove options 工厂。</summary>
    private static MemberDesugarOptions RemoveOptions(
        string[]? forceTypes = null, bool marker = false, bool extract = false, bool strict = false)
        => new()
        {
            Strict = strict,
            Finalizers = new FinalizerOptions
            {
                Policy = FinalizerPolicy.Remove,
                ForceRemoveTypes = forceTypes != null
                    ? new HashSet<string>(forceTypes, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal),
                EmitRemovalMarkerComment = marker,
                ExtractRemovedBody = extract,
            },
        };

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

    // ======================================================================
    // M2（B 档删除模式，设计 §7.1 FN-6/FN-8/FN-10）
    // ======================================================================

    private const string NativeReleaseSrc = """
        using System;
        using System.Runtime.InteropServices;

        class C
        {
            private IntPtr _p;
            ~C() { Marshal.FreeHGlobal(_p); }
        }
        """;

    // ---- B1) 基本删除：Empty / SafeHandleOnly / DisposePattern+Paired ----

    [Fact]
    public void RemovePolicy_Empty_Deleted_5201_SidecarEntry_Compiles()
    {
        var src = "class C { ~C() { } }";
        var (output, changed, stats, diags, plans, pp) =
            RewriteFnTracked(src, RemoveOptions(), ExtraFile);

        Assert.True(changed, "B 档 Empty 必须改树");
        Assert.DoesNotContain("~C(", output);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved,
            MemberDesugarSeverity.Warning));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected));
        Assert.Equal(1, stats.FinalizersRemoved);
        Assert.Equal(0, stats.FinalizersForceRemoved);
        Assert.Equal(1, stats.FinalizerSidecarEntries);
        Assert.Single(pp.FinalizerSidecarEntries);
        Assert.False(pp.FinalizerSidecarEntries[0].Forced);
        Assert.Equal(FinalizerAction.Removed, Plan(plans, "C")!.Action);
        Assert.Null(Plan(plans, "C")!.RefusalCode);
        Assert.Equal(0, stats.FinalizerResidual);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_Strict_5201StaysWarning()
    {
        var (_, _, _, diags, _) = RewriteFn(
            "class C { ~C() { } }", RemoveOptions(strict: true), ExtraFile);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved,
            MemberDesugarSeverity.Warning));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved,
            MemberDesugarSeverity.Error));
    }

    [Fact]
    public void RemovePolicy_SafeHandleOnly_Deleted_5201()
    {
        var src = """
            class C
            {
                private MyHandle _handle = new MyHandle();
                ~C() { _handle.Dispose(); }
            }
            """;
        var (output, changed, stats, _, plans) =
            RewriteFn(src, RemoveOptions(), SafeHandleSibling);

        Assert.True(changed);
        Assert.DoesNotContain("~C(", output);
        Assert.Equal(FinalizerAction.Removed, Plan(plans, "C")!.Action);
        Assert.Equal(1, stats.FinalizersRemoved);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, SafeHandleSibling);
    }

    [Fact]
    public void RemovePolicy_DisposePattern_Paired_Deleted_5201()
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
        var (output, changed, _, diags, plans) =
            RewriteFn(src, RemoveOptions(), ExtraFile);

        Assert.True(changed);
        Assert.DoesNotContain("~T(", output);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.Equal(FinalizerAction.Removed, Plan(plans, "T")!.Action);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_DisposePattern_Unpaired_Refused5204_Retained()
    {
        var src = """
            class T
            {
                ~T() { Dispose(false); }
                public void Dispose() { }
                protected virtual void Dispose(bool disposing) { }
            }
            """;
        var (output, changed, stats, diags, plans) =
            RewriteFn(src, RemoveOptions(), ExtraFile);

        Assert.False(changed, "保守拒绝时成员保留、树不变");
        Assert.Contains("~T(", output);
        var plan = Plan(plans, "T")!;
        Assert.Equal(FinalizerAction.Refused, plan.Action);
        Assert.Equal(MemberDesugarDiagnosticCode.FinalizerRemoveRefused, plan.RefusalCode);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused,
            MemberDesugarSeverity.Warning));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRejected));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.Equal(1, stats.FinalizersRefused);
        Assert.Equal(1, stats.SkippedByReason[MemberDesugarDiagnosticCode.FinalizerRemoveRefused]);
        Assert.Equal(0, stats.FinalizersRemoved);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_Strict_5204PromotedToError()
    {
        var src = """
            class T
            {
                ~T() { Dispose(false); }
                public void Dispose() { }
                protected virtual void Dispose(bool disposing) { }
            }
            """;
        var (_, _, _, diags, _) = RewriteFn(src, RemoveOptions(strict: true), ExtraFile);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused,
            MemberDesugarSeverity.Error));
    }

    // ---- B2) 拒删矩阵：NativeRelease / Other / Extern / resurrection / critical ----

    [Fact]
    public void RemovePolicy_NativeRelease_Refused5204_ThenForce_Deleted_WithSidecarFlag()
    {
        // 无 force：5204 保留。
        var (kept, keptChanged, keptStats, keptDiags, keptPlans) =
            RewriteFn(NativeReleaseSrc, RemoveOptions(), ExtraFile);
        Assert.False(keptChanged);
        Assert.Contains("~C(", kept);
        Assert.True(HasCode(keptDiags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused));
        Assert.Equal(FinalizerAction.Refused, Plan(keptPlans, "C")!.Action);
        Assert.Equal(1, keptStats.FinalizersRefused);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(kept, ExtraFile);

        // force（按类型 FQN，Ordinal）：删除，forced=true，sidecar 条目标 forced。
        var (output, changed, stats, diags, plans, pp) =
            RewriteFnTracked(NativeReleaseSrc, RemoveOptions(forceTypes: new[] { "C" }), ExtraFile);
        Assert.True(changed);
        Assert.DoesNotContain("~C(", output);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused));
        Assert.Equal(FinalizerAction.ForceRemoved, Plan(plans, "C")!.Action);
        Assert.Equal(1, stats.FinalizersRemoved);
        Assert.Equal(1, stats.FinalizersForceRemoved);
        var entry = Assert.Single(pp.FinalizerSidecarEntries);
        Assert.True(entry.Forced);
        Assert.Equal("NativeRelease", entry.BodyKind);
        Assert.Contains("Marshal.FreeHGlobal", entry.Body);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_OtherKind_FlushLikeLogic_RefusedThenForce()
    {
        var src = """
            using System.Text;

            class C
            {
                private readonly StringBuilder _log = new StringBuilder();
                ~C() { _log.Append('x'); }
            }
            """;
        var (kept, _, _, keptDiags, keptPlans) =
            RewriteFn(src, RemoveOptions(), ExtraFile);
        Assert.Equal(FinalizerBodyKind.Other, Plan(keptPlans, "C")!.BodyKind);
        Assert.True(HasCode(keptDiags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused));
        Assert.Contains("~C(", kept);
        Assert.Contains("forceRemoveTypes",
            keptDiags.First(d => d.Code == MemberDesugarDiagnosticCode.FinalizerRemoveRefused).Message,
            StringComparison.Ordinal);

        var (output, _, _, forceDiags, _, pp) =
            RewriteFnTracked(src, RemoveOptions(forceTypes: new[] { "C" }), ExtraFile);
        Assert.DoesNotContain("~C(", output);
        Assert.True(HasCode(forceDiags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.True(pp.FinalizerSidecarEntries[0].Forced);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_ExternFinalizer_RefusedThenForce()
    {
        var src = "class C { extern ~C(); }";
        var (kept, _, _, keptDiags, keptPlans) =
            RewriteFn(src, RemoveOptions(), ExtraFile);
        Assert.Equal(FinalizerBodyKind.Extern, Plan(keptPlans, "C")!.BodyKind);
        Assert.True(HasCode(keptDiags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused));
        Assert.Contains("~C(", kept);

        var (output, changed, _, forceDiags, _, pp) =
            RewriteFnTracked(src, RemoveOptions(forceTypes: new[] { "C" }), ExtraFile);
        Assert.True(changed);
        Assert.DoesNotContain("~C(", output);
        Assert.True(HasCode(forceDiags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.True(pp.FinalizerSidecarEntries[0].Forced);
        Assert.Equal(string.Empty, pp.FinalizerSidecarEntries[0].Body);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_ResurrectionSite_EmptyBody_RefusedThenForce()
    {
        var src = """
            using System;

            class C
            {
                ~C() { }
                public void Revive() { GC.ReRegisterForFinalize(this); }
            }
            """;
        var (kept, _, _, keptDiags, keptPlans) =
            RewriteFn(src, RemoveOptions(), ExtraFile);
        Assert.Equal(FinalizerBodyKind.Empty, Plan(keptPlans, "C")!.BodyKind);
        Assert.True(HasCode(keptDiags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused),
            "resurrection 站点即使空体也必须 5204 拒绝");
        Assert.Contains("~C(", kept);
        Assert.Contains("resurrection",
            keptDiags.First(d => d.Code == MemberDesugarDiagnosticCode.FinalizerRemoveRefused).Message,
            StringComparison.OrdinalIgnoreCase);

        var (output, _, _, forceDiags, _, _) =
            RewriteFnTracked(src, RemoveOptions(forceTypes: new[] { "C" }), ExtraFile);
        Assert.DoesNotContain("~C(", output);
        Assert.True(HasCode(forceDiags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_CriticalFinalizer_ForceDoesNotLift_5205_Retained()
    {
        var src = """
            using System.Runtime.ConstrainedExecution;

            class C : CriticalFinalizerObject
            {
                ~C() { }
            }
            """;
        var (output, changed, stats, diags, plans) =
            RewriteFn(src, RemoveOptions(forceTypes: new[] { "C" }), ExtraFile);

        Assert.False(changed, "5205 不可被 force 解除，成员必须保留");
        Assert.Contains("~C(", output);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerCriticalBlocked,
            MemberDesugarSeverity.Error));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoveRefused));
        var plan = Plan(plans, "C")!;
        Assert.Equal(FinalizerAction.Refused, plan.Action);
        Assert.Equal(MemberDesugarDiagnosticCode.FinalizerCriticalBlocked, plan.RefusalCode);
        Assert.Equal(1, stats.FinalizerCriticalBlocked);
        Assert.Equal(0, stats.FinalizersRemoved);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    // ---- B3) 可选项：marker 注释 / body 提取 ----

    [Fact]
    public void RemovePolicy_MarkerComment_AttachedToNextMember_Compiles()
    {
        var src = """
            class C
            {
                ~C() { }
                public int Keep;
            }
            """;
        var (output, changed, _, diags, _) =
            RewriteFn(src, RemoveOptions(marker: true), ExtraFile);

        Assert.True(changed);
        Assert.DoesNotContain("~C(", output);
        Assert.Contains("CS2J5201: removed finalizer 'C'", output, StringComparison.Ordinal);
        Assert.Contains("public int Keep", output);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_MarkerComment_LastMember_AttachedToCloseBrace_Compiles()
    {
        var src = """
            class C
            {
                public int Keep;
                ~C() { }
            }
            """;
        var (output, changed, _, _, _) =
            RewriteFn(src, RemoveOptions(marker: true), ExtraFile);

        Assert.True(changed);
        Assert.Contains("CS2J5201: removed finalizer 'C'", output, StringComparison.Ordinal);
        Assert.Contains("public int Keep", output);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_ExtractRemovedBody_GeneratesPrivateMethod_NoCallSite()
    {
        var (output2, changed, _, diags, _, pp) =
            RewriteFnTracked(NativeReleaseSrc,
                RemoveOptions(forceTypes: new[] { "C" }, extract: true), ExtraFile);

        Assert.True(changed);
        Assert.DoesNotContain("~C(", output2);
        Assert.Contains("private void Cs2jRemovedFinalizer_C", output2);
        Assert.Contains("Marshal.FreeHGlobal", output2);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        // 唯一出现是声明本身：无调用点（名称后不接实参括号调用）。
        var occurrences = System.Text.RegularExpressions.Regex
            .Matches(output2, "Cs2jRemovedFinalizer_C").Count;
        Assert.Equal(1, occurrences);
        Assert.True(pp.FinalizerSidecarEntries[0].Forced);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output2, ExtraFile);
    }

    [Fact]
    public void RemovePolicy_DefaultOptions_NoMarkerComment_NoExtractedMethod()
    {
        var (output, changed, _, diags, _) =
            RewriteFn(NativeReleaseSrc,
                RemoveOptions(forceTypes: new[] { "C" }), ExtraFile);

        Assert.True(changed);
        Assert.DoesNotContain("removed finalizer", output);
        Assert.DoesNotContain("Cs2jRemovedFinalizer", output);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }

    // ---- B4) sidecar 真实落盘 + 字段回读 + run 重置 ----

    [Fact]
    public void Sidecar_WrittenUnderIntermediateRoot_FieldsReadBack_ResetOnNewRun()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cs2j-fn-sidecar-" + Guid.NewGuid().ToString("N"),
            FinalizerSidecarReport.IntermediateSegment);
        try
        {
            string Lib(string name) => Path.Combine(root, "lib", name);

            var emptySrc = """
                class E
                {
                    ~E()
                    {
                        // fn-empty-marker
                    }
                }
                """;
            // Base 空体：Empty 可直接删；Derived 仍能记录 hasBaseFinalizer。
            var baseSrc = "class Base { ~Base() { } public static int BaseFinalizerRuns; }";
            var derivedSrc = "class Derived : Base { ~Derived() { } }";

            // 第一次 run：4 文件，N 走 force（NativeRelease），应写 4 条。
            var (_, _, stats1, _, _, pp1) = RewriteAllTracked(
                RemoveOptions(forceTypes: new[] { "N" }),
                (Lib("E.cs"), emptySrc),
                (Lib("N.cs"), NativeReleaseSrc.Replace("class C", "class N")),
                (Lib("Base.cs"), baseSrc),
                (Lib("Derived.cs"), derivedSrc));

            var sidecarPath = pp1.FinalizerSidecarPath;
            Assert.NotNull(sidecarPath);
            Assert.Equal(Path.Combine(root, FinalizerSidecarReport.FileName), sidecarPath);
            Assert.True(File.Exists(sidecarPath), "sidecar 必须真实落盘");
            Assert.Equal(4, stats1.FinalizersRemoved);
            Assert.Equal(1, stats1.FinalizersForceRemoved);
            Assert.Equal(4, stats1.FinalizerSidecarEntries);

            using (var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath)))
            {
                var rootEl = doc.RootElement;
                Assert.False(string.IsNullOrEmpty(rootEl.GetProperty("runId").GetString()));
                var entries = rootEl.GetProperty("entries");
                Assert.Equal(4, entries.GetArrayLength());

                var e0 = entries[0];
                Assert.Equal(0, e0.GetProperty("index").GetInt32());
                Assert.Equal("E", e0.GetProperty("typeFqn").GetString());
                Assert.Equal("lib/E.cs", e0.GetProperty("file").GetString());
                Assert.True(e0.GetProperty("line").GetInt32() > 0);
                Assert.True(e0.GetProperty("column").GetInt32() > 0);
                Assert.Equal("Empty", e0.GetProperty("bodyKind").GetString());
                Assert.Equal("Unpaired", e0.GetProperty("pairing").GetString());
                Assert.False(e0.GetProperty("hasBaseFinalizer").GetBoolean());
                Assert.False(e0.GetProperty("forced").GetBoolean());
                Assert.Contains("fn-empty-marker", e0.GetProperty("body").GetString());
                var sha = e0.GetProperty("bodySha256").GetString();
                Assert.Matches("^[0-9a-f]{8}$", sha);

                var e1 = entries[1];
                Assert.Equal("N", e1.GetProperty("typeFqn").GetString());
                Assert.True(e1.GetProperty("forced").GetBoolean());
                Assert.Equal("NativeRelease", e1.GetProperty("bodyKind").GetString());
                Assert.Contains("Marshal.FreeHGlobal", e1.GetProperty("body").GetString());

                var e3 = entries[3];
                Assert.Equal("Derived", e3.GetProperty("typeFqn").GetString());
                Assert.True(e3.GetProperty("hasBaseFinalizer").GetBoolean());
                Assert.Equal("Base", e3.GetProperty("baseFinalizerFqn").GetString());
            }

            // 第二次 run（新 preprocessor，同目录，不给 force）：N 被 5204 拒绝，
            // sidecar 在 run 开始重置，只留 3 条且不含 N。
            var (_, _, stats2, diags2, _, pp2) = RewriteAllTracked(
                RemoveOptions(),
                (Lib("E.cs"), emptySrc),
                (Lib("N.cs"), NativeReleaseSrc.Replace("class C", "class N")),
                (Lib("Base.cs"), baseSrc),
                (Lib("Derived.cs"), derivedSrc));

            Assert.Equal(3, stats2.FinalizersRemoved);
            Assert.Equal(1, stats2.FinalizersRefused);
            Assert.True(HasCode(diags2, MemberDesugarDiagnosticCode.FinalizerRemoveRefused));
            Assert.Equal(sidecarPath, pp2.FinalizerSidecarPath);
            using (var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath)))
            {
                var entries = doc.RootElement.GetProperty("entries");
                Assert.Equal(3, entries.GetArrayLength());
                var fqns = entries.EnumerateArray()
                    .Select(e => e.GetProperty("typeFqn").GetString()).ToList();
                Assert.DoesNotContain("N", fqns);
                Assert.Equal(new[] { "E", "Base", "Derived" }, fqns);
                // index 在重置后重新从 0 编排。
                Assert.Equal(0, entries[0].GetProperty("index").GetInt32());
                Assert.Equal(2, entries[2].GetProperty("index").GetInt32());
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- B5) residual 闸门（FN-10）----

    [Fact]
    public void ResidualGate_UnregisteredDestructor_Emits5210()
    {
        // 测试 hook：绕过 rewriter 直接对「有终结器但无任何 plan 台账」的输出树调对账。
        var src = "class C { ~C() { } }";
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create("md-fn-residual",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var rewriter = new FinalizerRewriter(
            model,
            Array.Empty<FinalizerPlan>(),
            Array.Empty<FinalizerPlan>(),
            new MemberDesugarOptions(),
            FinalizerSidecarReport.CreateFor(tree.FilePath),
            stats, diags, tree.FilePath);

        rewriter.ReconcileResidual(tree.GetCompilationUnitRoot());

        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerResidual,
            MemberDesugarSeverity.Warning));
        Assert.Equal(1, stats.FinalizerResidual);
        Assert.Equal(1, stats.SkippedByReason[MemberDesugarDiagnosticCode.FinalizerResidual]);
    }

    [Fact]
    public void ResidualGate_Strict_Promotes5210ToError()
    {
        var src = "class C { ~C() { } }";
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create("md-fn-residual2",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var diags = new List<MemberDesugarDiagnostic>();
        var rewriter = new FinalizerRewriter(
            model,
            Array.Empty<FinalizerPlan>(),
            Array.Empty<FinalizerPlan>(),
            new MemberDesugarOptions { Strict = true },
            FinalizerSidecarReport.CreateFor(tree.FilePath),
            new MemberDesugarStatistics(), diags, tree.FilePath);

        rewriter.ReconcileResidual(tree.GetCompilationUnitRoot());

        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerResidual,
            MemberDesugarSeverity.Error));
    }

    [Fact]
    public void APolicy_OutputHasZero5210()
    {
        var (_, _, _, diags, _) = RewriteAll(null,
            ("Base.cs", "class Base { ~Base() { } }"),
            ("Derived.cs", "class Derived : Base { ~Derived() { } }"));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerResidual));
    }

    [Fact]
    public void RemovePolicy_HappyPath_Zero5210()
    {
        var (_, _, stats, diags, _) = RewriteAll(RemoveOptions(),
            ("A.cs", "class A { ~A() { } }"),
            ("B.cs", "class B : A { ~B() { } }"));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerResidual));
        Assert.Equal(0, stats.FinalizerResidual);
    }

    // ---- B6) 幂等 + 干净文件字节级不变 ----

    [Fact]
    public void RemovePolicy_Idempotent_SecondRunNoChange()
    {
        var src = "class C { ~C() { } }";
        var (first, changed1, _, diags1, _, _) = RewriteFnTracked(src, RemoveOptions(), ExtraFile);
        Assert.True(changed1);
        Assert.DoesNotContain("~C(", first);
        Assert.True(HasCode(diags1, MemberDesugarDiagnosticCode.FinalizerRemoved));

        // B 档产物（已无终结器）再跑一次：零改动、零 5201/5210、输出一致。
        var (second, changed2, _, diags2, _) = RewriteFn(first, RemoveOptions(), ExtraFile);
        Assert.False(changed2);
        Assert.Equal(first, second);
        Assert.False(HasCode(diags2, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.False(HasCode(diags2, MemberDesugarDiagnosticCode.FinalizerResidual));
    }

    [Fact]
    public void RemovePolicy_ABothTiers_TwoRunsOutputStable()
    {
        var src = "class C { ~C() { } }";
        var (a1, _, _, _, _) = RewriteFn(src, null, ExtraFile);
        var (a2, _, _, _, _) = RewriteFn(a1, null, ExtraFile);
        Assert.Equal(Normalize(a1), Normalize(a2));

        var (b1, _, _, _, _) = RewriteFn(src, RemoveOptions(), ExtraFile);
        var (b2, _, _, _, _) = RewriteFn(b1, RemoveOptions(), ExtraFile);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void RemovePolicy_CleanOtherFile_ByteIdentical()
    {
        var cleanPath = "B.cs";
        var cleanSrc = "class Other { public void Keep() { } }";
        var cleanRoundTrip = CSharpSyntaxTree
            .ParseText(cleanSrc, ParseOptions, path: cleanPath)
            .GetCompilationUnitRoot().ToFullString();

        var (outputs, changed, stats, _, _) = RewriteAll(RemoveOptions(),
            ("A.cs", "class A { ~A() { } }"),
            (cleanPath, cleanSrc));

        Assert.True(changed[0]);
        Assert.False(changed[1], "无终结器的干净文件必须走 clean 复制");
        Assert.Equal(cleanRoundTrip, outputs[1]);
        Assert.Equal(1, stats.FinalizersRemoved);
    }

    // ---- B7) ExcludePlans 回退：成员保留、无 5210（适配器回退循环同路径）----

    [Fact]
    public void ExcludePlans_FinalizerPlan_Retained_No5201_No5210()
    {
        var src = "class C { ~C() { } }";
        var model = MemberDesugarPropertyUnitTests.ParseWithSemanticsPublic(src);
        var pp = new MemberDesugarPreprocessor(RemoveOptions());
        pp.BindCompilation(model.Compilation);
        var plan = pp.Plans.OfType<FinalizerPlan>().Single();
        pp.ExcludePlans(new[] { plan.Id });

        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);

        Assert.False(result.Changed, "被排除的 finalizer plan 成员保留");
        Assert.Contains("~C()", result.OutputCode);
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerRemoved));
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerResidual));
        Assert.Equal(0, stats.FinalizersRemoved);
        Assert.Equal(0, stats.FinalizerResidual);
    }

    // ---- B8) 泛型/嵌套泛型类型：纯语法 FQN 必须与 ToDisplayString 源码形同形 ----
    // 回归：SynthesizeTypeFqn 曾输出 C`1 arity 形而符号键为 C<T>，导致泛型类终结器
    // 在 A/B 两档都被 FN-10 误报 CS2J5210。

    [Fact]
    public void GenericClass_Finalizer_APolicy_NoResidual5210()
    {
        var src = """
            namespace Ns;

            class Box<T>
            {
                ~Box() { }
            }

            class Outer<T, U>
            {
                class Inner
                {
                    ~Inner() { }
                }
            }
            """;
        var (output, changed, _, diags, _) = RewriteFn(src, null, ExtraFile);

        Assert.False(changed, "A 档零树改动");
        Assert.Contains("~Box()", output);
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerResidual),
            "泛型类型 FQN 键不一致导致误报 5210: "
            + string.Join("; ", diags.Where(d => d.Code == MemberDesugarDiagnosticCode.FinalizerResidual)
                .Select(d => d.Message)));
        Assert.Equal(2, diags.Count(d => d.Code == MemberDesugarDiagnosticCode.FinalizerRejected));
    }

    [Fact]
    public void GenericClass_Finalizer_BPolicy_Removed_NoResidual5210()
    {
        var src = """
            namespace Ns;

            class Box<T>
            {
                ~Box() { }
            }

            class Outer<T>
            {
                class Inner
                {
                    ~Inner() { }
                }
            }
            """;
        var (output, changed, stats, diags, plans) = RewriteFn(src, RemoveOptions(), ExtraFile);

        Assert.True(changed, "B 档 Empty 终结器必须改树");
        Assert.DoesNotContain("~Box(", output);
        Assert.DoesNotContain("~Inner(", output);
        Assert.False(HasCode(diags, MemberDesugarDiagnosticCode.FinalizerResidual),
            "泛型类型删除后 FN-10 误报 5210: "
            + string.Join("; ", diags.Where(d => d.Code == MemberDesugarDiagnosticCode.FinalizerResidual)
                .Select(d => d.Message)));
        Assert.Equal(2, stats.FinalizersRemoved);
        Assert.All(plans.OfType<FinalizerPlan>(),
            p => Assert.Equal(FinalizerAction.Removed, p.Action));
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, ExtraFile);
    }
}
