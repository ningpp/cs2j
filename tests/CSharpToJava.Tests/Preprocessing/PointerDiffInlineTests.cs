using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.PointerDiffInline;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// pointer-diff-inline：独立表达式 pass 在 ref-out-in 之后、unsafe-ffm 之前，
/// 把文件内 private static 的同类型指针差工具方法（<c>PtrDiff</c>）在调用点
/// 内联为 <c>(R)(a - b)</c> 并在无引用时删除声明；非差分/不同类型/非 private
/// static 的形态保留并报 PDI001。
/// </summary>
public sealed class PointerDiffInlineTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static PointerDiffInlineResult Run(string source) =>
        new PointerDiffInlinePreprocessor().RewriteFile(source, ParseWithSemantics(source));

    // ── 1. 简单 byte* 差分 + 调用 → (int)(p - q)，方法删除，可编译 ──

    [Fact]
    public void SimpleBytePointerDiff_IsInlined_AndTheHelperIsRemoved()
    {
        const string src = """
            unsafe class C
            {
                private static int PtrDiff(byte* a, byte* b) => (int)(a - b);

                static int M(byte* p, byte* q) => PtrDiff(p, q);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("(int)(p - q)", result.OutputCode);
        Assert.DoesNotContain("PtrDiff", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDiffCallSitesInlined);
        Assert.Equal(1, result.Statistics.PointerDiffMethodsInlined);
        Assert.Equal(0, result.Statistics.PointerDiffMethodsPreserved);
        Assert.Empty(result.Diagnostics);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. char* 版：uint/>>>/(Byte*) 包装仍等价于 char* 同类型差 ──

    [Fact]
    public void CharPointerDiff_WithByteReinterpretAndUnsignedShift_IsInlined()
    {
        const string src = """
            using System;

            unsafe class C
            {
                private static int PtrDiff(char* a, char* b)
                {
                    return (int)((uint)((int)((UInt32)((Byte*)a - (Byte*)b)) >>> 1));
                }

                static int M(char* p, char* q) => PtrDiff(p, q);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("(int)(p - q)", result.OutputCode);
        Assert.DoesNotContain("PtrDiff", result.OutputCode);
        Assert.DoesNotContain(">>>", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDiffCallSitesInlined);
        Assert.Equal(1, result.Statistics.PointerDiffMethodsInlined);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2b. 非 int 返回类型按返回类型 cast ──

    [Fact]
    public void NonIntReturnType_CastsTheInlinedDifferenceToTheReturnType()
    {
        const string src = """
            unsafe class C
            {
                private static long PtrDiff(byte* a, byte* b) => (long)(a - b);

                static long M(byte* p, byte* q) => PtrDiff(p, q);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("(long)(p - q)", result.OutputCode);
        Assert.DoesNotContain("PtrDiff", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDiffCallSitesInlined);
        Assert.Equal(1, result.Statistics.PointerDiffMethodsInlined);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 3. 非差分方法（a + b / 常量偏移）保留 + PDI001 ──

    [Fact]
    public void NonDifferenceHelper_IsPreserved_WithPDI001()
    {
        const string src = """
            unsafe class C
            {
                private static int AddOffset(byte* a, byte* b) => (int)((a + 1) - b);

                static int M(byte* p, byte* q) => AddOffset(p, q);
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.PointerDiffCallSitesInlined);
        Assert.Equal(0, result.Statistics.PointerDiffMethodsInlined);
        Assert.Equal(1, result.Statistics.PointerDiffMethodsPreserved);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == PointerDiffInlineSeverity.Warning
            && d.Code == PointerDiffInlineDiagnosticCode.PDI001
            && d.Message.Contains("preserved", StringComparison.Ordinal));
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 4. 外部/非 private static 方法、不同指针元素类型 → 保留 ──

    [Fact]
    public void ExternalAndMismatchedPointerHelpers_ArePreserved()
    {
        const string src = """
            unsafe class Helper
            {
                public static int PtrDiff(byte* a, byte* b) => (int)(a - b);
            }

            unsafe class C
            {
                private static int DiffMixed(byte* a, char* b) => (int)((char*)a - b);

                static int M(byte* p, byte* q) => Helper.PtrDiff(p, q);
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.PointerDiffCallSitesInlined);
        Assert.Equal(0, result.Statistics.PointerDiffMethodsInlined);
        Assert.Equal(2, result.Statistics.PointerDiffMethodsPreserved);
        Assert.Equal(2, result.Diagnostics.Count(d =>
            d.Code == PointerDiffInlineDiagnosticCode.PDI001));
        Assert.DoesNotContain("(int)(p - q)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 5. 多调用点：全部内联；nameof 引用保留方法声明 ──

    [Fact]
    public void MultipleCallSites_AreInlined_ButNameOfKeepsTheDeclaration()
    {
        const string src = """
            unsafe class C
            {
                private static int PtrDiff(byte* a, byte* b) => (int)(a - b);

                static int M1(byte* p, byte* q) => PtrDiff(p, q) + PtrDiff(q, p);

                static string M2() => nameof(PtrDiff);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("(int)(p - q)", result.OutputCode);
        Assert.Contains("(int)(q - p)", result.OutputCode);
        Assert.Contains("nameof(PtrDiff)", result.OutputCode);
        Assert.Equal(2, result.Statistics.PointerDiffCallSitesInlined);
        Assert.Equal(0, result.Statistics.PointerDiffMethodsInlined);
        Assert.Equal(1, result.Statistics.PointerDiffMethodsPreserved);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 6. 幂等 ──

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        const string src = """
            unsafe class C
            {
                private static int PtrDiff(byte* a, byte* b) => (int)(a - b);

                static int M(byte* p, byte* q) => PtrDiff(p, q);
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);

        var second = new PointerDiffInlinePreprocessor()
            .RewriteFile(first.OutputCode, ParseWithSemantics(first.OutputCode));

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.PointerDiffCallSitesInlined);
        Assert.Equal(0, second.Statistics.PointerDiffMethodsInlined);
    }

    // ── 7. 注册：fixed-span-to-array 之后、unsigned/unsafe-ffm 之前，默认开启 ──

    [Fact]
    public void Registry_slots_after_fixed_span_to_array_before_unsigned_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.PointerDiffInline);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Expression, descriptor.Stage);
        Assert.Equal(new[] { PassIds.RefOutIn }, descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.Equal(".cs2j-pointer-diff-inline-src", descriptor.IntermediateDirectoryName);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var refOutIn = ordered.IndexOf(PassIds.RefOutIn);
        var fixedSpan = ordered.IndexOf(PassIds.FixedSpanToArray);
        var pointerDiff = ordered.IndexOf(PassIds.PointerDiffInline);
        var unsigned = ordered.IndexOf(PassIds.Unsigned);
        var unsafeFfm = ordered.IndexOf(PassStubCatalog.Ids.UnsafeFfm);

        Assert.True(refOutIn >= 0 && fixedSpan >= 0 && pointerDiff >= 0
            && unsigned >= 0 && unsafeFfm >= 0);
        Assert.True(refOutIn < pointerDiff, "pointer-diff-inline must run after ref-out-in");
        Assert.True(fixedSpan < pointerDiff, "pointer-diff-inline must run after fixed-span-to-array");
        Assert.True(pointerDiff < unsigned, "pointer-diff-inline must run before unsigned");
        Assert.True(pointerDiff < unsafeFfm, "pointer-diff-inline must run before unsafe-ffm");

        var disabled = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.PointerDiffInline });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.PointerDiffInline);
    }

    // ── helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "pointer-diff-inline-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "pointer-diff-inline-output",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten output has compile errors:\n" +
            string.Join("\n", errors) + "\n--- output ---\n" + output);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
