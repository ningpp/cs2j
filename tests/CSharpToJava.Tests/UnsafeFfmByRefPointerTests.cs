using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C06 unsafe-ffm 配对索引（paired-index）模型：指针经
/// <c>Cs2jByRef.ByRef&lt;T*&gt;</c> holder/shadow 流动族（UF020 最大子族的
/// holder 半边，EncodeChar/CharToUTF8 与 Html writer 的 Create 提升调用点）。
/// 覆盖——
/// holder 形参 <c>ByRef&lt;T*&gt; h</c> → <c>ByRef&lt;int&gt; h, T[] hArray</c>；
/// shadow copy-in（<c>T* s = h.Value</c>）降为继承 holder 伴生数组的 FOLDS
/// int 局部，末尾 <c>h.Value = s</c> 回写不逃逸；shadow 参与链式配对调用时
/// 实参展开到伴生数组；调用点 <c>ByRef&lt;T*&gt;.Create&lt;T*&gt;</c> 提升为
/// <c>ByRef&lt;int&gt;.Create&lt;int&gt;</c>，访问器捕获的形参游标/R3 降级游标
/// 改写为 int 索引；真不可证捕获保留 UF020。
/// </summary>
public class UnsafeFfmByRefPointerTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string ByRefStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract T Value { get; set; }
                public static ByRef<T> Create<T>(System.Func<T> getter, System.Action<T> setter) => null!;
            }
        }
        """;

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-brp-test",
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
        var compilation = CSharpCompilation.Create("uffm-brp-verify",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    // using 必须在 namespace 声明之前（CS1529 会使整个绑定退化为 ErrorType）。
    private const string HolderCalleeSource = "using Cs2jByRef;\n" + ByRefStub + """

        unsafe class Writer
        {
            static byte* EncodeSurrogate(char* pSrc, char* pSrcEnd, byte* pDst)
            {
                pDst[0] = 1;
                pDst += 1;
                return pDst;
            }

            void EncodeChar(ByRef<char*> pSrc, char* pSrcEnd, ByRef<byte*> pDst)
            {
                char* __shadow0 = pSrc.Value;
                byte* __shadow1 = pDst.Value;
                __shadow1 = EncodeSurrogate(__shadow0, pSrcEnd, __shadow1);
                __shadow0 += 2;
                pSrc.Value = __shadow0;
                pDst.Value = __shadow1;
            }
        }
        """;

    [Fact]
    public void HolderCallee_ShadowCopyIn_PairedSignatureAndFlow()
    {
        var result = Rewrite(HolderCalleeSource);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);

        // holder 形参 → ByRef<int> + 伴生数组；指针形参 → 数组 + int 索引
        //（输入非限定书写，输出保留非限定 ByRef<int>）。
        Assert.Contains(
            "void EncodeChar(ByRef<int> pSrc, char[] pSrcArray, char[] pSrcEnd, int pSrcEndIndex, ByRef<int> pDst, byte[] pDstArray)",
            result.OutputCode);
        // shadow copy-in 降为 int 局部，读自 holder.Value；末尾回写保留。
        Assert.Contains("int __shadow0 = pSrc.Value;", result.OutputCode);
        Assert.Contains("int __shadow1 = pDst.Value;", result.OutputCode);
        Assert.Contains("pSrc.Value = __shadow0;", result.OutputCode);
        Assert.Contains("pDst.Value = __shadow1;", result.OutputCode);
        // shadow 复合赋值直接作用于 int 局部。
        Assert.Contains("__shadow0 += 2;", result.OutputCode);
        Assert.DoesNotContain("char* ", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void HolderCallee_ShadowArguments_ExpandToCompanionArrays()
    {
        var result = Rewrite(HolderCalleeSource);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        // 链式配对调用：shadow 按其 holder 伴生数组展开，pSrcEnd 按形参对展开。
        Assert.Contains(
            "EncodeSurrogate(pSrcArray, __shadow0, pSrcEnd, pSrcEndIndex, pDstArray, __shadow1)",
            result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    private const string CreateCallSiteSource = "using Cs2jByRef;\n" + ByRefStub + """

        unsafe class Writer
        {
            byte[] buf = new byte[64];

            void EncodeChar(ByRef<char*> pSrc, char* pSrcEnd, ByRef<byte*> pDst)
            {
                char* s = pSrc.Value;
                byte* d = pDst.Value;
                d[0] = (byte)s[0];
                s++;
                pSrc.Value = s;
                pDst.Value = d;
            }

            void WriteAttributeText(char* pSrc, char* pSrcEnd)
            {
                if (pSrcEnd - pSrc > 0)
                {
                    fixed (byte* pDstBegin = buf)
                    {
                        byte* pDst = pDstBegin;
                        ByRef<char*> __ref0 = ByRef<char*>.Create<char*>(() => pSrc, v => pSrc = v);
                        ByRef<byte*> __ref1 = ByRef<byte*>.Create<byte*>(() => pDst, v => pDst = v);
                        EncodeChar(__ref0, pSrcEnd, __ref1);
                    }
                }
            }
        }
        """;

    [Fact]
    public void CreateCallSite_LiftsToByRefInt_CapturedCursorsBecomeIndexes()
    {
        var result = Rewrite(CreateCallSiteSource);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains(
            "ByRef<int> __ref0 = ByRef<int>.Create<int>(() => pSrcIndex, v => pSrcIndex = v);",
            result.OutputCode);
        // R3 降级游标 pDst（伴生数组 _pDstBegin_array）：捕获其同名 int 局部。
        Assert.Contains(
            "ByRef<int> __ref1 = ByRef<int>.Create<int>(() => pDst, v => pDst = v);",
            result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void CreateCallSite_PairedCall_ExpandsHolderAndPointerArguments()
    {
        var result = Rewrite(CreateCallSiteSource);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        // __ref0/__ref1 后插捕获数组实参；pSrcEnd 展开形参数组+索引。
        Assert.Contains(
            "EncodeChar(__ref0, pSrc, pSrcEnd, pSrcEndIndex, __ref1, _pDstBegin_array)",
            result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void CreateCallSite_HolderCaptureInNestedFixed_UsesInnerLoweredArray()
    {
        var src = "using Cs2jByRef;\n" + ByRefStub + """

            unsafe class Writer
            {
                byte[] uri = new byte[64];

                static void CharToUTF8(ByRef<char*> pSrc, char* pSrcEnd, ByRef<byte*> pDst)
                {
                    char* s = pSrc.Value;
                    byte* d = pDst.Value;
                    d[0] = (byte)s[0];
                    pSrc.Value = s;
                    pDst.Value = d;
                }

                void WriteUri(char* pSrc, char* pSrcEnd)
                {
                    fixed (byte* pUriBuffer = uri)
                    {
                        byte* pByte = pUriBuffer;
                        byte* pEnd = pByte;
                        ByRef<char*> __ref2 = ByRef<char*>.Create<char*>(() => pSrc, v => pSrc = v);
                        ByRef<byte*> __ref3 = ByRef<byte*>.Create<byte*>(() => pEnd, v => pEnd = v);
                        CharToUTF8(__ref2, pSrcEnd, __ref3);
                        while (pByte < pEnd)
                        {
                            pByte++;
                        }
                    }
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        // 内层 fixed 降级局部 pEnd 的伴生数组作为 holder 实参传入。
        Assert.Contains("CharToUTF8(__ref2, pSrc, pSrcEnd, pSrcEndIndex, __ref3, _pUriBuffer_array)",
            result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void HolderCreate_UnprovableCapture_KeepsUF020_FilePreserved()
    {
        var src = "using Cs2jByRef;\n" + ByRefStub + """

            unsafe class Writer
            {
                unsafe byte* stray;

                void Run(byte* p)
                {
                    ByRef<byte*> __ref = ByRef<byte*>.Create<byte*>(() => stray, v => stray = v);
                    Consume(__ref, p);
                }

                static void Consume(ByRef<byte*> h, byte* p)
                {
                    h.Value = p;
                }
            }
            """;

        var result = Rewrite(src);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF020);
        // 文件回滚：holder 提升保持指针形态。
        Assert.Contains("ByRef<byte*>.Create<byte*>", result.OutputCode);
    }

    [Fact]
    public void HolderRewrite_IsIdempotent_SecondPassClean()
    {
        var first = Rewrite(CreateCallSiteSource);
        Assert.True(first.Changed);
        Assert.False(first.HasErrors);
        AssertOutputCompiles(first.OutputCode);

        var second = Rewrite(first.OutputCode);

        Assert.False(second.Changed);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(0, second.Statistics.PointerParametersRewritten);
        AssertOutputCompiles(second.OutputCode);
    }
}
