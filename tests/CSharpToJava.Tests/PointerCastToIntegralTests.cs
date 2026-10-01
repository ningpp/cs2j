using System.IO;
using System.Linq;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M3b：指针↔整数强转归一（设计 D7、§6 UF021、§9 M3）。
/// <list type="bullet">
/// <item>同数组（含 R5 继承流别名）指针差 <c>(int)(q - p)</c> 允许：归一为
/// int 索引差，int 目标直接产出索引表达式；</item>
/// <item>裸地址/绝对地址 <c>(int)p</c>、<c>(IntPtr)p</c>、<c>(long)p</c> →
/// UF021 显式拒绝；</item>
/// <item>整数反向造指针 <c>(byte*)x</c> → UF021。</item>
/// </list>
/// 断言以归一结果与诊断为主。
/// </summary>
public class PointerCastToIntegralTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    [Fact]
    public void SameArrayDifference_CastToInt_NormalizesToIndexDifference()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static int M(byte* p) {
        byte* q = p;
        q += 3;
        return (int)(q - p);
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        var code = pre.OutputCode;
        Assert.Contains("return _q_index;", code);
        Assert.Contains("_q_index += 3;", code);

        Assert.Equal(1, pre.Statistics.PointerDifferences);
        Assert.Equal(1, pre.Statistics.PointerArithmeticPropagated);
        Assert.Equal(0, pre.Statistics.PointerIncrements);
        Assert.Equal(0, pre.Statistics.Uf021Rejections);
    }

    [Fact]
    public void SameSourceDifference_CastToLong_WrapsIndexDifference()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static long M(byte* p) {
        byte* q = p;
        return (long)(q - p);
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        // 差值恒为 0（q/p 同源同偏移），Simplify 折叠为 0；long 站点保留 cast。
        Assert.Contains("(long)", pre.OutputCode);
        Assert.Equal(1, pre.Statistics.PointerDifferences);
    }

    [Fact]
    public void AbsoluteAddress_CastInt_Rejected_UF021()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static int M(byte* p) {
        return (int)p;
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Equal(1, pre.Statistics.Uf021Rejections);
    }

    [Fact]
    public void AbsoluteAddress_CastIntPtr_Rejected_UF021()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static System.IntPtr M(byte* p) {
        return (IntPtr)p;
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
    }

    [Fact]
    public void AbsoluteAddress_CastLong_Rejected_UF021()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static long M(byte* p) {
        return (long)p;
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
    }

    [Fact]
    public void IntegerToPointerCast_Rejected_UF021()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static int M(int x) {
        return (int)(byte*)x;
    }
}");

        Assert.False(pre.Changed);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
    }

    // ---- helpers ----

    private static UnsafeFfmResult Preprocess(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "c06m3b-cast",
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
