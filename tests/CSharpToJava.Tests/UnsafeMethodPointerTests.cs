using System.IO;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M3a 起，指针方法走「先经 <see cref="UnsafeFfmPreprocessor"/> 归一
/// （EnablePointer，R5 签名 + R6 表达式）再 Convert」的端到端路径：
/// 12 基元白名单元素指针形参在 C# 侧已变为 <c>T[]</c>，转换器产出 Java
/// 数组签名（不再注入 MemorySegment）；<c>void*</c> 在预处理面被
/// UF021 拒绝。另保留一个「不预处理」的最小直连用例，锁定转换器
/// 直连 MemorySegment 的安全网不被弱化（设计 §7.4）。
/// </summary>
public class UnsafeMethodPointerTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    [Fact]
    public void UnsafeMethod_BytePointerParam_MapsToJavaByteArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(byte* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("byte[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_CharPointerParam_MapsToJavaCharArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(char* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("char[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerParam_MapsToJavaIntArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(int* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("int[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_LongPointerParam_MapsToJavaLongArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(long* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("long[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_FloatPointerParam_MapsToJavaFloatArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(float* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("float[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_DoublePointerParam_MapsToJavaDoubleArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(double* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("double[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_SBytePointerParam_MapsToJavaIntArray()
    {
        // 归一产物 C# 形态为 sbyte[] p（R5 正确）。转换器对 sbyte[] 的全局
        // Java 映射是 int[]（非 byte[]）：System.Text.Json 中 byte[] 序列化为
        // Base64 字符串、sbyte[] 序列化为数字数组，专门提交 ed6774c3
        // （converter+compat: map sbyte[] to int[] instead of byte[]）确立
        // 此规则；new sbyte[N] 同样发 new int[N]。故端到端产物断言 int[] p。
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(sbyte* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("int[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_ShortPointerParam_MapsToJavaShortArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(short* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("short[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BoolPointerParam_MapsToJavaBooleanArray()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(bool* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("boolean[] p", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_MultiplePointerParams_AllMapToJavaArrays()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void Decode(char* pChars, char* pCharsEndPos, byte* pBytes, byte* pBytesEndPos) { }
}");
        Assert.True(result.Success);
        Assert.Contains("char[] pChars", result.GeneratedCode);
        Assert.Contains("char[] pCharsEndPos", result.GeneratedCode);
        Assert.Contains("byte[] pBytes", result.GeneratedCode);
        Assert.Contains("byte[] pBytesEndPos", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_MixedPointerAndNonPointerParams()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(byte* p, int count, byte* q) { }
}");
        Assert.True(result.Success);
        Assert.Contains("byte[] p", result.GeneratedCode);
        Assert.Contains("int count", result.GeneratedCode);
        Assert.Contains("byte[] q", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_PointerParamWithOutParam()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(byte* pBytes, out int bytesDecoded) { bytesDecoded = 0; }
}");
        Assert.True(result.Success);
        Assert.Contains("byte[] pBytes", result.GeneratedCode);
        Assert.Contains("IntHolder bytesDecoded", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_PointerParamWithRefParam()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(char* pChars, ref int count) { }
}");
        Assert.True(result.Success);
        Assert.Contains("char[] pChars", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_NoPointerParams_NoScopeInjection()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(int x) { }
}");
        Assert.True(result.Success);
        Assert.Contains("int x", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_UnsafeModifierStripped()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    void M(byte* p) { }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("unsafe", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_DecodeLikeMethod()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    private unsafe void Decode(char* pChars, char* pCharsEndPos,
                               byte* pBytes, byte* pBytesEndPos,
                               out int charsDecoded, out int bytesDecoded)
    {
        charsDecoded = 0;
        bytesDecoded = 0;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("char[] pChars", result.GeneratedCode);
        Assert.Contains("char[] pCharsEndPos", result.GeneratedCode);
        Assert.Contains("byte[] pBytes", result.GeneratedCode);
        Assert.Contains("byte[] pBytesEndPos", result.GeneratedCode);
        Assert.Contains("IntHolder charsDecoded", result.GeneratedCode);
        Assert.Contains("IntHolder bytesDecoded", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_VoidPointerParam_PreprocessingRejects_UF021()
    {
        var pre = Preprocess(@"
unsafe class Test {
    void M(void* p) { }
}");

        // void* 不在托管数组白名单：Changed=false、原文保留、UF021 显式拒绝。
        Assert.False(pre.Changed);
        Assert.True(pre.HasErrors);
        Assert.Contains(pre.Diagnostics, d => d.Code == UnsafeFfmDiagnosticCode.UF021);
        Assert.Equal(1, pre.Statistics.Uf021Rejections);
    }

    [Fact]
    public void UnsafeMethod_StaticUnsafeMethod()
    {
        var result = PreprocessAndConvert(@"
unsafe class Test {
    static unsafe void M(byte* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("static", result.GeneratedCode);
        Assert.Contains("byte[] p", result.GeneratedCode);
        Assert.DoesNotContain("unsafe", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void M3a_DirectConversion_BytePointer_StillMapsToMemorySegment_SafetyNetNotWeakened()
    {
        // 安全网：不跑预处理时，转换器直连路径必须仍把指针形参映射为
        // MemorySegment（fail-safe 面不弱化），这是 M4 interop facade 的基座。
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) { }
}");

        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    // ---- helpers ----

    private static UnsafeFfmResult Preprocess(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "uffm-pointer-e2e",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new UnsafeFfmPreprocessor(new UnsafeFfmOptions())
            .RewriteFile(sourceCode, model);
    }

    private static ConversionResult PreprocessAndConvert(string sourceCode)
    {
        var pre = Preprocess(sourceCode);
        Assert.True(pre.Changed,
            "expected unsafe-ffm preprocessing to change the source, but it did not; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));
        return Convert(pre.OutputCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
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
