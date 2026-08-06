using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// TsRawStatementNormalizer 组件级测试（Task 11 引入）。
/// 前端对 Console.WriteLine/foreach 产出 JavaRawStatement 直通（非结构化节点），
/// 发射前经最小文本归一化得到合法 TS；不匹配的 raw 语句原样直通。
/// </summary>
public class TsRawStatementNormalizerTests
{
    [Fact]
    public void System_Out_Println_Maps_To_Console_Log()
    {
        Assert.Equal(
            "console.log(\"hello\");",
            TsRawStatementNormalizer.Normalize("System.out.println(\"hello\");"));
    }

    [Fact]
    public void System_Err_Println_Maps_To_Console_Error()
    {
        Assert.Equal(
            "console.error(\"oops\");",
            TsRawStatementNormalizer.Normalize("System.err.println(\"oops\");"));
    }

    [Fact]
    public void Println_With_Arguments_Preserves_Expression_And_Semicolon()
    {
        Assert.Equal(
            "console.log(\"total: \" + total);",
            TsRawStatementNormalizer.Normalize("System.out.println(\"total: \" + total);"));
    }

    [Fact]
    public void Println_Inside_If_Guard_Is_Normalized()
    {
        // WriteLineIf 前端产出 "if (cond) System.out.println(x);" 形式的 raw 语句
        Assert.Equal(
            "if (cond) console.log(x);",
            TsRawStatementNormalizer.Normalize("if (cond) System.out.println(x);"));
    }

    [Fact]
    public void Enhanced_For_Maps_To_For_Of_Preserving_Multiline_Body()
    {
        var input = "for (int item : arr) {\n        total = total + item;\n    }";
        var expected = "for (const item of arr) {\n        total = total + item;\n    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Enhanced_For_With_Quoted_String_Argument_Is_Normalized()
    {
        var input = "for (String part : \"a,b,c\".split(\",\")) {\n        console.log(part);\n    }";
        var expected = "for (const part of \"a,b,c\".split(\",\")) {\n        console.log(part);\n    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Enhanced_For_With_Method_Call_Collection_Is_Normalized()
    {
        var input = "for (char c : name.toCharArray()) { System.out.println(c); }";
        var expected = "for (const c of name.toCharArray()) { console.log(c); }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Enhanced_For_Without_Braces_Is_Normalized()
    {
        var input = "for (int item : arr) System.out.println(item);";
        var expected = "for (const item of arr) console.log(item);";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Generic_Enhanced_For_Type_Is_Dropped()
    {
        var input = "for (Map.Entry<K, V> entry : map.entrySet()) { process(entry); }";
        var expected = "for (const entry of map.entrySet()) { process(entry); }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Non_For_Statement_Passes_Through()
    {
        var input = "if (x > 0) { return 1; } else { return 0; }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Plain_Statement_Passes_Through()
    {
        var input = "int total = 0;";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Classic_For_Without_Colon_Passes_Through()
    {
        var input = "for (int i = 0; i < n; i = i + 1) {\n        total = total + i;\n    }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Classic_For_With_Ternary_In_Header_Passes_Through()
    {
        // 普通 for 头内三元含 " : "，但 " : " 左侧末 token 非标识符，必须直通避免误改写
        var input = "for (int i = cond ? 0 : 1; i < n; i++) { work(i); }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Empty_String_Passes_Through()
    {
        Assert.Equal(string.Empty, TsRawStatementNormalizer.Normalize(string.Empty));
    }

    [Fact]
    public void Classic_For_With_Ternary_Identifier_Operands_Passes_Through()
    {
        // 回归（Task 11 复审 Bug 1）：三元操作数为标识符（flag ? a : b）时，旧守卫
        // "左侧末 token 为标识符" 无法拦截（a : 满足），会被误改写为非法 for-of；
        // 增强 for 头不含顶层 ";"，普通 for（含任意三元）必须直通。
        var input = "for (int i = flag ? a : b; i < n; i++) { work(i); }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Println_Argument_Containing_Println_Text_Is_Preserved()
    {
        // 回归（Task 11 复审 Bug 2）：实参字符串字面量内含 "System.out.println(" 文本时，
        // 旧整串 Replace 会连同字面量内容一并改写（console.log("console.log(x)")），
        // 破坏实参语义；替换必须跳过引号内区域。
        Assert.Equal(
            "console.log(\"System.out.println(x)\");",
            TsRawStatementNormalizer.Normalize("System.out.println(\"System.out.println(x)\");"));
    }

    [Fact]
    public void Nested_Enhanced_Fors_Are_All_Normalized()
    {
        // 回归（Task 11 复审 Bug 3）：嵌套 foreach 整段 raw 直通时，旧实现只归一化
        // 语句起始的最外层 for 头，内层 for (int item : row) 原样保留（非法 TS）；
        // 修复后同一文本内所有增强 for 头均改写，body 原样保留。
        var input = "for (int row : arr) {\n        for (int item : row) {\n            total = total + item;\n        }\n    }";
        var expected = "for (const row of arr) {\n        for (const item of row) {\n            total = total + item;\n        }\n    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }
}
