using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C06 unsafe-ffm 配对索引（paired-index）模型：指针返回值方法族（UF020 最大
/// 子族，Xml/Html RawTextWriter 四文件）。统一规则覆盖——
/// ① 返回白名单元素指针的方法整体配对：T* M(T* p) → int M(T[] p, int pIndex)，
/// 体内 *p/p[i]/p±n/p++/p-q/比较按 int 索引归一，调用点实参展开为「数组+索引」，
/// 返回指针与入参指针同源时继承 provenance（入口契约）；
/// ③④ fixed 块内以指针形参为根的 void 游标方法同样配对（返回类型保持 void）；
/// ⑥ 配对约定沿调用图传播；
/// 真不可证实参（整数强转指针等外部源）保留 UF020，文件回滚。
/// </summary>
public class UnsafeFfmPointerReturnMethodTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-prm-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static UnsafeFfmResult Rewrite(string src, UnsafeFfmOptions? options = null)
    {
        var model = ParseWithSemantics(src);
        return new UnsafeFfmPreprocessor(options ?? new UnsafeFfmOptions())
            .RewriteFile(src, model);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("uffm-prm-verify",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private const string EntitySource = """
        unsafe class Writer
        {
            byte[] buf = new byte[64];

            static byte* AmpEntity(byte* p)
            {
                p[0] = (byte)'&';
                return p + 1;
            }

            void Run()
            {
                fixed (byte* p = buf)
                {
                    AmpEntity(p);
                }
            }
        }
        """;

    [Fact]
    public void PointerReturnMethod_SignatureBecomesInt_ArgumentsExpandAtCallSite()
    {
        var result = Rewrite(EntitySource);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("static int AmpEntity(byte[] p, int pIndex)", result.OutputCode);
        Assert.Contains("return pIndex + 1;", result.OutputCode);
        // fixed 调用点：指针实参展开为「伴生数组 + int 索引」。
        Assert.Contains("AmpEntity(_p_array, p)", result.OutputCode);
        Assert.DoesNotContain("byte* ", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void PointerReturnMethod_ResultAssignedBackToCursor_ThenDereferenced()
    {
        var src = """
            unsafe class Writer
            {
                byte[] buf = new byte[64];

                static byte* Entity(byte* p)
                {
                    *p = 1;
                    return p + 1;
                }

                void Run()
                {
                    fixed (byte* p = buf)
                    {
                        byte* q = p;
                        q = Entity(q);
                        *q = 9;
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("q = Entity(_p_array, q);", result.OutputCode);
        Assert.Contains("_p_array[q] = 9;", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void PairedCallChain_ForwardsSameSourceProvenance()
    {
        var src = """
            unsafe class Chain
            {
                static byte* Inner(byte* p)
                {
                    *p = 1;
                    return p + 1;
                }

                static byte* Outer(byte* p)
                {
                    return Inner(p);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("static int Inner(byte[] p, int pIndex)", result.OutputCode);
        Assert.Contains("static int Outer(byte[] p, int pIndex)", result.OutputCode);
        Assert.Contains("return Inner(p, pIndex);", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void PointerReturnMethod_IncrementDerefCompoundReturn_AllIndexized()
    {
        var src = """
            unsafe class Enc
            {
                static byte* Encode(byte* pSrc, char* pSrcEnd, byte* pDst)
                {
                    if (pSrc + 1 < pSrcEnd)
                    {
                        pDst[0] = 1;
                        pDst[1] = 2;
                        pDst += 2;
                        return pDst;
                    }

                    return pDst;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains(
            "static int Encode(byte[] pSrc, int pSrcIndex, char[] pSrcEnd, int pSrcEndIndex, byte[] pDst, int pDstIndex)",
            result.OutputCode);
        Assert.Contains("(pSrcIndex + 1) < pSrcEndIndex", result.OutputCode);
        Assert.Contains("pDst[pDstIndex] = 1;", result.OutputCode);
        Assert.Contains("pDst[pDstIndex + 1] = 2;", result.OutputCode);
        Assert.Contains("pDstIndex += 2;", result.OutputCode);
        Assert.Contains("return pDstIndex;", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedCursorVoidMethod_IsPaired_ReturnTypeStaysVoid()
    {
        var src = """
            unsafe class Copier
            {
                byte[] buf = new byte[64];

                void Copy(char* pSrc, char* pSrcEnd)
                {
                    fixed (byte* pDstBegin = buf)
                    {
                        byte* pDst = pDstBegin;
                        byte* pDstEnd = pDst + (pSrcEnd - pSrc);
                        while (pDst < pDstEnd)
                        {
                            *pDst = (byte)*pSrc;
                            pDst++;
                            pSrc++;
                        }
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains(
            "void Copy(char[] pSrc, int pSrcIndex, char[] pSrcEnd, int pSrcEndIndex)",
            result.OutputCode);
        Assert.DoesNotContain("int Copy(", result.OutputCode);
        // 形参索引自增、跨形参差值（入口契约同源）归一为 int 运算。
        Assert.Contains("pSrcIndex++;", result.OutputCode);
        Assert.Contains("(pSrcEndIndex - pSrcIndex)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void PointerReturnInsideFixed_ReturnsLoweredIndex()
    {
        var src = """
            unsafe class W
            {
                byte[] buf = new byte[64];

                byte* Advance(byte* pDst)
                {
                    fixed (byte* b = buf)
                    {
                        return b + 2;
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int Advance(byte[] pDst, int pDstIndex)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnprovablePointerArgument_KeepsUF020_FilePreserved()
    {
        var src = """
            unsafe class W
            {
                static byte* Entity(byte* p)
                {
                    return p + 1;
                }

                void Run()
                {
                    Entity((byte*)0);
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
        // 文件回滚：指针签名/调用原样保留。
        Assert.Contains("static byte* Entity(byte* p)", result.OutputCode);
        Assert.Contains("Entity((byte*)0);", result.OutputCode);
    }

    [Fact]
    public void PurePointerParameterMethod_WithoutFixedOrForwarding_NotPaired()
    {
        // 假阳防护：无 fixed、不转发给配对方法的纯指针形参方法只走 array-only
        // 既有路径（T* p → T[] p），不得引入 int 索引伴生形参。
        var src = """
            unsafe class Sum
            {
                static int Compute(byte* p, int len)
                {
                    int sum = 0;
                    for (int i = 0; i < len; i++)
                    {
                        sum += p[i];
                    }

                    return sum;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("static int Compute(byte[] p, int len)", result.OutputCode);
        Assert.DoesNotContain("pIndex", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void MethodLevelUnsafeModifier_WithLeadingTrivia_StillPaired()
    {
        // 根因回归（RawTextWriter 四文件 UF020 的直接成因）：方法自带 unsafe
        // 修饰符时，R1 RemoveUnsafe 把 unsafe token 的 trivia 并入相邻保留
        // 修饰符，Roslyn IsEquivalentTo 对非弹性 trivia 敏感、结构全等仍判否，
        // resolver 映射断链导致整方法配对丢失。去 trivia 兜底后应照常配对。
        var src = """
            class Writer
            {
                byte[] buf = new byte[64];

                /// <summary>写实体并前进游标。</summary>
                unsafe static byte* AmpEntity(byte* p)
                {
                    p[0] = (byte)'&';
                    return p + 1;
                }

                unsafe void Run()
                {
                    fixed (byte* p = buf)
                    {
                        AmpEntity(p);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("static int AmpEntity(byte[] p, int pIndex)", result.OutputCode);
        Assert.Contains("AmpEntity(_p_array, p)", result.OutputCode);
        Assert.DoesNotContain("byte* ", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void PointerReturnRewrite_IsIdempotent_SecondPassClean()
    {
        var first = Rewrite(EntitySource);
        Assert.True(first.Changed);
        Assert.False(first.HasErrors);
        AssertOutputCompiles(first.OutputCode);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(0, second.Statistics.PointerReturnsRewritten);
        AssertOutputCompiles(second.OutputCode);
    }
}
