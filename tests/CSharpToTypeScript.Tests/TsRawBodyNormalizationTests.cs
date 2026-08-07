namespace CSharpToTypeScript.Tests;

/// <summary>
/// M5 / MSAGL: raw method bodies and field initializers (Java-flavor text produced
/// by the shared front-end) are normalized at emission time: Java declarations
/// become let, classic for heads let-ify, float suffixes drop, and Java array
/// creation literals become TS array literals. All cases compile under real tsc.
/// </summary>
public class TsRawBodyNormalizationTests : TsConversionTestBase
{
    [Fact]
    public void Method_Body_Declaration_And_Float_Suffix_Are_Normalized()
    {
        var result = Convert("""
class Sample
{
    double Scale()
    {
        double factor = 1.5f;
        return factor;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("let factor: number = 1.5;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Method_Body_Multi_Variable_Declaration_Is_Normalized()
    {
        var result = Convert("""
class Sample
{
    double MinMax()
    {
        double aSol, bSol;
        aSol = 1;
        bSol = 2;
        return aSol + bSol;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("let aSol: number, bSol: number;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Field_Array_Initializer_Becomes_Array_Literal()
    {
        var result = Convert("""
class Sample
{
    static readonly string[] Colors = new string[] { "red", "green" };
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private static readonly Colors: string[] = [\"red\", \"green\"];", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Field_Multi_Dimensional_Array_Initializer_Becomes_Nested_Literals()
    {
        var result = Convert("""
class Sample
{
    readonly double[][] Points = new double[][] { new double[] { 1, 2 }, new double[] { 3, 4 } };
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private Points: number[][] = [[1, 2], [3, 4]];", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}
