using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 回归（modulecore-tests javac）：
/// ① `new Func&lt;T&gt;(delegateVar)` 包一层已有委托变量时必须 SAM 转换为
///    lambda（`() -> delegateVar.invoke()`），不能发射 `new Supplier&lt;T&gt;(var)`
///    （Supplier 抽象不可实例化）。
/// ② 集合接收器上的 `.Single()` 应映射取首元素（get(0)），不得映射为流方法
///    findAny()（List 没有 findAny）。
/// </summary>
public class DelegateVarWrapAndSingleTests
{
    [Fact]
    public void DelegateVariableWrap_EmitsSamLambda()
    {
        var source = @"
delegate CTestModule ModuleGenerator();
class CTestModule { }
class XmlTestsAttribute
{
    private string _methodName;
    public XmlTestsAttribute(string methodName) { _methodName = methodName; }

    public static ModuleGenerator GetMethod(System.Reflection.MethodInfo mi) => null;

    public System.Func<CTestModule> GetGenerator()
    {
        ModuleGenerator moduleGenerator = GetMethod(null);
        return new System.Func<CTestModule>(moduleGenerator);
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("new Supplier<CTestModule>(", result.GeneratedCode, StringComparison.Ordinal);
        // SAM 方法名与 DelegateTransformer 接口发射同约定：自定义委托
        // ModuleGenerator（Supplier 形）→ get()，不再是硬编码 invoke()。
        Assert.Contains(".get()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".invoke()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionSingle_MapsToFirstElement()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
class Use
{
    public object GetData(List<object> args)
    {
        return args.Single();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain(".findAny()", result.GeneratedCode, StringComparison.Ordinal);
        // 取首元素形态（get(0)）。
        Assert.Contains("get(0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Probe.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = System.IO.Path.Combine(
                    System.AppContext.BaseDirectory, "config", "TypeMappings.json"),
            },
        });
    }
}
