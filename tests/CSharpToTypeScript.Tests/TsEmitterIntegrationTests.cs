using System.IO;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.TypeScript;
using CSharpToJava.TypeMapping;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// 计划 10 个集成测试（Task 11 分叉后全绿）+ 10 个直接发射绿测试 + 1 个 L3 tsc 编译用例。
/// 断言修订说明（Task 11，以 IR 实证为准）：
/// - 方法名：IR 为 Java camelCase（sum/doNothing），PascalCase 还原属命名管线后续任务；
/// - Console.WriteLine/foreach：IR 为 JavaRawStatement 直通（System.out.println/for (int item : arr)），
///   Task 11 引入 TsRawStatementNormalizer 最小归一化后按 console.log/for-of 断言；
/// - int[]：IR 类型为 Java 风味数组，MapType 数组后缀递归修复（I1）后按 number[] 断言。
/// </summary>
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
        // IR 方法名为 Java camelCase（sum），PascalCase 还原属命名管线后续任务（Task 10 注记 ③）
        Assert.Contains("sum(): number", result.GeneratedCode);
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
        // IR 方法名为 Java camelCase（doNothing），PascalCase 还原属命名管线后续任务（Task 10 注记 ③）
        Assert.Contains("doNothing(): void", result.GeneratedCode);
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
        // TS 专属金丝雀断言（Task 11 建议）：Java 输出不含 "check(x: number): number"，
        // 使本测试成为真正的管线分叉金丝雀（参数名 x、类型 number 均为 TS 形态）。
        Assert.Contains("check(x: number): number", result.GeneratedCode);
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
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // I1：int[] 经 MapType 数组后缀递归映射为 number[]（Java 输出为 int[]，不满足）
        Assert.Contains("sum(arr: number[]): number", result.GeneratedCode);
        // 前端 foreach 为 JavaRawStatement 直通，Task 11 最小归一化后按 for-of 断言
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
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // 前端 Console.WriteLine 为 JavaRawStatement("System.out.println(...)") 直通，
        // Task 11 最小归一化（println → console.log）后按 TS 输出断言
        Assert.Contains("console.log(\"hello\")", result.GeneratedCode);
    }

    [Fact]
    public void Pipeline_Output_Compiles_With_Tsc()
    {
        // L3（Task 11 新增）：Console/ForEach/数组参数混合场景的管线输出经真实 tsc 编译
        // （strict/ES2022；tsconfig 未指定 lib，默认含 DOM，console 可用）。
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
        Console.WriteLine("total: " + total);
        return total;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        AssertCompiles(result.GeneratedCode);
    }

    // ─── 直接发射测试（Task 10 起）：利用 Convert 保留的 JavaCompilationUnit IR 直接调用
    // TypeScriptEmitter.Emit。Task 11 分叉后与管线发射共用 TsStatementWriter（含 raw 归一化），
    // 断言按归一化后的真实输出同步。
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
        // TS 专属金丝雀断言（Task 11 建议）：if/else 本身语言中性，方法签名证明输出确为 TS。
        Assert.Contains("check(x: number): number", code);
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
        // 前端 foreach 为 JavaRawStatement 直通；Task 11 最小归一化（增强 for 头 → for-of）后
        // 与测试名名副其实。I1 修复后数组参数类型亦为 number[]。
        Assert.Contains("sum(arr: number[]): number", code);
        Assert.Contains("for (const item of arr)", code);
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
        // 前端 Console.WriteLine 为 JavaRawStatement 直通；Task 11 最小归一化（println → console.log）
        // 后与测试名名副其实。
        Assert.Contains("console.log(\"hello\")", code);
    }

    [Fact]
    public void Classic_For_With_Ternary_Is_Let_ified_Not_ForOf()
    {
        // 端到端复现（Task 11 复审 Bug 1 回归 + Task 13 let 化）：普通 for 头内三元操作数为
        // 标识符（flag != 0 ? a : b）时不得被误改写为 for-of；声明段按 Task 13 let 化
        // （for (let i: number = (flag != 0 ? a : b); ...)），三元初始化器原样保留。
        var result = Convert("""
class Sample
{
    int Pick(int flag, int a, int b)
    {
        int total = 0;
        for (int i = flag != 0 ? a : b; i < 10; i = i + 1)
        {
            total = total + i;
        }
        return total;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("for (let i: number = (flag !== 0 ? a : b); i < 10; i = i + 1)", result.GeneratedCode);
        Assert.DoesNotContain("for (const a of b)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Console_WriteLine_With_Println_Text_Argument_Is_Preserved()
    {
        // 端到端复现（Task 11 复审 Bug 2）：实参为含 "System.out.println(" 文本的字符串字面量时，
        // 归一化不得改写字面量内容（输出 console.log("System.out.println(x)")，语义保持）。
        var result = Convert("""
class Sample
{
    void Greet()
    {
        Console.WriteLine("System.out.println(x)");
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("console.log(\"System.out.println(x)\")", result.GeneratedCode);
        Assert.DoesNotContain("console.log(\"console.log(x)\")", result.GeneratedCode);
    }

    [Fact]
    public void Nested_ForEach_Emits_ForOf_For_All_Levels()
    {
        // 端到端复现（Task 11 复审 Bug 3）：嵌套 foreach 整段 raw 直通时，内外两层
        // 增强 for 头都必须归一化为 for-of，且产物可过 L3 tsc（strict/ES2022）。
        var result = Convert("""
class Sample
{
    int Sum(int[][] arr)
    {
        int total = 0;
        foreach (var row in arr)
        {
            foreach (var item in row)
            {
                total = total + item;
            }
        }
        return total;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("for (const row of arr)", result.GeneratedCode);
        Assert.Contains("for (const item of row)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Enumerator_Bridge_HasNext_Keeps_Its_Condition()
    {
        // End-to-end regression: the Java iterator bridge emits a raw hasNext() body
        // "if (_iteratorHasNext) return true;" that must keep its parenthesized
        // condition; it was misread as a reference cast and emitted as
        // "if return true;" (TS1005).
        var result = Convert("""
using System.Collections.Generic;

class MyEnumerator : IEnumerator<int>
{
    private int _current = -1;

    public int Current { get { return _current; } }

    object System.Collections.IEnumerator.Current { get { return _current; } }

    public bool MoveNext()
    {
        _current++;
        return _current < 3;
    }

    public void Reset()
    {
        _current = -1;
    }

    public void Dispose()
    {
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Round 16：裸字段补 this. 前缀（与 TS2663 收敛一致）
        Assert.Contains("if (this._iteratorHasNext) return true;", result.GeneratedCode);
    }
}
