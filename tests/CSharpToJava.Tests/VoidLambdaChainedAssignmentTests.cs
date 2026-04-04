using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that chained property assignments inside void lambdas (Action/Consumer)
/// do not emit an invalid `return` statement in Java.
/// C# allows expression-bodied lambdas where the value is discarded for Action delegates,
/// but Java Consumer lambdas cannot have return values.
/// </summary>
public class VoidLambdaChainedAssignmentTests
{
    [Fact]
    public void ActionLambda_ChainedPropertyAssignment_NoReturn()
    {
        var result = Convert(@"
using System;

class Cluster
{
    public bool IsInSolver { get; set; }
    public bool GenerateWorker() => true;
}

class Test
{
    void Process(Action<Cluster> action) { }
    void M()
    {
        Process(cluster => cluster.IsInSolver = cluster.GenerateWorker());
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The void lambda should NOT contain a return of the chain value
        Assert.DoesNotContain("return _chainVal", result.GeneratedCode);
        // Should still contain the setter call
        Assert.Contains("setIsInSolver", result.GeneratedCode);
    }

    [Fact]
    public void FuncLambda_ChainedPropertyAssignment_HasReturn()
    {
        var result = Convert(@"
using System;

class Item
{
    public int Value { get; set; }
    public int Compute() => 42;
}

class Test
{
    void Process(Func<Item, int> func) { }
    void M()
    {
        Process(item => item.Value = item.Compute());
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The non-void lambda SHOULD contain a return statement
        Assert.Contains("return", result.GeneratedCode);
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
