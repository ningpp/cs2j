using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class LogicalNotOnIntIrTests
{

    [Fact]
    public void LogicalNot_OnBool_KeepsExclamation()
    {
        var result = Convert(@"
class Test
{
    bool M(bool flag)
    {
        return !flag;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("!flag", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalNot_OnBoolMethodCall_KeepsExclamation()
    {
        var result = Convert(@"
class Test
{
    bool RaiseInteractiveAssert(Exception ex)
    {
        return ex != null;
    }

    void Handle(Exception ex)
    {
        if (!RaiseInteractiveAssert(ex))
        {
            return;
        }
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("== 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("!raiseInteractiveAssert(ex)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalNot_OnBoolProperty_KeepsExclamation()
    {
        var result = Convert(@"
class Node
{
    internal bool ChildrenHaveBeenPushed { get; set; }
}

class Test
{
    void M(Node node)
    {
        if (!node.ChildrenHaveBeenPushed)
        {
            return;
        }
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("!node.getChildrenHaveBeenPushed()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getChildrenHaveBeenPushed() == 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectLogicalNot_OnBoolProperty_KeepsExclamation()
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        });
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Test.cs",
                Content = @"
class Node
{
    internal bool ChildrenHaveBeenPushed { get; set; }
}

class Test
{
    void M(Node node)
    {
        if (!node.ChildrenHaveBeenPushed)
        {
            return;
        }
    }
}",
            },
        });

        var result = Assert.Single(results, r => r.FileName == "Test.java");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("!node.getChildrenHaveBeenPushed()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getChildrenHaveBeenPushed() == 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
