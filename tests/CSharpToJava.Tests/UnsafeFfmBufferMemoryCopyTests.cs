using System.IO;
using System.Linq;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C06 unsafe-ffm：<c>Buffer.MemoryCopy(void* source, void* destination,
/// ulong destinationSizeInBytes, ulong sourceBytesToCopy)</c> 在两侧实参
/// 都可溯源到「托管数组 + 元素索引」（fixed 帧/别名偏移/R5 flow/
/// GCHandle Pinned 句柄）时归一为 <c>Buffer.BlockCopy</c> 字节拷贝；
/// 指针实参上的 byte* 重解释强转（char*→byte*）按元素尺寸换算字节偏移。
/// 不可证明/不可度量元素的强转保持 UF021（整文回滚，不静默放行）。
/// </summary>
public class UnsafeFfmBufferMemoryCopyTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-memcopy-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static UnsafeFfmResult Rewrite(string src)
    {
        var model = ParseWithSemantics(src);
        return new UnsafeFfmPreprocessor(new UnsafeFfmOptions()).RewriteFile(src, model);
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
        var compilation = CSharpCompilation.Create("uffm-memcopy-verify",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    /// <summary>IriHelper.EscapeUnescapeIri 扩容站点的忠实复刻：pinned 旧
    /// 数组（pDest）→ fixed 新数组（pNewDest），双向 char*→byte* 强转。</summary>
    [Fact]
    public void PinnedSource_FixedDestination_BidirectionalCharReinterpret_LoweredToBlockCopy()
    {
        var src = """
            using System.Runtime.InteropServices;
            unsafe class C
            {
                static void M(int end, int start)
                {
                    char[] dest = new char[end - start];
                    GCHandle destHandle = GCHandle.Alloc(dest, GCHandleType.Pinned);
                    char* pDest = (char*)destHandle.AddrOfPinnedObject();
                    int destOffset = 3;
                    int newBufferLength = dest.Length + 90;
                    char[] newDest = new char[newBufferLength];
                    fixed (char* pNewDest = newDest)
                    {
                        System.Buffer.MemoryCopy((byte*)pDest, (byte*)pNewDest,
                            newBufferLength * sizeof(char), destOffset * sizeof(char));
                    }
                    destHandle.Free();
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.MemoryCopiesLowered);
        Assert.Equal(0, result.Statistics.Uf021Rejections);
        Assert.DoesNotContain("MemoryCopy", result.OutputCode);
        Assert.Contains("global::System.Buffer.BlockCopy(dest, (pDest) * 2,", result.OutputCode);
        Assert.Contains("_pNewDest_array, (pNewDest) * 2, destOffset * sizeof(char)",
            result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void FixedBytePointers_WithOffsets_ByteOffsetsKept_LoweredToBlockCopy()
    {
        var src = """
            unsafe class C
            {
                static void M(byte[] src, byte[] dst)
                {
                    fixed (byte* pSrc = &src[2])
                    fixed (byte* pDst = &dst[4])
                    {
                        System.Buffer.MemoryCopy((byte*)(pSrc + 1), (byte*)(pDst + 1),
                            100UL, 5UL);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.MemoryCopiesLowered);
        Assert.DoesNotContain("MemoryCopy", result.OutputCode);
        Assert.Contains("global::System.Buffer.BlockCopy(", result.OutputCode);
        // byte 元素步长 1：元素索引即字节偏移，不放大（实参位的冗余括号
        // 会被格式化器剥除）。
        Assert.Contains("pSrc + 1", result.OutputCode);
        Assert.Contains("pDst + 1", result.OutputCode);
        Assert.DoesNotContain("pSrc + 1) *", result.OutputCode);
        Assert.DoesNotContain("pDst + 1) *", result.OutputCode);
        // 非 int 计数实参显式收窄为 BlockCopy 的 int count（ulong 形参消失；
        // 格式化可能去掉字面量外的冗余括号，只校验收窄形态）。
        Assert.Contains("(int)", result.OutputCode);
        Assert.Contains("5UL", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void CharPointers_WithOffset_OffsetScaledByElementSize_Lowered()
    {
        var src = """
            unsafe class C
            {
                static void M(char[] chars, char[] more)
                {
                    fixed (char* p = chars)
                    fixed (char* q = more)
                    {
                        System.Buffer.MemoryCopy((byte*)(p + 1), (byte*)q,
                            more.Length * 2, 4);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.MemoryCopiesLowered);
        Assert.Contains("(p + 1) * 2", result.OutputCode);
        Assert.Contains("(q) * 2", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void BothSidesPinnedHandles_NoFixedBlock_LoweredToBlockCopy()
    {
        var src = """
            using System.Runtime.InteropServices;
            unsafe class C
            {
                static void M()
                {
                    char[] a = new char[8];
                    char[] b = new char[16];
                    GCHandle ha = GCHandle.Alloc(a, GCHandleType.Pinned);
                    GCHandle hb = GCHandle.Alloc(b, GCHandleType.Pinned);
                    char* pa = (char*)ha.AddrOfPinnedObject();
                    char* pb = (char*)hb.AddrOfPinnedObject();
                    System.Buffer.MemoryCopy((byte*)pa, (byte*)pb, 16 * 2, 8 * 2);
                    ha.Free();
                    hb.Free();
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.MemoryCopiesLowered);
        Assert.Contains("global::System.Buffer.BlockCopy(a, (pa) * 2, b, (pb) * 2, 8 * 2)",
            result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void MixedByteSource_CharDestination_ImplicitVoidPointerConversion_Lowered()
    {
        var src = """
            unsafe class C
            {
                static void M(byte[] bytes, char[] chars)
                {
                    fixed (byte* ps = bytes)
                    fixed (char* pd = chars)
                    {
                        // 源侧无强转（byte* 隐式转 void*），目标侧 char*→byte*。
                        System.Buffer.MemoryCopy(ps, (byte*)pd, chars.Length * 2, 2);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.MemoryCopiesLowered);
        Assert.Contains("BlockCopy(_ps_array, ps, _pd_array, (pd) * 2, 2)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void NestedVoidAndByteCasts_StrippedAndResolved_Lowered()
    {
        var src = """
            unsafe class C
            {
                static void M(byte[] a, byte[] b)
                {
                    fixed (byte* p = a)
                    fixed (byte* q = b)
                    {
                        System.Buffer.MemoryCopy((void*)(byte*)p, (void*)(byte*)q, 8, 4);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.MemoryCopiesLowered);
        Assert.Contains("BlockCopy(_p_array, p, _q_array, q, 4)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void NonPrimitiveStructPointerCast_RemainsUF021_FilePreserved()
    {
        var src = """
            unsafe struct Foo { public int x; }
            unsafe class C
            {
                static void M(Foo[] a, Foo[] b)
                {
                    fixed (Foo* p = a)
                    fixed (Foo* q = b)
                    {
                        System.Buffer.MemoryCopy((byte*)p, (byte*)q, 32, 16);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.MemoryCopiesLowered);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Contains("MemoryCopy", result.OutputCode);
    }

    [Fact]
    public void IntegerOriginPointerCast_AsMemoryCopyArgument_NotLowered_Rejected()
    {
        var src = """
            unsafe class C
            {
                static byte* Obtain();
                static void M(byte[] data)
                {
                    byte* rogue = (byte*)0;
                    fixed (byte* q = data)
                    {
                        System.Buffer.MemoryCopy((byte*)rogue, (byte*)q, 8, 4);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.MemoryCopiesLowered);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
    }

    /// <summary>回归核心修复点：catalog 因⑧信号识别为 paired 后，R5 改写签名并
    /// 以 FoldsToOffset=true 登记 flow；R6 VisitInvocationExpression 门控必须
    /// 命中路径B（root=pInput 自身即配对 flow key）进入
    /// TryExpandMetadataInvocationAsPairedOffset 降级 fallback，而非被
    /// IsOldPointerTyped 过滤跳过或回落到 base.VisitInvocationExpression 不作
    /// 处理。单元测试编译可能无法提供 Encoding 的完整托管重载，此时 fallback
    /// 走到 UF020 即门控命中证据。</summary>
    [Fact]
    public void PairedParameter_ParamOffsetArg_GateTriggersFallback()
    {
        // catalog 识别⑧信号：方法把 pInput+next 外发元数据指针方法。
        // R5 paired 改写 char[] pInput + int pInputIndex，flow FoldsToOffset=true。
        // fixed 游标的 byte* pEncodedBytes 零不变，容量位 (4) 可消去，应匹配
        // GetBytes(char[], int, int, byte[], int) 5 参托管重载（BCL 无 6 参形式）。
        var src = """
            using System.Text;
            unsafe class C
            {
                static int M(char* pInput, int next)
                {
                    byte[] encodedBytes = new byte[4];
                    fixed (byte* pEncodedBytes = encodedBytes)
                    {
                        return Encoding.UTF8.GetBytes(pInput + next, 1, pEncodedBytes, 4);
                    }
                }
            }
            """;

        var result = Rewrite(src);

        // 进入 TryRewriteMetadataPointerInvocation 即门控命中。paired-parameter offset
        // 路径下 fixed 缓冲零不变，应成功展开为 GetBytes(char[],int,int,byte[],int)。
        Assert.True(result.Changed, "Expected 5-param managed expansion to succeed");
        Assert.False(result.HasErrors, "Unexpected UF020:\n" +
            string.Join("\n", result.Diagnostics));
        Assert.Contains("GetBytes(", result.OutputCode);
    }

    [Fact]
    public void LoweredOutput_IsIdempotent_SecondRunClean()
    {
        var src = """
            using System.Runtime.InteropServices;
            unsafe class C
            {
                static void M(int end, int start)
                {
                    char[] dest = new char[end - start];
                    GCHandle destHandle = GCHandle.Alloc(dest, GCHandleType.Pinned);
                    char* pDest = (char*)destHandle.AddrOfPinnedObject();
                    int destOffset = 3;
                    int newBufferLength = dest.Length + 90;
                    char[] newDest = new char[newBufferLength];
                    fixed (char* pNewDest = newDest)
                    {
                        System.Buffer.MemoryCopy((byte*)pDest, (byte*)pNewDest,
                            newBufferLength * sizeof(char), destOffset * sizeof(char));
                    }
                    destHandle.Free();
                }
            }
            """;

        var first = Rewrite(src);
        Assert.True(first.Changed, string.Join("\n", first.Diagnostics));
        Assert.Contains("BlockCopy", first.OutputCode);

        var second = Rewrite(first.OutputCode);
        Assert.False(second.HasErrors, string.Join("\n", second.Diagnostics));
        Assert.Equal(0, second.Statistics.MemoryCopiesLowered);
        Assert.DoesNotContain("MemoryCopy", second.OutputCode);
        Assert.Contains("BlockCopy", second.OutputCode);
    }
}
