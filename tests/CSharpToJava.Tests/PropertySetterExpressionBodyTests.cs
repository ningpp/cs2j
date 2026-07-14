using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that expression-bodied property setters which assign to another property
/// generate valid Java code (not a bare chain variable like `_chainVal1;`).
///
/// Root cause: When a C# setter has an arrow expression body like
///   set => Other.Prop = value;
/// the AssignmentTransformer hoists the property assignment to pre-statements
/// and returns a temp variable name. But PropertyTransformer only uses the
/// return value as the setter body, discarding the pre-statements, resulting
/// in invalid Java like `{ _chainVal1; }`.
/// </summary>
public class PropertySetterExpressionBodyTests
{
    [Fact]
    public void SetterExpressionBody_DelegatesToOtherProperty_NoBareChainVal()
    {
        var result = Convert(@"
class Config
{
    public static int Value { get; set; }
}

class Wrapper
{
    public static int Prop
    {
        get => Config.Value;
        set => Config.Value = value;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;

        // The setter should NOT contain a bare chain variable statement
        Assert.DoesNotContain("_chainVal", code);
        // The setter should delegate to Config.setValue(value)
        Assert.Contains("Config.setValue(value)", code);
    }

    [Fact]
    public void SetterExpressionBody_InstancePropertyDelegates_NoBareChainVal()
    {
        var result = Convert(@"
class Inner
{
    public int Data { get; set; }
}

class Outer
{
    private Inner _inner = new Inner();

    public int Data
    {
        get => _inner.Data;
        set => _inner.Data = value;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;

        // The setter should NOT contain a bare chain variable statement
        Assert.DoesNotContain("_chainVal", code);
        // The setter should delegate to _inner.setData(value)
        Assert.Contains("_inner.setData(value)", code);
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
