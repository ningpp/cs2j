using System.IO;
using System.Linq;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M3b：HexConverter.ToString 式「fixed 指针 → (IntPtr) 元组字段 → lambda 内
/// 从指针重建 ReadOnlySpan」往返的显式拒绝路径（设计 D7、§6、§9 M3）。
/// 归一后 fixed 指针只是相对数组索引：把它当绝对地址取整、或从它重建
/// Span 都属逃逸，必须显式 UF021/UF022，不追求可运行 Java：
/// <list type="bullet">
/// <item><c>(IntPtr)p</c>（R5 域参数 / R3 域 fixed 局部）→ UF021；</item>
/// <item><c>new Span&lt;byte&gt;(p, len)</c> / <c>new ReadOnlySpan&lt;byte&gt;((byte*)p,
/// len)</c>（fixed 域内）→ UF022。</item>
/// </list>
/// </summary>
public class FixedPointerSpanRoundTripTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    [Fact]
    public void IntPtrCast_OverR5PointerParameter_Rejected_UF021()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static System.IntPtr M(byte* p) {
        return (IntPtr)p;
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Equal(1, pre.Statistics.Uf021Rejections);
    }

    [Fact]
    public void IntPtrCast_InsideFixedBlock_Rejected_UF021()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static System.IntPtr M(byte[] data) {
        fixed (byte* p = data) {
            return (IntPtr)p;
        }
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
    }

    [Fact]
    public void SpanReconstruction_FromFixedPointer_Rejected_UF022()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static byte M(byte[] data) {
        fixed (byte* p = data) {
            System.Span<byte> s = new System.Span<byte>(p, 4);
            return s[0];
        }
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF022);
        Assert.Equal(1, pre.Statistics.Uf022Rejections);
    }

    [Fact]
    public void ReadOnlySpanReconstruction_FromCastedFixedPointer_Rejected_UF022()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static byte M(byte[] data) {
        fixed (byte* p = data) {
            System.ReadOnlySpan<byte> s = new System.ReadOnlySpan<byte>((byte*)p, 4);
            return s[0];
        }
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF022);
    }

    // ---- helpers ----

    private static UnsafeFfmResult Preprocess(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "c06m3b-roundtrip",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new UnsafeFfmPreprocessor(new UnsafeFfmOptions())
            .RewriteFile(sourceCode, model);
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
}
