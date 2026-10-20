using System.IO;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C06 unsafe-ffm：<c>Encoding.UTF8.GetBytes(char*, int, byte*, int)</c> 指针
/// 重载在 paired-parameter offset 展开后因原 <see cref="MetadataArgRecord.RootZeroInvariant"/>
/// 仅记录 stackalloc 零不变种子、遗漏了 fixed/Pinned 游标零不变，导致
/// <see cref="UnsafeFfmDiagnosticCode.UF020"/> 误报；下游 async-normalize
/// 阶段因此无法展开为本机 array+index 托管形式，最终编译失败。
/// 本测试集验证修复后 <c>GetBytes(char*,int,byte*,int)</c> 能正确展开为
/// BCL 已有的 5 参 <c>GetBytes(char[],int,int,byte[],int)</c> 托管重载。
/// </summary>
public class UnsafeFfmEncodingGetBytesTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-encoding-getbytes",
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

    /// <summary>
    /// Repro 自 uri 项目 IriHelper.cs 真实退化点：形参 char* pInput 经 R5 配对
    /// rewrite 为 char[] + int 索引；fixed 游标 byte* pEncodedBytes 零不变，
    /// 尾部容量位 (MaxNumberOfBytesEncoded) 应消去并匹配 5 参托管重载。
    /// </summary>
    [Fact]
    public void PairedFixed_EncodingGetBytes_ExpandsToFiveParam()
    {
        var src = """
            using System.Text;
            unsafe class C
            {
                static int Encode(char* pInput, int next, bool surrogatePair)
                {
                    byte[] encodedBytes = new byte[16];
                    fixed (byte* pEncodedBytes = encodedBytes)
                    {
                        return Encoding.UTF8.GetBytes(pInput + next,
                            surrogatePair ? 2 : 1, pEncodedBytes, 16);
                    }
                }
            }
            """;

        var result = Rewrite(src);
        Assert.True(result.Changed, "Expected managed expansion to succeed:\n" +
            string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, "Unexpected UF020:\n" +
            string.Join("\n", result.Diagnostics));
        Assert.Contains("GetBytes(", result.OutputCode);
    }

    /// <summary>
    /// fixed 游标 + 指针参数不是 paired 而是 plain 局部接收 fixed cursor：
    /// R3 将 fixed buffer 局部改写为 int 索引，零不变性应被正确识别。
    /// </summary>
    [Fact]
    public void FixedCursorLocalAsPointerArg_ExpandsToFiveParam()
    {
        var src = """
            using System.Text;
            unsafe class C
            {
                static int Encode(char* pInput, int next)
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
        Assert.True(result.Changed, "Expected expansion:\n" +
            string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, "Unexpected UF020:\n" +
            string.Join("\n", result.Diagnostics));
    }

    /// <summary>
    /// 栈缓冲降级路径：stackalloc 种子零不变，paired 形参 char* pInput。
    /// BCL 同样只提供 5 参托管重载。
    /// </summary>
    [Fact]
    public void StackAllocCursorFixedOutput_ExpandsToFiveParam()
    {
        var src = """
            using System.Text;
            unsafe class C
            {
                static int Encode(char* pInput, int next)
                {
                    byte* pEncodedBytes = stackalloc byte[16];
                    return Encoding.UTF8.GetBytes(pInput + next, 1, pEncodedBytes, 16);
                }
            }
            """;

        var result = Rewrite(src);
        Assert.True(result.Changed, "Expected stackalloc expansion:\n" +
            string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, "Unexpected UF020:\n" +
            string.Join("\n", result.Diagnostics));
    }

    /// <summary>
    /// 常量偏移 &encodedBytes[0] 形式：fixed cursor 带 Zero 元素访问模式
    /// 展开，零不变性应被识别。
    /// </summary>
    [Fact]
    public void AddressOfZeroElementFixedCursor_ExpandsToFiveParam()
    {
        var src = """
            using System.Text;
            unsafe class C
            {
                static int Encode(char* pInput, int next)
                {
                    byte[] encodedBytes = new byte[4];
                    fixed (byte* pEncodedBytes = &encodedBytes[0])
                    {
                        return Encoding.UTF8.GetBytes(pInput + next, 1, pEncodedBytes, 4);
                    }
                }
            }
            """;

        var result = Rewrite(src);
        Assert.True(result.Changed, "Expected expansion with AddressOf-zero cursor:\n" +
            string.Join("\n", result.Diagnostics));
        Assert.False(result.HasErrors, "Unexpected UF020:\n" +
            string.Join("\n", result.Diagnostics));
    }

    /// <summary>
    /// 容量位无法消去的边界场景：fixed 游标偏移非零 (pEncodedBytes = &encodedBytes[2])，
    /// 此时零展开不成立，展开应在原调用点停下并发 UF020，行为与本 fix 前一致。
    /// </summary>
    [Fact]
    public void AddressOfNonZeroElementFixedCursor_KeepsUF020()
    {
        var src = """
            using System.Text;
            unsafe class C
            {
                static int Encode(char* pInput, int next)
                {
                    byte[] encodedBytes = new byte[8];
                    fixed (byte* pEncodedBytes = &encodedBytes[2])
                    {
                        return Encoding.UTF8.GetBytes(pInput + next, 1, pEncodedBytes, 2);
                    }
                }
            }
            """;

        var result = Rewrite(src);
        // 非零偏移：无零不变性，容量位不能消去，找不到 5 参/6 参托管重载 → 应 UF020。
        // 不强制 Changed=false：仍可能因门控命中有其他转换；但谛视必含 UF020。
        Assert.Contains(result.Diagnostics, d =>
            d.Code == UnsafeFfmDiagnosticCode.UF020);
    }
}
