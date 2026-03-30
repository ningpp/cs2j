using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #8: 属性赋值表达式作为子表达式使用时，错误地转换为 void 返回的 setter 调用.
///
/// Root cause: When a C# property assignment expression (e.g., cone.LeftSide = new ConeLeftSide(cone))
/// is used as a sub-expression (method argument, return value, etc.), the converter was generating
/// a void Java setter call (e.g., cone.setLeftSide(...)) which cannot be used as a value expression.
///
/// Fix: When a property assignment is used as a value (not a standalone ExpressionStatement),
/// hoist the setter call to a pre-statement and return a temp variable holding the value.
/// </summary>
public class PropertyAssignmentAsSubExpressionTests
{
    /// <summary>
    /// Issue #8 Case 1: property assignment used as a method argument.
    /// C#: InsertToTree(leftConeSides, cone.LeftSide = new ConeLeftSide(cone))
    /// Java should NOT be: insertToTree(leftConeSides, cone.setLeftSide(...))  // void arg — invalid
    /// Java should BE:    var _chainVal0 = new ConeLeftSide(cone); cone.setLeftSide(_chainVal0);
    ///                    insertToTree(leftConeSides, _chainVal0)
    /// </summary>
    [Fact]
    public void PropertyAssignment_UsedAsMethodArgument_HoistsSetterToPreStatement()
    {
        var result = Convert(@"
class ConeLeftSide { public ConeLeftSide(Cone c) {} }
class RBNode {}
class Cone
{
    public ConeLeftSide LeftSide { get; set; }
    RBNode[] leftConeSides;
    RBNode InsertToTree(RBNode[] sides, ConeLeftSide side) { return null; }
    void Test(Cone cone)
    {
        RBNode leftNode = InsertToTree(leftConeSides, cone.LeftSide = new ConeLeftSide(cone));
    }
}");

        Assert.True(result.Success);
        var code = result.GeneratedCode;

        // The setter call must NOT appear as a method argument (would be void)
        Assert.DoesNotContain("insertToTree(leftConeSides, cone.setLeftSide(", code, StringComparison.Ordinal);
        // The setter call must be a pre-statement
        Assert.Contains("cone.setLeftSide(", code, StringComparison.Ordinal);
        // A temp variable must be used as the argument
        Assert.Contains("insertToTree(leftConeSides,", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #8 Case 2: property assignment used in a return statement.
    /// C#: return boneEdge.CrossedCdtEdges = ThreadBoneEdgeThroughCdt(boneEdge);
    /// Java should NOT be: return boneEdge.setCrossedCdtEdges(...)  // return void — invalid
    /// Java should BE:    var _chainVal0 = threadBoneEdgeThroughCdt(boneEdge);
    ///                    boneEdge.setCrossedCdtEdges(_chainVal0);
    ///                    return _chainVal0;
    /// </summary>
    [Fact]
    public void PropertyAssignment_UsedInReturnStatement_HoistsSetterToPreStatement()
    {
        var result = Convert(@"
using System.Collections.Generic;
class SdBoneEdge
{
    public IEnumerable<object> CrossedCdtEdges { get; set; }
}
class Sample
{
    IEnumerable<object> ThreadBoneEdgeThroughCdt(SdBoneEdge e) { return null; }
    IEnumerable<object> CrossedCdtEdgesOfBoneEdge(SdBoneEdge boneEdge)
    {
        if (boneEdge.CrossedCdtEdges != null)
            return boneEdge.CrossedCdtEdges;
        return boneEdge.CrossedCdtEdges = ThreadBoneEdgeThroughCdt(boneEdge);
    }
}");

        Assert.True(result.Success);
        var code = result.GeneratedCode;

        // The setter call must NOT appear in a return statement (would return void)
        Assert.DoesNotContain("return boneEdge.setCrossedCdtEdges(", code, StringComparison.Ordinal);
        // The setter call must be a pre-statement
        Assert.Contains("boneEdge.setCrossedCdtEdges(", code, StringComparison.Ordinal);
        // There must be a return of a temp variable
        Assert.Contains("return ", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Standalone property assignment statement should still generate a simple setter call (no hoisting).
    /// </summary>
    [Fact]
    public void PropertyAssignment_StandaloneStatement_GeneratesSimpleSetterCall()
    {
        var result = Convert(@"
class Container { public int Value { get; set; } }
class Sample
{
    void Test(Container c)
    {
        c.Value = 42;
    }
}");

        Assert.True(result.Success);
        var code = result.GeneratedCode;

        // Standalone assignment → simple setter call, no temp variable needed
        Assert.Contains("c.setValue(42)", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property assignment used as condition in an if statement should be hoisted.
    /// </summary>
    [Fact]
    public void PropertyAssignment_UsedAsIfCondition_HoistsSetterToPreStatement()
    {
        var result = Convert(@"
class Container { public bool Flag { get; set; } }
class Sample
{
    bool Compute() { return true; }
    void Test(Container c)
    {
        if ((c.Flag = Compute()) == true)
        {
            System.Console.WriteLine(""yes"");
        }
    }
}");

        Assert.True(result.Success);
        var code = result.GeneratedCode;

        // The setter must be hoisted out of the condition
        Assert.DoesNotContain("if (c.setFlag(", code, StringComparison.Ordinal);
        Assert.Contains("c.setFlag(", code, StringComparison.Ordinal);
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
