namespace CSharpToTypeScript.Tests;

public class TsControlFlowEmissionTests : TsConversionTestBase
{
    [Fact]
    public void While_Loop_Emits_While()
    {
        var result = Convert("""
class Sample
{
    int Count(int n)
    {
        int i = 0;
        while (i < n)
        {
            i = i + 1;
        }
        return i;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("while (i < n)", result.GeneratedCode);
    }

    [Fact]
    public void For_Loop_Emits_For()
    {
        var result = Convert("""
class Sample
{
    int Sum(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i = i + 1)
        {
            total = total + i;
        }
        return total;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("for (let i: number = 0; i < n; i = i + 1)", result.GeneratedCode);
    }

    [Fact]
    public void Do_While_Emits_DoWhile()
    {
        var result = Convert("""
class Sample
{
    int Run()
    {
        int x = 0;
        do
        {
            x = x + 1;
        } while (x < 10);
        return x;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("do {", result.GeneratedCode);
        Assert.Contains("} while (x < 10);", result.GeneratedCode);
    }

    [Fact]
    public void Switch_Emits_Switch_Case()
    {
        var result = Convert("""
class Sample
{
    string Describe(int n)
    {
        switch (n)
        {
            case 1:
                return "one";
            case 2:
                return "two";
            default:
                return "other";
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("switch (n)", result.GeneratedCode);
        Assert.Contains("case 1:", result.GeneratedCode);
        Assert.Contains("default:", result.GeneratedCode);
    }

    [Fact]
    public void Return_Without_Value_Emits_Bare_Return()
    {
        var result = Convert("""
class Sample
{
    void EarlyExit(bool flag)
    {
        if (flag)
        {
            return;
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return;", result.GeneratedCode);
    }
}
