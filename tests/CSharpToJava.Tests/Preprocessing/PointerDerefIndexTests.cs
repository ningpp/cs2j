using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.PointerDerefIndex;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// pointer-deref-index：独立只读表达式后处理 pass 在 unsafe-ffm 之后把可证明的
/// 指针解引用降级为指针元素访问。覆盖 <c>*p</c>、<c>*(p ± k)</c>、
/// <c>*(T*)expr</c>、左值赋值、副作用保守保留、取址不受影响、幂等与注册顺序。
/// </summary>
public sealed class PointerDerefIndexTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static PointerDerefIndexResult Run(string source) =>
        new PointerDerefIndexPreprocessor().RewriteFile(source, ParseWithSemantics(source));

    // ── 1. *p → p[0]，输出可编译 ──

    [Fact]
    public void SimpleDereference_LowersToElementAccess_AndCompiles()
    {
        const string src = """
            unsafe class C
            {
                static int M(int* p) => *p;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("p[0]", result.OutputCode);
        Assert.DoesNotContain("*p", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDerefsRewritten);
        Assert.Equal(0, result.Statistics.PointerDerefsPreserved);
        AssertOutputCompiles(result.OutputCode);
        AssertNoPointerIndirectionResidual(result.OutputCode);
    }

    // ── 2. *(p + 2) / *(2 + p) → p[2] ──

    [Fact]
    public void PointerAddition_BothOperandOrders_LowerToTheSameIndex()
    {
        const string src = """
            unsafe class C
            {
                static int M(int* p) => *(p + 2) + *(2 + p);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("p[2]", result.OutputCode);
        Assert.DoesNotContain("*(p + 2)", result.OutputCode);
        Assert.DoesNotContain("*(2 + p)", result.OutputCode);
        Assert.Equal(2, result.Statistics.PointerDerefsRewritten);
        AssertOutputCompiles(result.OutputCode);
        AssertNoPointerIndirectionResidual(result.OutputCode);
    }

    // ── 3. *((int*)ptr) → ((int*)ptr)[0]；*(int*)(p + 2) → 强转后索引 ──

    [Fact]
    public void PointerCastDereference_LowersToParenthesizedElementAccess()
    {
        const string src = """
            unsafe class C
            {
                static int M(int* ptr) => *((int*)ptr) + *(int*)(ptr + 2);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("((int*)ptr)[0]", result.OutputCode);
        Assert.Contains("((int*)(ptr + 2))[0]", result.OutputCode);
        Assert.Equal(2, result.Statistics.PointerDerefsRewritten);
        AssertOutputCompiles(result.OutputCode);
        AssertNoPointerIndirectionResidual(result.OutputCode);
    }

    // ── 4. 减法：*(p - 3) → p[-(3)] ──

    [Fact]
    public void PointerSubtraction_LowersToNegatedIndex()
    {
        const string src = """
            unsafe class C
            {
                static int M(int* p) => *(p - 3);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("p[-(3)]", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDerefsRewritten);
        AssertOutputCompiles(result.OutputCode);
        AssertNoPointerIndirectionResidual(result.OutputCode);
    }

    // ── 5. 左值：*p = 3 → p[0] = 3，仍可编译 ──

    [Fact]
    public void DereferenceAsAssignmentTarget_StaysAnLValue()
    {
        const string src = """
            unsafe class C
            {
                static void M(int* p)
                {
                    *p = 3;
                    *(p + 1) += 4;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("p[0] = 3;", result.OutputCode);
        Assert.Contains("p[1] += 4;", result.OutputCode);
        Assert.Equal(2, result.Statistics.PointerDerefsRewritten);
        AssertOutputCompiles(result.OutputCode);
        AssertNoPointerIndirectionResidual(result.OutputCode);
    }

    // ── 6. 副作用接收者：*(p++) 保留 + PDI001 ──

    [Fact]
    public void SideEffectReceiver_IsPreserved_WithPDI001()
    {
        const string src = """
            unsafe class C
            {
                static int M(int* p) => *(p++);
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("*(p++)", result.OutputCode);
        Assert.Equal(0, result.Statistics.PointerDerefsRewritten);
        Assert.Equal(1, result.Statistics.PointerDerefsPreserved);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == PointerDerefIndexSeverity.Warning
            && d.Code == PointerDerefIndexDiagnosticCode.PDI001
            && d.Message.Contains("preserved", StringComparison.Ordinal));
        Assert.Single(result.Diagnostics);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 7. 取址 &expr 不受影响 ──

    [Fact]
    public void AddressOf_IsNeverTouched()
    {
        const string src = """
            unsafe class C
            {
                static int* M(int x)
                {
                    return &x;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("&x", result.OutputCode);
        Assert.Equal(0, result.Statistics.PointerDerefsRewritten);
        Assert.Equal(0, result.Statistics.PointerDerefsPreserved);
        Assert.Empty(result.Diagnostics);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 8. 幂等：对输出再跑不再变化 ──

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        const string src = """
            unsafe class C
            {
                static int M(int* p) => *(p + 1) + *(2 + p) + *p;
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);

        var second = new PointerDerefIndexPreprocessor()
            .RewriteFile(first.OutputCode, ParseWithSemantics(first.OutputCode));

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.PointerDerefsRewritten);
    }

    // ── 9. 注册：unsafe-ffm 之后、ref-struct-lowering 之后、closure-lift 之前，默认开启 ──

    [Fact]
    public void Registry_slots_after_ref_struct_lowering_before_closure_lift_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.PointerDerefIndex);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassStage.Expression, descriptor.Stage);
        Assert.Equal(new[] { PassIds.UnsafeFfm }, descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.Equal(".cs2j-pointer-deref-index-src", descriptor.IntermediateDirectoryName);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(pass => pass.Id).ToList();
        var unsafeFfm = ordered.IndexOf(PassIds.UnsafeFfm);
        var stackAlloc = ordered.IndexOf(PassIds.StackAllocLowering);
        var refStruct = ordered.IndexOf(PassIds.RefStructLowering);
        var pointerDeref = ordered.IndexOf(PassIds.PointerDerefIndex);
        var closure = ordered.IndexOf(PassIds.ClosureLift);

        Assert.True(unsafeFfm >= 0 && stackAlloc >= 0 && refStruct >= 0
            && pointerDeref >= 0 && closure >= 0);
        Assert.True(unsafeFfm < stackAlloc, "pointer-deref-index must run after unsafe-ffm");
        Assert.True(stackAlloc < pointerDeref, "pointer-deref-index must run after stackalloc-lowering");
        Assert.True(refStruct < pointerDeref, "pointer-deref-index must run after ref-struct-lowering");
        Assert.True(pointerDeref < closure, "pointer-deref-index must run before closure-lift");

        var disabled = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.PointerDerefIndex });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.PointerDerefIndex);
    }

    // ── 10. Utf8JsonWriterTests 的外部指针 override 复刻：return *text; → return text[0]; ──

    [Fact]
    public void ExternalPointerOverrideDereference_LowersToElementAccess_AndCompiles()
    {
        const string src = """
            using System.Text.Encodings.Web;

            public class BuggyJavaScriptEncoder : JavaScriptEncoder
            {
                public override int MaxOutputCharactersPerInputCharacter
                    => throw new System.NotImplementedException();

                public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
                {
                    // Access the text pointer even though it might be null and text length is 0.
                    return *text;
                }

                public override unsafe bool TryEncodeUnicodeScalar(
                    int unicodeScalar, char* buffer, int bufferLength,
                    out int numberOfCharactersWritten)
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }

                public override bool WillEncode(int unicodeScalar) => false;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("return text[0];", result.OutputCode);
        Assert.DoesNotContain("return *text;", result.OutputCode);
        Assert.Equal(1, result.Statistics.PointerDerefsRewritten);
        Assert.Equal(0, result.Statistics.PointerDerefsPreserved);
        AssertOutputCompiles(result.OutputCode);
        AssertNoPointerIndirectionResidual(result.OutputCode);
    }

    // ── Helpers ──

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "pointer-deref-index-test",
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
            "pointer-deref-index-output",
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

    private static void AssertNoPointerIndirectionResidual(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var residuals = tree.GetRoot().DescendantNodes()
            .OfType<PrefixUnaryExpressionSyntax>()
            .Where(node => node.IsKind(SyntaxKind.PointerIndirectionExpression))
            .ToList();

        Assert.Empty(residuals);
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
