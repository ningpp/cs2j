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
        // 经典 for 头不含 " : "，绝不走增强 for 改写路径（1 参重载无映射表，直通；
        // let 化经 2 参重载由 Classic_For_With_Int_Declaration_Is_Let_ified 覆盖）。
        var input = "for (int i = 0; i < n; i = i + 1) {\n        total = total + i;\n    }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Classic_For_With_Ternary_In_Header_Passes_Through()
    {
        // 普通 for 头内三元含 " : "，但 " : " 左侧末 token 非标识符，必须直通避免误改写为
        // for-of（1 参重载无映射表；2 参重载下三元初始化器经 let 化，见
        // Classic_For_With_Ternary_Initializer_Is_Let_ified）。
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
        // 增强 for 头不含顶层 ";"，普通 for（含任意三元）不得走 for-of 改写
        //（1 参重载直通；2 参重载下三元初始化器经 let 化，见
        // Classic_For_With_Ternary_Initializer_Is_Let_ified）。
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

    // ---- Task 13：经典 for 头声明段 let 化（2 参重载，映射表走 TsMemberMappingTable）----

    private static string NormalizeWithTable(string code)
    {
        var table = new TsMemberMappingTable(null, null);
        var writer = new TsExpressionWriter(table, new TsImportCollector());
        return TsRawStatementNormalizer.Normalize(code, writer.MapType);
    }

    [Fact]
    public void Classic_For_With_Int_Declaration_Is_Let_ified()
    {
        // Task 13 登记项（Task 11 注记⑥）：raw 经典 for 头保持 Java 形态
        // "for (int i = 0; ...)"（含非法 TS 声明 int i = 0），let 化后
        // "for (let i: number = 0; ...)"，类型经 TsMemberMappingTable（int → number）。
        var input = "for (int i = 0; i < n; i = i + 1) {\n        total = total + i;\n    }";
        var expected = "for (let i: number = 0; i < n; i = i + 1) {\n        total = total + i;\n    }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_String_Declaration_Is_Let_ified()
    {
        // String → string（TsMemberMappingTable 内置 Java 风味类型表）
        var input = "for (String s = \"\"; s.length() > 0; s = s.substring(1)) { use(s); }";
        var expected = "for (let s: string = \"\"; s.length() > 0; s = s.substring(1)) { use(s); }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_Multi_Variable_Declaration_Passes_Through()
    {
        // 多声明（init 顶层逗号）无法用单个 let 表达 → 直通
        var input = "for (int i = 0, j = 1; i < n; i++) { work(i, j); }";
        Assert.Equal(input, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_Without_Initializer_Passes_Through()
    {
        // 无初始化声明段（int i;）不匹配 "TYPE identifier = expr" 形态 → 直通
        var input = "for (int i; i < n; i++) { work(i); }";
        Assert.Equal(input, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_Empty_Header_Passes_Through()
    {
        // for (;;) 声明段为空 → 直通（保留无限循环形态）
        var input = "for (;;) { work(); }";
        Assert.Equal(input, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_Ternary_Initializer_Is_Let_ified()
    {
        // 与三元场景共存：声明段 let 化，三元初始化器原样保留（Task 11 复审 Bug 1
        // 的 for-of 误改写回归由 Classic_For_With_Ternary_Identifier_Operands_Passes_Through 守卫）
        var input = "for (int i = (flag != 0 ? a : b); i < 10; i = i + 1) { work(i); }";
        var expected = "for (let i: number = (flag != 0 ? a : b); i < 10; i = i + 1) { work(i); }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    // ---- Task 13：raw 语句内 char 字面量 → 双引号字符串 ----

    [Fact]
    public void Char_Literal_In_Raw_Statement_Is_Double_Quoted()
    {
        // 前端 return 语句为 JavaRawStatement 直通（return 'A';），不经表达式发射层；
        // char 字面量归一化为双引号字符串，与 char → string 映射语义一致。
        Assert.Equal("return \"A\";", NormalizeWithTable("return 'A';"));
    }

    [Fact]
    public void Char_Literal_Newline_Escape_Is_Preserved()
    {
        // '\n' → "\n"（转义在双引号字符串中原样合法）
        Assert.Equal("return \"\\n\";", NormalizeWithTable("return '\\n';"));
    }

    [Fact]
    public void Char_Literal_Single_Quote_Escape_Is_Unescaped()
    {
        // '\'' → "'"（单引号转义在双引号字符串中不需要）
        Assert.Equal("return \"'\";", NormalizeWithTable("return '\\'';"));
    }

    [Fact]
    public void Char_Literal_Inside_String_Is_Not_Touched()
    {
        // 双引号字符串区域整体跳过，内部单引号（如 it's）不被改写
        var input = "return \"it's a test\";";
        Assert.Equal(input, NormalizeWithTable(input));
    }

    [Fact]
    public void Char_Literal_With_Double_Quote_Passes_Through()
    {
        // 内含双引号的 '\"' 保持单引号直通（M2 字符处理任务细化，与表达式层边界一致）
        var input = "return '\\\"';";
        Assert.Equal(input, NormalizeWithTable(input));
    }
}
