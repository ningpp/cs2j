using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.IO;

namespace CSharpToJava.Tests;

/// <summary>
/// Console.Error/Console.Out resolve Write/WriteLine to System.IO.TextWriter, whose
/// (string format, args…) overloads map to print/println — Java PrintStream/PrintWriter
/// have no (String, Object) form (mvn 现场: QUT.ShiftReduceParser displayStack /
/// displayProduction). They must bridge to compat TextWriterHelper.writeFormat /
/// writeFormatLine (C# composite formatting), while plain Write(object)/WriteLine()
/// keep the direct System.err/System.out calls.
/// </summary>
public class ConsoleErrorTextWriterFormatTests
{
    [Fact]
    public void ErrorWrite_FormatOverload_UsesTextWriterHelper()
    {
        var result = Convert("""
using System;
class C
{
    void Log(int ruleNumber, string text)
    {
        Console.Error.Write(" {0}", ruleNumber);
        Console.Error.WriteLine("-> {0}", text);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("import io.github.ningpp.compat.TextWriterHelper;", code, StringComparison.Ordinal);
        Assert.Contains(
            "TextWriterHelper.writeFormat(System.err, \" {0}\", ruleNumber)",
            code, StringComparison.Ordinal);
        Assert.Contains(
            "TextWriterHelper.writeFormatLine(System.err, \"-> {0}\", text)",
            code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.err.print(\" {0}\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.err.println(\"-> {0}\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OutWrite_FormatOverload_UsesTextWriterHelper()
    {
        var result = Convert("""
using System;
class C
{
    void Log(int value)
    {
        Console.Out.Write("v={0}", value);
        Console.Out.WriteLine("v={0}", value);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains(
            "TextWriterHelper.writeFormat(System.out, \"v={0}\", value)",
            code, StringComparison.Ordinal);
        Assert.Contains(
            "TextWriterHelper.writeFormatLine(System.out, \"v={0}\", value)",
            code, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainWrites_StayDirectOnTheStream()
    {
        // 单实参 Write(object)/无参 WriteLine() 没有复合格式语义，必须保持原映射，
        // 不能被格式桥接（会改写成 writeFormat(…, ) 之类的伪调用）。
        var result = Convert("""
using System;
class C
{
    void Log(object value)
    {
        Console.Error.Write(value);
        Console.Error.WriteLine();
        Console.Error.WriteLine(value);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("System.err.print(value)", code, StringComparison.Ordinal);
        Assert.Contains("System.err.println()", code, StringComparison.Ordinal);
        Assert.Contains("System.err.println(value)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TextWriterHelper", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatTextWriterHelper_ProvidesFormatWriters()
    {
        var compatPath = Path.Combine(
            FindRepoRoot(),
            "java", "csharptojava-compat", "src", "main", "java",
            "io", "github", "ningpp", "compat", "TextWriterHelper.java");
        var source = File.ReadAllText(compatPath);

        Assert.Contains("writeFormat(PrintStream stream, String format, Object... args)", source, StringComparison.Ordinal);
        Assert.Contains("writeFormatLine(PrintStream stream, String format, Object... args)", source, StringComparison.Ordinal);
        Assert.Contains("writeFormat(PrintWriter writer, String format, Object... args)", source, StringComparison.Ordinal);
        Assert.Contains("writeFormatLine(PrintWriter writer, String format, Object... args)", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root.");
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
