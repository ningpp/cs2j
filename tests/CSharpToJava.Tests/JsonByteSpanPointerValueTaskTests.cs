using System.IO;
using System.Linq;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M3b：byte/span/pointer 转换簇归一（设计 §9 M3）。断言以语法/归一结果
/// 为主，不依赖 JDK：
/// <list type="bullet">
/// <item>可证数组源字节访问：<c>fixed(byte* p = data)</c> 归一为数组局部+索引；</item>
/// <item><c>fixed(byte* p = &amp;MemoryMarshal.GetReference(span))</c>：span 源自
/// 数组（直赋/<c>AsSpan</c>/<c>new Span</c>）则归一，参数 span 等不可证源
/// → UF020；</item>
/// <item><c>AsSpan(start)</c> 起始偏移保留到索引局部。</item>
/// </list>
/// </summary>
public class JsonByteSpanPointerValueTaskTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    [Fact]
    public void FixedOverByteArray_ByteAccess_Normalizes()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static byte M(byte[] data) {
        fixed (byte* p = data) {
            return *p;
        }
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        var code = pre.OutputCode;
        Assert.Contains("byte[] _p_array = data;", code);
        Assert.Contains("int p = 0;", code);
        Assert.Contains("_p_array[p]", code);

        var converted = Convert(code);
        Assert.True(converted.Success,
            "expected conversion to succeed; diagnostics:\n" +
            string.Join("\n", converted.Diagnostics.Select(d => d.ToString())));
        Assert.DoesNotContain("MemorySegment", converted.GeneratedCode);
    }

    [Fact]
    public void GetReference_SpanLocalFromArray_Normalizes()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static byte M(byte[] data) {
        System.Span<byte> span = data;
        fixed (byte* p = &System.Runtime.InteropServices.MemoryMarshal.GetReference(span)) {
            return *p;
        }
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        var code = pre.OutputCode;
        Assert.Contains("byte[] _p_array = data;", code);
        Assert.Contains("int p = 0;", code);
        Assert.Contains("_p_array[p]", code);
    }

    [Fact]
    public void GetReference_InlineNewSpanFromArray_Normalizes()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static byte M(byte[] data) {
        fixed (byte* p = &System.Runtime.InteropServices.MemoryMarshal.GetReference(new System.Span<byte>(data))) {
            return *p;
        }
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        Assert.Contains("_p_array = data", pre.OutputCode);
    }

    [Fact]
    public void GetReference_AsSpanWithStart_PreservesStartOffset()
    {
        var pre = Preprocess(@"
using System;
unsafe class Test {
    static byte M(byte[] data) {
        fixed (byte* p = &System.Runtime.InteropServices.MemoryMarshal.GetReference(data.AsSpan(2))) {
            return *p;
        }
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        var code = pre.OutputCode;
        Assert.Contains("byte[] _p_array = data;", code);
        Assert.Contains("int p = 2;", code);
    }

    [Fact]
    public void GetReference_ParameterSpan_Unprovable_Rejected_UF020()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static byte M(System.Span<byte> span) {
        fixed (byte* p = &System.Runtime.InteropServices.MemoryMarshal.GetReference(span)) {
            return *p;
        }
    }
}");

        Assert.False(pre.Changed);
        Assert.NotEmpty(pre.Diagnostics);
        Assert.All(pre.Diagnostics, d => Assert.Equal(UnsafeFfmDiagnosticCode.UF020, d.Code));
        Assert.Contains(pre.Diagnostics, d => d.Message.Contains("GetReference"));
    }

    // ---- helpers ----

    private static UnsafeFfmResult Preprocess(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "c06m3b-jsonspan",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new UnsafeFfmPreprocessor(new UnsafeFfmOptions())
            .RewriteFile(sourceCode, model);
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
