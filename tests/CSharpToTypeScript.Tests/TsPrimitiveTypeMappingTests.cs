namespace CSharpToTypeScript.Tests;

public class TsPrimitiveTypeMappingTests : TsConversionTestBase
{
    [Fact]
    public void Int_Maps_To_Number()
    {
        var result = Convert("class S { int x = 1; }");
        Assert.Contains("x: number = 1", result.GeneratedCode);
    }

    [Fact]
    public void Double_Maps_To_Number()
    {
        var result = Convert("class S { double x = 1.5; }");
        Assert.Contains("x: number = 1.5", result.GeneratedCode);
    }

    [Fact]
    public void Long_Maps_To_Bigint_With_N_Suffix()
    {
        var result = Convert("class S { long x = 100L; }");
        Assert.Contains("x: bigint = 100n", result.GeneratedCode);
    }

    [Fact]
    public void Bool_Maps_To_Boolean()
    {
        var result = Convert("class S { bool x = true; }");
        Assert.Contains("x: boolean = true", result.GeneratedCode);
    }

    [Fact]
    public void String_Maps_To_String()
    {
        var result = Convert("class S { string x = \"hi\"; }");
        Assert.Contains("x: string = \"hi\"", result.GeneratedCode);
    }

    [Fact]
    public void Char_Maps_To_String()
    {
        var result = Convert("class S { char x = 'a'; }");
        Assert.Contains("x: string = \"a\"", result.GeneratedCode);
    }

    [Fact]
    public void Void_Maps_To_Void()
    {
        var result = Convert("class S { void DoNothing() { } }");
        // IR 方法名为 Java camelCase（doNothing），PascalCase 还原属命名管线后续任务
        //（Task 10/11 既有约定：断言按 IR 实证修订）
        Assert.Contains("doNothing(): void", result.GeneratedCode);
    }

    [Fact]
    public void Object_Maps_To_Any()
    {
        var result = Convert("class S { object x = null; }");
        Assert.Contains("x: any = null", result.GeneratedCode);
    }

    [Fact]
    public void Nullable_Int_Maps_To_Number_Or_Null()
    {
        var result = Convert("""
class S
{
    int? x = null;
}
""");
        Assert.True(result.Success);
        Assert.Contains("number | null", result.GeneratedCode);
    }

    [Fact]
    public void Task13_Key_Scenarios_Compile_With_Tsc()
    {
        // L3 汇总（Task 13 发射器修复关键场景）：经典 for 头 let 化、char 字段 → 双引号字符串、
        // int? 初始化为 null → number | null、object → any 的产物经真实 tsc strict 编译通过。
        var result = Convert("""
class Sample
{
    char Flag = 'A';
    int? Count = null;
    object Tag = null;

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
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Flag: string = \"A\"", result.GeneratedCode);
        Assert.Contains("Count: number | null = null", result.GeneratedCode);
        Assert.Contains("Tag: any = null", result.GeneratedCode);
        Assert.Contains("for (let i: number = 0; i < n; i = i + 1)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}
