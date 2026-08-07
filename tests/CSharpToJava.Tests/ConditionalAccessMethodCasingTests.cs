using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a statement-level null-conditional method invocation (obj?.Method())
/// lowercases the method name to match the generated declaration. Reproduces
/// Match.java:[74,45] "找不到符号 方法 Reset()" — the C# '_groupcoll?.Reset();' was
/// lowered to 'if (_groupcoll != null) { _groupcoll.Reset(); }' keeping the C#
/// PascalCase name, while the declaration was emitted as reset(), so the call
/// referenced a non-existent Reset(). The invocation name must be camelCased the
/// same way normal method invocations are.
/// </summary>
public class ConditionalAccessMethodCasingTests
{
    [Fact]
    public void NullConditionalMethodCall_LowercasesMethodName()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
public class Holder
{
    private Worker _worker;
    public void Run() { _worker?.Reset(); }
}
public class Worker
{
    public void Reset() { }
}",
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The declaration is lowercased; the null-conditional call must match it.
        Assert.Contains(".reset()", code);
        Assert.DoesNotContain(".Reset()", code);
    }
}
