namespace CSharpToTypeScript.Tests;

/// <summary>
/// Iteration 25 regression from MSAGL Core/Layout/Node.cs ToString():
/// base.ToString() (binding to System.Object.ToString) was routed through
/// ObjectHelper.objectToString(super) — `super` is not a value expression,
/// producing TS1034 "'super' must be followed by an argument list or member
/// access". The base receiver must instead emit a direct super.toString() call.
/// </summary>
public class TsBaseToStringTests : TsConversionTestBase
{
    [Fact]
    public void BaseToString_BindingToObject_EmitsSuperToString()
    {
        var result = Convert("""
class Base { }
class Foo : Base
{
    public override string ToString()
    {
        return base.ToString();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("super.toString()", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHelper", result.GeneratedCode);
        Assert.DoesNotContain("super)", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
