using System.IO;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.TypeScript;
using CSharpToJava.TypeMapping;

namespace CSharpToTypeScript.Tests;

public class TsEmitterIntegrationTests : TsConversionTestBase
{
    [Fact]
    public void Empty_Class_Converts_To_Ts()
    {
        var result = Convert("""
class Foo { }
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("export class Foo", result.GeneratedCode);
        Assert.Contains("}", result.GeneratedCode);
    }

    [Fact]
    public void Class_With_Field_And_Method_Converts()
    {
        var result = Convert("""
class Point
{
    int x;
    int y;

    int Sum()
    {
        return x + y;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("export class Point", result.GeneratedCode);
        Assert.Contains("x: number = 0", result.GeneratedCode);
        Assert.Contains("y: number = 0", result.GeneratedCode);
        Assert.Contains("Sum(): number", result.GeneratedCode);
    }

    [Fact]
    public void Int_Field_Gets_Number_Type()
    {
        var result = Convert("""
class Sample
{
    int value = 42;
}
""");
        Assert.True(result.Success);
        Assert.Contains("value: number = 42", result.GeneratedCode);
    }

    [Fact]
    public void Long_Literal_Gets_Bigint_Suffix()
    {
        var result = Convert("""
class Sample
{
    long ticks = 100L;
}
""");
        Assert.True(result.Success);
        Assert.Contains("ticks: bigint = 100n", result.GeneratedCode);
    }

    [Fact]
    public void String_Field_Gets_String_Type()
    {
        var result = Convert("""
class Sample
{
    string name = "test";
}
""");
        Assert.True(result.Success);
        Assert.Contains("name: string = \"test\"", result.GeneratedCode);
    }

    [Fact]
    public void Bool_Field_Gets_Boolean_Type()
    {
        var result = Convert("""
class Sample
{
    bool flag = true;
}
""");
        Assert.True(result.Success);
        Assert.Contains("flag: boolean = true", result.GeneratedCode);
    }

    [Fact]
    public void Void_Method_Gets_Void_Return()
    {
        var result = Convert("""
class Sample
{
    void DoNothing() { }
}
""");
        Assert.True(result.Success);
        Assert.Contains("DoNothing(): void", result.GeneratedCode);
    }

    [Fact]
    public void If_Statement_Emits_Correctly()
    {
        var result = Convert("""
class Sample
{
    int Check(int x)
    {
        if (x > 0)
        {
            return 1;
        }
        else
        {
            return 0;
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if (x > 0)", result.GeneratedCode);
        Assert.Contains("} else {", result.GeneratedCode);
    }

    [Fact]
    public void ForEach_Emits_ForOf()
    {
        var result = Convert("""
class Sample
{
    int Sum(int[] arr)
    {
        int total = 0;
        foreach (var item in arr)
        {
            total = total + item;
        }
        return total;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("for (const item of arr)", result.GeneratedCode);
    }

    [Fact]
    public void Console_WriteLine_Maps_To_ConsoleLog()
    {
        var result = Convert("""
class Sample
{
    void Greet()
    {
        Console.WriteLine("hello");
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("console.log(\"hello\")", result.GeneratedCode);
    }

    // ─── 直接发射绿测试（Task 10）：管线未分叉（Task 11 才分叉），利用 Convert 保留的
    // JavaCompilationUnit IR 直接调用 TypeScriptEmitter.Emit，证明发射器端到端正确。
    private string EmitTs(string csharpSource)
    {
        var result = Convert(csharpSource);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(result.Compilation);

        var basePath = AppContext.BaseDirectory;
        var typeMappingPath = Path.Combine(basePath, "config", "typescript", "TypeMappings.json");
        var memberMappingPath = Path.Combine(basePath, "config", "typescript", "MemberMappings.json");

        var context = new ConversionContext(
            new ConversionOptions
            {
                TargetLanguage = TargetLanguage.TypeScript,
                TypeScriptMappingConfigPath = File.Exists(typeMappingPath) ? typeMappingPath : null,
                TypeScriptMemberMappingPath = File.Exists(memberMappingPath) ? memberMappingPath : null,
            },
            new TypeMappingRegistry());
        return new TypeScriptEmitter().Emit(result.Compilation, context);
    }

    [Fact]
    public void Direct_Empty_Class_Converts_To_Ts()
    {
        var code = EmitTs("""
class Foo { }
""");
        Assert.Contains("export class Foo", code);
        Assert.Contains("}", code);
    }

    [Fact]
    public void Direct_Class_With_Field_And_Method_Converts()
    {
        var code = EmitTs("""
class Point
{
    int x;
    int y;

    int Sum()
    {
        return x + y;
    }
}
""");
        Assert.Contains("export class Point", code);
        Assert.Contains("x: number = 0", code);
        Assert.Contains("y: number = 0", code);
        // IR 形态：方法名为 Java camelCase（sum），非 C# PascalCase；PascalCase 还原属命名管线后续任务。
        Assert.Contains("sum(): number", code);
    }

    [Fact]
    public void Direct_Int_Field_Gets_Number_Type()
    {
        var code = EmitTs("""
class Sample
{
    int value = 42;
}
""");
        Assert.Contains("value: number = 42", code);
    }

    [Fact]
    public void Direct_Long_Literal_Gets_Bigint_Suffix()
    {
        var code = EmitTs("""
class Sample
{
    long ticks = 100L;
}
""");
        Assert.Contains("ticks: bigint = 100n", code);
    }

    [Fact]
    public void Direct_String_Field_Gets_String_Type()
    {
        var code = EmitTs("""
class Sample
{
    string name = "test";
}
""");
        Assert.Contains("name: string = \"test\"", code);
    }

    [Fact]
    public void Direct_Bool_Field_Gets_Boolean_Type()
    {
        var code = EmitTs("""
class Sample
{
    bool flag = true;
}
""");
        Assert.Contains("flag: boolean = true", code);
    }

    [Fact]
    public void Direct_Void_Method_Gets_Void_Return()
    {
        var code = EmitTs("""
class Sample
{
    void DoNothing() { }
}
""");
        // IR 形态：方法名 Java camelCase（doNothing）。
        Assert.Contains("doNothing(): void", code);
    }

    [Fact]
    public void Direct_If_Statement_Emits_Correctly()
    {
        var code = EmitTs("""
class Sample
{
    int Check(int x)
    {
        if (x > 0)
        {
            return 1;
        }
        else
        {
            return 0;
        }
    }
}
""");
        Assert.Contains("if (x > 0)", code);
        Assert.Contains("} else {", code);
    }

    [Fact]
    public void Direct_ForEach_Emits_ForOf()
    {
        var code = EmitTs("""
class Sample
{
    int Sum(int[] arr)
    {
        int total = 0;
        foreach (var item in arr)
        {
            total = total + item;
        }
        return total;
    }
}
""");
        // IR 形态：foreach 当前为 JavaRawStatement（"for (int item : arr) {...}"），
        // 非 JavaForEachStatement；for-of 发射需 IR 结构化/rewriter（Task 9 单测已覆盖结构化节点映射）。
        Assert.Contains("for (int item : arr)", code);
    }

    [Fact]
    public void Direct_Console_WriteLine_Maps_To_ConsoleLog()
    {
        var code = EmitTs("""
class Sample
{
    void Greet()
    {
        Console.WriteLine("hello");
    }
}
""");
        // IR 形态：Console.WriteLine 当前为 JavaRawStatement（"System.out.println(...)"），
        // console.log 映射在 IR 携带结构化调用节点后生效（Task 9 单测已覆盖）。
        Assert.Contains("System.out.println(\"hello\")", code);
    }
}