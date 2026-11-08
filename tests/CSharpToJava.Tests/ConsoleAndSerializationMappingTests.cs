using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    [Fact]
    public void ExpressionStatementContext_ChainedCallResult_IsDiscarded()
    {
        // dotnet-text-json JsonDocumentTests: `await DeserializeAsync<JsonDocument>(...).join();`
        // — restoring the erased generic return with a cast around a discarded
        // expression is not a valid Java statement ("不是语句").
        Assert.True(StatementContextOfInvocation(
            "class C { void M() { JsonSerializer.DeserializeAsync<JsonDocument>(s).Join(); } }"));
    }

    [Fact]
    public void ExpressionStatementContext_TaskResultProperty_IsDiscarded()
    {
        // The await lowering renders `task.Result` as `task.join()`; its restored
        // cast must also be dropped when the result is discarded.
        Assert.True(StatementContextOfNode(
            "class C { void M() { JsonSerializer.DeserializeAsync<JsonDocument>(s).Result; } }",
            node => node is IdentifierNameSyntax { Identifier.Text: "Result" }));
    }

    [Fact]
    public void ExpressionStatementContext_ValueContext_KeepsCast()
    {
        // In a value position (return/assignment/argument) the cast is required,
        // so the helper must report false.
        Assert.False(StatementContextOfNode(
            "class C { int M() { return JsonSerializer.Deserialize<JsonDocument>(s).Value; } }",
            node => node is GenericNameSyntax { Identifier.Text: "Deserialize" }));
    }

    private static bool StatementContextOfInvocation(string source)
        => StatementContextOfNode(source,
            node => node is InvocationExpressionSyntax
                { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "DeserializeAsync" } });

    private static bool StatementContextOfNode(string source, Func<SyntaxNode, bool> predicate)
    {
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source).GetRoot();
        var node = root.DescendantNodes().First(predicate);
        return CSharpToJava.Core.Transformers.Expression.InvocationExpressionTransformer
            .IsExpressionStatementContext(node);
    }

    /// <summary>
    /// Java has no IL emit, so the STJ member-accessor chosen for the NETCOREAPP
    /// branch must be the reflection-backed one; otherwise every
    /// JsonSerializerOptions.GetMemberAccessorStrategy call reaches
    /// ILGenerator.Emit and throws PlatformNotSupportedException.
    /// </summary>
    [Fact]
    public async Task ReflectionEmitMemberAccessor_MapsToReflectionFallback()
    {
        // 真实的 STJ 工程里这些类型分属不同文件：语法回退路径拿不到跨文件符号，
        // 必须靠 sourceTypeRedirects 的简单名索引命中。
        var files = new[]
        {
            new SourceFile { FilePath = "MemberAccessor.cs", Content = """
namespace dotnet.text.json.Serialization
{
    internal abstract class MemberAccessor { }
}
""" },
            new SourceFile { FilePath = "ReflectionMemberAccessor.cs", Content = """
namespace dotnet.text.json.Serialization
{
    internal sealed class ReflectionMemberAccessor : MemberAccessor { }
}
""" },
            new SourceFile { FilePath = "ReflectionEmitMemberAccessor.cs", Content = """
namespace dotnet.text.json.Serialization
{
    internal sealed class ReflectionEmitMemberAccessor : MemberAccessor { }
}
""" },
            new SourceFile { FilePath = "Use.cs", Content = """
namespace dotnet.text.json.Serialization
{
    internal sealed class Use
    {
        private MemberAccessor _strategy;
        public MemberAccessor Strategy()
        {
            if (_strategy == null)
            {
                _strategy = new ReflectionEmitMemberAccessor();
            }
            return _strategy;
        }
    }
}
""" },
        };
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(files, projectName: "Demo");
        var code = string.Join("\n", results.Select(r => r.GeneratedCode));
        Assert.All(results, r => Assert.True(r.Success, string.Join("\n", r.Diagnostics)));
        Assert.Contains("new ReflectionMemberAccessor()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new ReflectionEmitMemberAccessor()", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 公共泛型 API 的跨工程调用（真实链 JsonSerializer.Deserialize&lt;T&gt;）无法由 GD-MTOK
    /// 改写消费方调用点，转换器改走 C# 自带的 System.Type 令牌重载：普通类型 X.class、
    /// 数组 CSharpArray.arrayType、闭合泛型 closedCallSiteGenericType(parameterized(...))。
    /// </summary>
    [Fact]
    public void GenericApiCall_PassesRuntimeTypeTokenToTypeOverload()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;

class Options { }
class Item { }

static class Api
{
    public static T Deserialize<T>(string json, Options options = null) => default(T);
    public static object Deserialize(string json, Type returnType, Options options = null) => null;
}

class Use
{
    Item One(string json) => Api.Deserialize<Item>(json);
    Item Named(string j) => Api.Deserialize<Item>(j, options: null);
    List<int> Items(string json) => Api.Deserialize<List<int>>(json);
    Item[] ArrayOfItems(string json) => Api.Deserialize<Item[]>(json);
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        // 省略的可选实参在预处理后是命名实参（options: null），令牌仍要插在形参位。
        Assert.Contains("Api.deserialize(json, Item.class, null)", code, StringComparison.Ordinal);
        Assert.Contains("Api.deserialize(j, Item.class, null)", code, StringComparison.Ordinal);
        Assert.Contains(
            "TypeHelper.closedCallSiteGenericType(TypeHelper.parameterized(CSharpList.class, Integer.class))",
            code, StringComparison.Ordinal);
        Assert.Contains("CSharpArray.arrayType(Item.class)", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 令牌重载返回 Object；被丢弃的语句位置不能再加 cast（javac: 不是语句）。
    /// </summary>
    [Fact]
    public void TokenOverloadCall_InStatementPosition_HasNoCast()
    {
        var result = Convert("""
using System;

static class Check
{
    public static T Throws<T>(Action action) => default(T);
    public static object Throws(Type type, Action action) => null;
}

class Use
{
    void M()
    {
        Check.Throws<Exception>(() => { });
    }

    void N() => Check.Throws<Exception>(() => { });
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("Check.throwsValue(RuntimeException.class, () ->", code, StringComparison.Ordinal);
        Assert.DoesNotContain("((RuntimeException) Check.throwsValue", code, StringComparison.Ordinal);
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
