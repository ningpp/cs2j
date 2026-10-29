using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Diagnostics.Debug/Trace WriteLine map to out.println, but the
/// composite-format overload takes (string format, params object[] args);
/// Java PrintStream has no (String, Object[]) form (mvn 现场: AGL
/// tools/ArgsParser getDoubleOptionValue / getIntOptionValue
/// "for option '{0}' cannot parse value of '{1}'"). It must bridge to compat
/// TextWriterHelper.writeFormatLine on System.out, while plain
/// WriteLine(object) keeps the direct stream call.
/// </summary>
public class DebugTraceFormatWriteTests
{
    [Fact]
    public void DebugWriteLine_FormatWithArrayArgs_UsesTextWriterHelper()
    {
        var result = Convert("""
using System.Diagnostics;
class C
{
    bool Parse(string s, object value)
    {
        Debug.WriteLine("for option '{0}' cannot parse value of '{1}'", new object[] { s, value });
        return false;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("import io.github.ningpp.compat.TextWriterHelper;", code, StringComparison.Ordinal);
        Assert.Contains(
            "TextWriterHelper.writeFormatLine(System.out, \"for option '{0}' cannot parse value of '{1}'\", new Object[]{ s, value })",
            code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.out.println(\"for option", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugWriteLine_FormatWithSingleArg_UsesTextWriterHelper()
    {
        var result = Convert("""
using System.Diagnostics;
class C
{
    void Log(int value)
    {
        Debug.WriteLine("v={0}", value);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains(
            "TextWriterHelper.writeFormatLine(System.out, \"v={0}\", value)",
            code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.out.println(\"v={0}\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainDebugWriteLine_StaysDirectOnTheStream()
    {
        // 单实参 WriteLine(object) 没有复合格式语义，必须保持原映射。
        var result = Convert("""
using System.Diagnostics;
class C
{
    void Log(object value)
    {
        Debug.WriteLine(value);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("System.out.println(value)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TextWriterHelper", code, StringComparison.Ordinal);
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
}
