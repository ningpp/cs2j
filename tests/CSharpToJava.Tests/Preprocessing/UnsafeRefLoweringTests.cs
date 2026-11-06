using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.UnsafeRefLowering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M3 unsafe-ref-lowering：独立 ABI pass 在 ref-out-in 之后降级残留的
/// <c>ref T x = ref MemoryMarshal.GetReference(span)</c> + Unsafe 内在函数读取。
/// 覆盖 GetKey 精确片段、不稳定 span、其他 Unsafe 用法、普通 ref local 负例、
/// 幂等与注册顺序/NORM-REF-LOCAL owner。
/// </summary>
public sealed class UnsafeRefLoweringTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static UnsafeRefLoweringResult Run(string source) =>
        new UnsafeRefLoweringPreprocessor().RewriteFile(source, ParseWithSemantics(source));

    // ── 1. 精确 GetKey 片段 → span 读取，无 ref local，输出可编译 ──

    [Fact]
    public void GetKey_shape_lowers_to_span_reads_without_ref_local_and_compiles()
    {
        const string src = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            class C
            {
                static ulong GetKey(ReadOnlySpan<byte> name)
                {
                    ulong key;
                    ref byte reference = ref MemoryMarshal.GetReference(name);
                    int length = name.Length;
                    if (length > 7)
                    {
                        key = (ulong)Unsafe.ReadUnaligned<ulong>(ref reference) & 0x00ffffffffffffffUL;
                    }
                    else
                    {
                        key = length > 5
                            ? (ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref reference, 4)) << 32
                            : 0UL;
                        var offset = length - 1;
                        key = Unsafe.Add(ref reference, offset);
                    }
                    return key;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("ref byte", result.OutputCode);
        Assert.Contains("MemoryMarshal.Read<ulong>(name)", result.OutputCode);
        Assert.Contains("MemoryMarshal.Read<ushort>(name.Slice(4))", result.OutputCode);
        Assert.Contains("name[offset]", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefLocalsLowered);
        Assert.Equal(2, result.Statistics.ReadUnalignedSitesLowered);
        Assert.Equal(1, result.Statistics.AddValueSitesLowered);
        Assert.Equal(0, result.Statistics.RejectedRefLocals);
        AssertOutputCompiles(result.OutputCode);
        AssertNoRefLocalResidual(result.OutputCode);
    }

    // ── 2. span 是复杂调用 → 不稳定引用，保留 ref local + URL001 ──
    [Fact]
    public void Unstable_span_expression_preserves_ref_local_with_URL001()
    {
        const string src = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            class C
            {
                static ReadOnlySpan<byte> GetSpan() => default;

                static ulong M()
                {
                    ref byte reference = ref MemoryMarshal.GetReference(GetSpan());
                    return Unsafe.ReadUnaligned<ulong>(ref reference);
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref byte reference", result.OutputCode);
        Assert.Equal(1, result.Statistics.RejectedRefLocals);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(UnsafeRefLoweringDiagnosticCode.URL001, diagnostic.Code);
        Assert.Equal(UnsafeRefLoweringSeverity.Warning, diagnostic.Severity);
        Assert.Contains("stable", diagnostic.Message, StringComparison.Ordinal);
    }

    // ── 3. 其他 Unsafe 内在函数 → 保留 + URL001 ──

    [Fact]
    public void Other_unsafe_intrinsic_use_preserves_ref_local_with_URL001()
    {
        const string src = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            class C
            {
                static ulong M(ReadOnlySpan<byte> name)
                {
                    ref byte reference = ref MemoryMarshal.GetReference(name);
                    return (ulong)Unsafe.As<byte, ulong>(ref reference);
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(1, result.Statistics.RejectedRefLocals);
        Assert.Equal(0, result.Statistics.RefLocalsLowered);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(UnsafeRefLoweringDiagnosticCode.URL001, diagnostic.Code);
        Assert.Contains("unsupported", diagnostic.Message, StringComparison.Ordinal);
    }

    // ── 4. 负向：普通 ref local（无 Unsafe）不处理 ──

    [Fact]
    public void Plain_ref_local_without_unsafe_is_left_untouched()
    {
        const string src = """
            class C
            {
                void M()
                {
                    int x = 1;
                    ref int r = ref x;
                    r = 2;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.RefLocalsLowered);
        Assert.Equal(0, result.Statistics.RejectedRefLocals);
    }

    // ── 5. 幂等：对输出再跑不再变化 ──

    [Fact]
    public void Rewrite_is_idempotent()
    {
        const string src = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            class C
            {
                static ulong GetKey(ReadOnlySpan<byte> name)
                {
                    ref byte reference = ref MemoryMarshal.GetReference(name);
                    int length = name.Length;
                    var offset = length - 1;
                    return (ulong)Unsafe.ReadUnaligned<ulong>(ref reference)
                        + Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref reference, 4))
                        + Unsafe.Add(ref reference, offset);
                }
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);
        AssertOutputCompiles(first.OutputCode);

        var second = Run(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.RefLocalsLowered);
        Assert.Equal(0, second.Statistics.RejectedRefLocals);
    }

    // ── 6. 注册/顺序：ref-out-in 之后、unsigned/unsafe-ffm 之前，默认开启 ──

    [Fact]
    public void Registry_slots_after_ref_out_in_before_unsigned_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.UnsafeRefLowering);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.Equal(new[] { PassIds.RefOutIn }, descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.Equal(".cs2j-unsafe-ref-lowering-src", descriptor.IntermediateDirectoryName);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var refOutIn = ordered.IndexOf(PassIds.RefOutIn);
        var lowering = ordered.IndexOf(PassIds.UnsafeRefLowering);
        var unsigned = ordered.IndexOf(PassIds.Unsigned);
        var unsafeFfm = ordered.IndexOf(PassIds.UnsafeFfm);

        Assert.True(refOutIn >= 0 && lowering >= 0 && unsigned >= 0 && unsafeFfm >= 0);
        Assert.True(refOutIn < lowering, "unsafe-ref-lowering must run after ref-out-in");
        Assert.True(lowering < unsigned, "unsafe-ref-lowering must run before unsigned");
        Assert.True(lowering < unsafeFfm, "unsafe-ref-lowering must run before unsafe-ffm");
        Assert.DoesNotContain(PassIds.NormalizationAudit, ordered);

        var auditPlan = PassPlanResolver.Resolve(
            registry, enablePassIds: new[] { PassIds.NormalizationAudit });
        Assert.True(auditPlan.IsValid);
        Assert.Equal(PassIds.NormalizationAudit, auditPlan.Enabled[^1].Id);

        var residual = ResidualRegistry.CreateDefault().Descriptors
            .Single(d => d.Code == "NORM-REF-LOCAL");
        Assert.Equal(PassIds.UnsafeRefLowering, residual.OwnerPassId);
    }

    // ── Helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "unsafe-ref-lowering-test",
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
            "unsafe-ref-lowering-output",
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

    private static void AssertNoRefLocalResidual(string output)
    {
        var hits = ResidualRegistry.CreateDefault()
            .Scan(ParseWithSemantics(output), "src/Sample.cs")
            .Where(hit => hit.Code == "NORM-REF-LOCAL")
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
