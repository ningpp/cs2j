using System.IO;
using System.Linq;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M3b：<c>*p++</c> 出现在短路（<c>||</c>/<c>&amp;&amp;</c>）表达式中时必须
/// 保持短路语义。归一策略（设计 §9 M3）：自增不被提取为前置语句，而是原地
/// 变为索引局部上的 <c>_p_index++</c>，故条件的短路顺序天然保持；早先转换器
/// 把自增提前成无条件语句的缺陷不再可能出现。断言以归一结果（语法）为主，
/// 并跑一次 Convert 确认端到端产物保持同一条件形态。
/// </summary>
public class PointerPostIncrementShortCircuitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    [Fact]
    public void PostIncrement_InsideLogicalOr_StaysInCondition_NotHoisted()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static int M(byte* p, bool flag) {
        if (flag || *p++ < 0xA0) return 1;
        return 0;
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        var code = pre.OutputCode;
        var lines = code.Replace("\r\n", "\n").Split('\n');
        var ifLine = System.Array.FindIndex(lines, l => l.Contains("if ("));
        Assert.True(ifLine >= 0, "expected an if statement in:\n" + code);

        // 自增只在 if 条件行出现：if 之前只有索引局部声明（无自增语句）。
        Assert.Contains("_p_index++", lines[ifLine]);
        Assert.Contains("flag ||", lines[ifLine]);
        for (var i = 0; i < ifLine; i++)
        {
            Assert.DoesNotContain("_p_index++", lines[i]);
            Assert.DoesNotContain("++_p_index", lines[i]);
        }

        Assert.Equal(1, pre.Statistics.PointerIncrements);

        var converted = Convert(code);
        Assert.True(converted.Success,
            "expected conversion to succeed; diagnostics:\n" +
            string.Join("\n", converted.Diagnostics.Select(d => d.ToString())));
        var javaLines = converted.GeneratedCode.Replace("\r\n", "\n").Split('\n');
        var javaIfLine = System.Array.FindIndex(javaLines, l => l.Contains("if ("));
        Assert.True(javaIfLine >= 0);
        Assert.Contains("flag ||", javaLines[javaIfLine]);
        Assert.Contains("_p_index++", javaLines[javaIfLine]);
    }

    [Fact]
    public void PostIncrement_InsideLogicalAnd_StaysInWhileCondition()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static int M(byte* p, bool flag) {
        while (flag && *p++ != 0) { flag = false; }
        return 0;
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        var lines = pre.OutputCode.Replace("\r\n", "\n").Split('\n');
        var whileLine = System.Array.FindIndex(lines, l => l.Contains("while ("));
        Assert.True(whileLine >= 0);
        Assert.Contains("flag &&", lines[whileLine]);
        Assert.Contains("_p_index++", lines[whileLine]);
        for (var i = 0; i < whileLine; i++)
        {
            Assert.DoesNotContain("_p_index++", lines[i]);
            Assert.DoesNotContain("++_p_index", lines[i]);
        }

        Assert.Equal(1, pre.Statistics.PointerIncrements);
    }

    [Fact]
    public void PreIncrement_InsideLogicalOr_NormalizesInPlace()
    {
        var pre = Preprocess(@"
unsafe class Test {
    static int M(byte* p, bool flag) {
        if (flag || *++p >= 1) return 1;
        return 0;
    }
}");

        Assert.True(pre.Changed,
            "expected normalization to succeed; diagnostics:\n" +
            string.Join("\n", pre.Diagnostics.Select(d => d.ToString())));

        var lines = pre.OutputCode.Replace("\r\n", "\n").Split('\n');
        var ifLine = System.Array.FindIndex(lines, l => l.Contains("if ("));
        Assert.True(ifLine >= 0);
        Assert.Contains("++_p_index", lines[ifLine]);
        Assert.Equal(1, pre.Statistics.PointerIncrements);
    }

    // ---- helpers ----

    private static UnsafeFfmResult Preprocess(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "c06m3b-shortcircuit",
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
