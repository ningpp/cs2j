using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 回归：嵌套 enum / interface 不得发射 `static enum` / `static interface`
/// （Java 语法错误——嵌套枚举与接口隐式静态，显式 static 修饰非法）。
/// </summary>
public class NestedEnumStaticModifierTests
{
    private const string Source = @"
namespace MS.Internal.Xml.XPath
{
    internal class Function
    {
        public enum FunctionType
        {
            FuncLast,
            FuncBoolean,
            FuncUserDefined
        }

        internal interface IFunctionArg
        {
            string Name { get; }
        }

        private static class Holder
        {
            public static int Value => 1;
        }
    }
}";

    [Fact]
    public void NestedEnum_IsNotEmittedAsStaticEnum()
    {
        var result = Convert(Source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("static enum", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("static interface", result.GeneratedCode, StringComparison.Ordinal);
        // 枚举与嵌套静态类仍正常发射。
        Assert.Contains("enum FunctionType", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("FuncBoolean", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("static class Holder", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Function.cs",
            Options = new ConversionOptions(),
        });
    }
}
