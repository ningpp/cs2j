using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# uint ++/-- operations mask wrap-around values while
/// preserving the Java int storage type used for uint.
/// </summary>
public class UIntIncrementDecrementTests
{
    [Fact]
    public void UInt_PostfixIncrement_InExpressionContext_CastsMaskedAssignmentBackToInt()
    {
        var result = Convert(@"
class Node {
    public Node(uint id) {}
}

class Test {
    uint nextNodeId;

    Node Add() {
        var n = new Node(this.nextNodeId++);
        return n;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains(
            "this.nextNodeId = (int)((this.nextNodeId + 1) & 0xFFFFFFFFL);",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "this.nextNodeId = ((this.nextNodeId + 1) & 0xFFFFFFFFL);",
            result.GeneratedCode,
            StringComparison.Ordinal);
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
