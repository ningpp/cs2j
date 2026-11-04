using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.StackAllocLowering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M2 stackalloc-lowering：独立只读后处理 pass 在 unsafe-ffm 之后降级残留
/// stackalloc。覆盖 Span/ReadOnlySpan 目标（直接/三目）、可证明的裸指针局部
/// + Span 构造，以及无法证明的裸指针保留 + SL001、幂等与注册顺序。
/// </summary>
public sealed class StackAllocLoweringTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static StackAllocLoweringResult Run(string source) =>
        new StackAllocLoweringPreprocessor().RewriteFile(source, ParseWithSemantics(source));

    // ── 1. Span<byte> 直接赋值 → 堆数组，无 residual ──

    [Fact]
    public void SpanTarget_DirectInitializer_LowersToHeapArray_NoResidual_AndCompiles()
    {
        const string src = """
            using System;
            class C
            {
                static void M(int n)
                {
                    Span<byte> s = stackalloc byte[n];
                    s[0] = 1;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Contains("new byte[n]", result.OutputCode);
        Assert.Equal(1, result.Statistics.SpanTargetSitesLowered);
        Assert.Equal(0, result.Statistics.RejectedSites);
        AssertOutputCompiles(result.OutputCode);
        AssertNoStackAllocResidual(result.OutputCode);
    }

    // ── 2. 三目 Span 目标：两分支都是堆数组，输出可编译 ──

    [Fact]
    public void SpanTarget_Conditional_LowersBothBranchesToArrays_AndCompiles()
    {
        const string src = """
            using System;
            using System.Buffers;
            class C
            {
                static void M(bool condition, int n)
                {
                    byte[]? pooled = null;
                    Span<byte> s = condition
                        ? stackalloc byte[n]
                        : (pooled = ArrayPool<byte>.Shared.Rent(n));
                    s[0] = 1;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Contains("new byte[n]", result.OutputCode);
        Assert.Equal(1, result.Statistics.SpanTargetSitesLowered);
        AssertOutputCompiles(result.OutputCode);
        AssertNoStackAllocResidual(result.OutputCode);
    }

    // ── 3. 裸指针局部 + Span 构造 → 堆数组 + (ptr, 0, length) ──

    [Fact]
    public void PointerLocal_UsedOnlyAsSpanArgument_LowersToArray_AndRemovesPointer()
    {
        const string src = """
            using System;
            unsafe class C
            {
                static void M(int n)
                {
                    char* ptr = stackalloc char[n];
                    var s = new Span<char>(ptr, n);
                    s[0] = 'x';
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Contains("char[] ptr = new char[n];", result.OutputCode);
        Assert.Contains("new Span<char>(ptr, 0, n)", result.OutputCode);
        Assert.DoesNotContain("char*", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerLocalSitesLowered);
        Assert.Equal(1, result.Statistics.SpanConstructionsRewritten);
        Assert.Equal(0, result.Statistics.RejectedSites);
        AssertOutputCompiles(result.OutputCode);
        AssertNoStackAllocResidual(result.OutputCode);
    }

    // ── 4. ReadOnlySpan 变体（直接赋值 + 指针构造） ──

    [Fact]
    public void ReadOnlySpanTarget_DirectAndPointerConstruction_LowerSafely()
    {
        const string src = """
            using System;
            unsafe class C
            {
                static void M(int n)
                {
                    ReadOnlySpan<byte> direct = stackalloc byte[n];

                    byte* ptr = stackalloc byte[n];
                    ReadOnlySpan<byte> constructed = new ReadOnlySpan<byte>(ptr, n);

                    Console.Write(direct.Length + constructed.Length);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.DoesNotContain("byte*", result.OutputCode);
        Assert.Contains("new byte[n]", result.OutputCode);
        Assert.Contains("new ReadOnlySpan<byte>(ptr, 0, n)", result.OutputCode);
        Assert.Equal(1, result.Statistics.SpanTargetSitesLowered);
        Assert.Equal(1, result.Statistics.PointerLocalSitesLowered);
        Assert.Equal(1, result.Statistics.SpanConstructionsRewritten);
        AssertOutputCompiles(result.OutputCode);
        AssertNoStackAllocResidual(result.OutputCode);
    }

    // ── 5. 反例：指针被解引用 → 原样保留 + SL001，不生成坏代码 ──

    [Fact]
    public void PointerLocal_WithDereference_IsPreserved_WithSL001_AndNoBadCode()
    {
        const string src = """
            unsafe class C
            {
                static void M(int n)
                {
                    char* p = stackalloc char[n];
                    *p = 'x';
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.DoesNotContain("char[]", result.OutputCode);
        Assert.Equal(1, result.Statistics.RejectedSites);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == StackAllocLoweringSeverity.Warning
            && d.Code == StackAllocLoweringDiagnosticCode.SL001
            && d.Message.Contains("preserved", StringComparison.Ordinal));
        Assert.Equal(1, result.Diagnostics.Count);
    }

    // ── 6. 幂等：对输出再跑不再变化 ──

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        const string src = """
            using System;
            unsafe class C
            {
                static void M(int n)
                {
                    Span<byte> s = stackalloc byte[n];
                    char* ptr = stackalloc char[n];
                    var span = new Span<char>(ptr, n);
                    s[0] = 1;
                    span[0] = 'x';
                }
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.SpanTargetSitesLowered);
        Assert.Equal(0, second.Statistics.PointerLocalSitesLowered);
    }

    // ── 7. 注册/顺序：unsafe-ffm 之后、closure-lift 之前，默认开启 ──

    [Fact]
    public void Registry_slots_after_unsafe_ffm_before_closure_lift_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.StackAllocLowering);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.Equal(new[] { PassStubCatalog.Ids.UnsafeFfm }, descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.Equal(".cs2j-stackalloc-lowering-src", descriptor.IntermediateDirectoryName);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var unsafeFfm = ordered.IndexOf(PassStubCatalog.Ids.UnsafeFfm);
        var lowering = ordered.IndexOf(PassIds.StackAllocLowering);
        var closure = ordered.IndexOf(PassStubCatalog.Ids.ClosureLift);

        Assert.True(unsafeFfm >= 0 && lowering >= 0 && closure >= 0);
        Assert.True(unsafeFfm < lowering, "stackalloc-lowering must run after unsafe-ffm");
        Assert.True(lowering < closure, "stackalloc-lowering must run before closure-lift");
        Assert.DoesNotContain(PassIds.NormalizationAudit, ordered);

        var auditPlan = PassPlanResolver.Resolve(
            registry, enablePassIds: new[] { PassIds.NormalizationAudit });
        Assert.True(auditPlan.IsValid);
        Assert.Equal(PassIds.NormalizationAudit, auditPlan.Enabled[^1].Id);
    }

    // ── Helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "stackalloc-lowering-test",
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
            "stackalloc-lowering-output",
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

    private static void AssertNoStackAllocResidual(string output)
    {
        var hits = ResidualRegistry.CreateDefault()
            .Scan(ParseWithSemantics(output), "src/Sample.cs")
            .Where(hit => hit.Code == "NORM-STACKALLOC")
            .ToList();

        Assert.Empty(hits);
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
