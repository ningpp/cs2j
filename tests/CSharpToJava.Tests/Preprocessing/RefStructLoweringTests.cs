using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.RefStructLowering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// NORM-REF-STRUCT 的可安全部分：ref-struct-lowering 只对无 ref-only 成员的
/// 用户 ref struct 去 ref；含 Span/ref 字段等保守信号时原文保留并报 RSL001。
/// 同时覆盖注册/顺序/owner 与 normalization-audit 的 support library 跳过规则。
/// </summary>
public sealed class RefStructLoweringTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private const string MarkerName = "Cs2jByRefLikeMarker";

    /// <summary>
    /// 与 src/Cs2jPrelude 中注入的支撑类型保持一致；独立编译校验时需要它才能解析
    /// 降级后 struct 的标记接口与 Helper 调用。
    /// </summary>
    private const string SupportSource = @"
namespace Cs2jPrelude
{
    public interface Cs2jByRefLikeMarker { }
    public static class Cs2jByRefLikeHelper
    {
        public static bool IsByRefLike(global::System.Type type)
            => type != null && typeof(Cs2jByRefLikeMarker).IsAssignableFrom(type);
    }
}
";

    private static RefStructLoweringResult Run(string source) =>
        new RefStructLoweringPreprocessor().RewriteFile(source, ParseWithSemantics(source));

    // ── 1. 空 ref struct → struct，输出可编译且无残留 ──

    [Fact]
    public void EmptyRefStruct_IsLowered_AndCompiles()
    {
        const string src = """
            ref struct R { }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("struct R", result.OutputCode);
        Assert.DoesNotContain("ref struct", result.OutputCode);
        Assert.Contains(MarkerName, result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsLowered);
        Assert.Equal(0, result.Statistics.RefStructsPreserved);
        AssertOutputCompiles(result.OutputCode);
        AssertNoRefStructResidual(result.OutputCode);
    }

    // ── 1b. typeof 使用 → 仍然降级，byref-like 语义改由标记接口承载 ──

    [Fact]
    public void RefStruct_UsedInTypeOf_IsLowered_WithMarker()
    {
        const string src = """
            using System;
            ref struct R { }
            class C { Type T = typeof(R); }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("ref struct R", result.OutputCode);
        Assert.Contains(MarkerName, result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsLowered);
        Assert.Equal(0, result.Statistics.RefStructsPreserved);
        Assert.Empty(result.Diagnostics);
        AssertOutputCompiles(result.OutputCode);
        AssertNoRefStructResidual(result.OutputCode);
    }

    // ── 1c. Type.IsByRefLike 消费点 → Helper 查询（C# 预言机语义闭环） ──

    [Fact]
    public void Type_IsByRefLike_IsRewritten_ToHelper()
    {
        const string src = """
            using System;
            class C { static bool Check(Type t) => t.IsByRefLike; }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("Cs2jByRefLikeHelper.IsByRefLike(t)", result.OutputCode);
        Assert.DoesNotContain("t.IsByRefLike", result.OutputCode);
        Assert.Equal(0, result.Statistics.RefStructsLowered);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. readonly ref struct → readonly struct ──

    [Fact]
    public void ReadonlyRefStruct_BecomesReadonlyStruct_AndCompiles()
    {
        const string src = """
            readonly ref struct R { }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("readonly struct R", result.OutputCode);
        Assert.DoesNotContain("ref struct", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsLowered);
        AssertOutputCompiles(result.OutputCode);
        AssertNoRefStructResidual(result.OutputCode);
    }

    // ── 3. Span<T> 字段 → 保留 ref + RSL001 ──

    [Fact]
    public void RefStruct_WithSpanField_IsPreserved_WithRSL001()
    {
        const string src = """
            using System;
            ref struct R { public Span<byte> S; }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref struct R", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsPreserved);
        Assert.Equal(0, result.Statistics.RefStructsLowered);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RefStructLoweringDiagnosticCode.RSL001, diagnostic.Code);
        Assert.Equal(RefStructLoweringSeverity.Warning, diagnostic.Severity);
        Assert.Contains("preserved", diagnostic.Message, StringComparison.Ordinal);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 4. ref 字段 → 保留 ref + RSL001 ──

    [Fact]
    public void RefStruct_WithRefField_IsPreserved_WithRSL001()
    {
        const string src = """
            ref struct R { public ref int X; }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ref struct R", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefStructsPreserved);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(RefStructLoweringDiagnosticCode.RSL001, diagnostic.Code);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 5. 普通 struct/class 不受影响 ──

    [Fact]
    public void OrdinaryStructAndClass_AreUntouched()
    {
        const string src = """
            struct S { public int X; }
            class C { public int Y; }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.RefStructsLowered);
        Assert.Equal(0, result.Statistics.RefStructsPreserved);
        Assert.Empty(result.Diagnostics);
    }

    // ── 6. 幂等：对输出再跑不再变化 ──

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        const string src = """
            readonly ref struct R { }
            ref struct S { }
            """;

        var first = Run(src);
        Assert.True(first.Changed);
        Assert.Equal(2, first.Statistics.RefStructsLowered);

        var second = Run(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.RefStructsLowered);
        Assert.Equal(0, second.Statistics.RefStructsPreserved);
    }

    // ── 7. 注册/顺序/owner ──

    [Fact]
    public void Registry_slots_after_stackalloc_before_closure_lift_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.RefStructLowering);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Declarative, descriptor.Stage);
        Assert.Equal(new[] { PassIds.StackAllocLowering }, descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.Equal(".cs2j-ref-struct-lowering-src", descriptor.IntermediateDirectoryName);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var stackAlloc = ordered.IndexOf(PassIds.StackAllocLowering);
        var refStruct = ordered.IndexOf(PassIds.RefStructLowering);
        var closure = ordered.IndexOf(PassIds.ClosureLift);

        Assert.True(stackAlloc >= 0 && refStruct >= 0 && closure >= 0);
        Assert.True(stackAlloc < refStruct, "ref-struct-lowering must run after stackalloc-lowering");
        Assert.True(refStruct < closure, "ref-struct-lowering must run before closure-lift");

        // NORM-REF-STRUCT is owned by the dedicated ref-struct-lowering pass.
        Assert.Equal(
            PassIds.RefStructLowering,
            ResidualRegistry.CreateDefault()
                .Descriptors.Single(d => d.Code == "NORM-REF-STRUCT").OwnerPassId);
    }

    // ── 8. normalization-audit 跳过 support library ──

    [Theory]
    [InlineData("Cs2jRefOutIn/ByRef.cs")]
    [InlineData("Cs2jRefOutIn\\RefStructByRef.cs")]
    [InlineData("Cs2jPrelude/CsPrelude.cs")]
    [InlineData("cs2junsignedmath/UnsignedMath.cs")]
    [InlineData("Cs2jArray/ArrayHelper.cs")]
    [InlineData("Cs2jTuples/generated.cs")]
    [InlineData("./Cs2jRefOutIn/ByRef.cs")]
    public void Audit_Skips_SupportLibrary_Paths(string relativePath)
    {
        Assert.True(NormalizationAuditPassAdapter.IsSupportLibraryRelativePath(relativePath));
    }

    [Theory]
    [InlineData("json/src/System/Text/Json/Reader/Utf8JsonReader.MultiSegment.cs")]
    [InlineData("src/Cs2jRefOutIn/ByRef.cs")]
    [InlineData("Cs2jRefOutInX/ByRef.cs")]
    [InlineData("Cs2jRefOutIn.cs")]
    public void Audit_Does_Not_Skip_User_Paths(string relativePath)
    {
        Assert.False(NormalizationAuditPassAdapter.IsSupportLibraryRelativePath(relativePath));
    }

    [Fact]
    public async Task Audit_RewriteFile_Skips_SupportLibrary_Without_Needing_A_SemanticModel()
    {
        var adapter = new NormalizationAuditPassAdapter(
            Array.Empty<string>(),
            "audit",
            failOnResidual: false,
            reportPath: null,
            verbose: false,
            filterUnsupportedProjects: true,
            compatNamespacePrefixes: null);

        var rewrite = await adapter.RewriteFileAsync(new PassFileContext
        {
            SourceCode = "public ref struct R { private ref int _x; }",
            SourceFilePath = "Cs2jRefOutIn/ByRef.cs",
            RelativePath = "Cs2jRefOutIn/ByRef.cs",
            DestinationFilePath = "out/Cs2jRefOutIn/ByRef.cs",
            SemanticModel = null,
        });

        Assert.Null(rewrite);
        Assert.Contains("support-library", adapter.BuildChainSummaryLine(), StringComparison.Ordinal);
    }

    // ── Helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(SupportSource, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "ref-struct-lowering-test",
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(SupportSource, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "ref-struct-lowering-output",
            new[] { tree, support },
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

    private static void AssertNoRefStructResidual(string output)
    {
        var hits = ResidualRegistry.CreateDefault()
            .Scan(ParseWithSemantics(output), "src/Sample.cs")
            .Where(hit => hit.Code == "NORM-REF-STRUCT")
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
