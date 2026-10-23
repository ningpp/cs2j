using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 回归：实例成员访问作调用接收者时（prefixes.Keys.GetEnumerator()），
/// AddStaticReceiverTypeImports 不得把成员名当作类型简单名查映射表——
/// 否则注入同名类型的 import（Keys → dotnet.winforms.swing.Keys，
/// javac: 程序包dotnet.winforms.swing不存在）。
/// </summary>
public class StaticReceiverTypeImportMismatchTests
{
    [Fact]
    public void InstanceMemberAccessReceiver_DoesNotInjectMappedTypeImport()
    {
        var source = @"
using System.Collections.Generic;
class NsMgr
{
    private Dictionary<string, string> prefixes = new Dictionary<string, string>();
    public System.Collections.IEnumerator GetEnumerator()
    {
        return prefixes.Keys.GetEnumerator();
    }
}";
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = source,
            FileName = "NsMgr.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = System.IO.Path.Combine(
                    System.AppContext.BaseDirectory, "config", "TypeMappings.json"),
            },
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("winforms.swing", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getKeys().iterator()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticTypeReceiver_KeepsMappedTypeImport()
    {
        var source = @"
class Use
{
    int M()
    {
        return (int)System.StringComparison.Ordinal;
    }
}";
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = source,
            FileName = "Use.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = System.IO.Path.Combine(
                    System.AppContext.BaseDirectory, "config", "TypeMappings.json"),
            },
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // 静态接收者（类型名）的映射导入行为保持不变。
        Assert.Contains("Ordinal", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Probe.cs",
            Options = new ConversionOptions(),
        });
    }
}
