using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class ConsoleAndSerializationMappingTests
{

    [Fact]
    public void ExceptionSerializationConstructor_IsOmitted()
    {
        // A .NET serialization constructor (SerializationInfo, StreamingContext) is
        // meaningless in Java: java.io.Serializable does not use it, and a base
        // forwarding super(info, context) cannot compile because the mapped base
        // (Exception -> RuntimeException) has no (Object, Object) constructor.
        // The constructor must be omitted entirely.
        var result = Convert("""
using System;
using System.Runtime.Serialization;

class AcceptException : Exception
{
    internal AcceptException()
    {
    }

    protected AcceptException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("AcceptException()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SerializationInfo", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("StreamingContext", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("__suppress__", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("super(info, context)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("super(SerializationInfo", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorBaseInitializer_WithNull_IsEmittedAsSuperCall()
    {
        var result = Convert("""
class Base
{
    public Base(object value)
    {
    }
}

class Parser : Base
{
    public Parser() : base(null)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public Parser() {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super(null);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public Parser();", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticAndInstanceParameterlessConstructors_AreBothEmitted()
    {
        var result = Convert("""
abstract class Base
{
    protected Base(object scanner)
    {
    }
}

class Parser : Base
{
    static Parser()
    {
        int x = 0;
    }

    public Parser() : base(null)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("static {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Parser() {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super(null);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleWriteLine_FormatOverload_BridgesThroughStringHelperFormatCs()
    {
        // Console.WriteLine/Write 的复合格式重载 (string format, args…) 映射
        // out.println/out.print 后 arity 失配——Java PrintStream 没有两参 println
        // （mvn 现场：xml-tiny ModuleCore CError.java 三处）。桥接为
        // StringHelper.formatCs(format, args...)（C# 复合格式语义）；
        // Debug/Trace.WriteLine(string, string) 的 category 语义不按形状桥接。
        var result = Convert("""
using System;
class C
{
    void Log(string message, object value)
    {
        Console.WriteLine("ERROR: {0}", message);
        Console.WriteLine("Expected: {0} Actual: {1}", message, value);
        Console.Write("prefix {0}", message);
        Console.WriteLine("plain");
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("System.out.println(StringHelper.formatCs(\"ERROR: {0}\", message))", code, StringComparison.Ordinal);
        Assert.Contains("System.out.println(StringHelper.formatCs(\"Expected: {0} Actual: {1}\", message, value))", code, StringComparison.Ordinal);
        Assert.Contains("System.out.print(StringHelper.formatCs(\"prefix {0}\", message))", code, StringComparison.Ordinal);
        Assert.Contains("System.out.println(\"plain\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionMemberMappings_UseCompatHelpers()
    {
        // System.Exception 映射为 RuntimeException 后，InnerException/StackTrace/Source
        // 若沿用 java.lang Throwable 成员（getCause/getStackTrace）或直译 getSource，
        // 类型与成员均不匹配（getCause 返回 Throwable 无法赋回 RuntimeException、
        // getSource 不存在、getStackTrace 返回数组而非 string）——须桥接
        // ExceptionCompat 静态助手（mvn 现场：dotnet-xml-modulecore-tests CTestBase）。
        var result = Convert("""
using System;
class C
{
    string Describe(Exception e)
    {
        if (e.InnerException != null)
            e = e.InnerException;
        string inner = e.InnerException == null ? "" : e.InnerException.Message;
        return e.Source + "|" + e.StackTrace + "|" + inner;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("import io.github.ningpp.compat.ExceptionCompat;", code, StringComparison.Ordinal);
        Assert.Contains("ExceptionCompat.getInnerException(e)", code, StringComparison.Ordinal);
        Assert.Contains("ExceptionCompat.getStackTrace(", code, StringComparison.Ordinal);
        Assert.Contains("ExceptionCompat.getSource(", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".getCause()", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = CreateOptions(),
        });
    }

    private static ConversionOptions CreateOptions()
        => new()
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
}
