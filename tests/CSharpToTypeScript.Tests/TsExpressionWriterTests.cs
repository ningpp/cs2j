using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsExpressionWriterTests
{
    private static string EmitExpr(JavaExpression expr)
    {
        var table = new TsMemberMappingTable(null, null);
        var writer = new TsExpressionWriter(table, new TsImportCollector());
        return writer.ToInlineString(expr);
    }

    [Fact]
    public void Literal_String_Emits_With_Quotes()
    {
        Assert.Equal("\"hello\"", EmitExpr(new JavaLiteralExpression("\"hello\"")));
    }

    [Fact]
    public void Literal_Long_Emits_Bigint_Suffix()
    {
        // IR 中 long 字面量带 L 后缀，TS 需改为 n
        Assert.Equal("100n", EmitExpr(new JavaLiteralExpression("100L")));
    }

    [Fact]
    public void Literal_Long_Hex_Emits_Bigint_Suffix()
    {
        // 十六进制 long 字面量同样按 L 后缀转换
        Assert.Equal("0xFFn", EmitExpr(new JavaLiteralExpression("0xFFL")));
    }

    [Fact]
    public void Literal_Long_With_Underscores_Emits_Bigint_Suffix()
    {
        // 下划线分隔的 long 字面量（StopwatchApiRewriter 等真实产出路径）
        Assert.Equal("1_000_000n", EmitExpr(new JavaLiteralExpression("1_000_000L")));
    }

    [Fact]
    public void Literal_Char_Emits_As_String()
    {
        // IR 中 char 字面量 'a'，TS 用 "a"
        Assert.Equal("\"a\"", EmitExpr(new JavaLiteralExpression("'a'")));
    }

    [Fact]
    public void Identifier_Passthrough()
    {
        Assert.Equal("foo", EmitExpr(new JavaIdentifierExpression("foo")));
    }

    [Fact]
    public void Identifier_Reserved_Word_Suffixed()
    {
        Assert.Equal("let$", EmitExpr(new JavaIdentifierExpression("let")));
    }

    [Fact]
    public void BinaryExpression_IntegerLiteralDivision_Wraps_MathTrunc()
    {
        // 两个整数字面量操作数的 / -> Math.trunc(7 / 2)
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("7"),
            Operator = "/",
            Right = new JavaLiteralExpression("2"),
        };
        var result = EmitExpr(expr);
        Assert.Equal("Math.trunc(7 / 2)", result);
    }

    [Fact]
    public void BinaryExpression_Hex_Integer_Division_Wrapped()
    {
        // 十六进制整数字面量同样包装 Math.trunc
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("0xFF"),
            Operator = "/",
            Right = new JavaLiteralExpression("2"),
        };
        Assert.Equal("Math.trunc(0xFF / 2)", EmitExpr(expr));
    }

    [Fact]
    public void BinaryExpression_Underscore_Integer_Division_Wrapped()
    {
        // 下划线分隔的整数字面量同样包装 Math.trunc
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("1_000"),
            Operator = "/",
            Right = new JavaLiteralExpression("2"),
        };
        Assert.Equal("Math.trunc(1_000 / 2)", EmitExpr(expr));
    }

    [Fact]
    public void BinaryExpression_IdentifierDivision_Not_Wrapped_In_M1()
    {
        // 标识符操作数无类型信息（IR 不携带类型），M1 不包 Math.trunc，
        // 避免误伤 double 除法；类型感知处理在 M2 数值语义 rewriter（Task 19）
        var expr = new JavaBinaryExpression
        {
            Left = new JavaIdentifierExpression("a"),
            Operator = "/",
            Right = new JavaIdentifierExpression("b"),
        };
        Assert.Equal("a / b", EmitExpr(expr));
    }

    [Fact]
    public void BinaryExpression_BigintLiteralDivision_Not_Wrapped()
    {
        // bigint 除法 JS 天然截断，且 Math.trunc 不接受 bigint（spec §2.5）
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("100L"),
            Operator = "/",
            Right = new JavaLiteralExpression("3L"),
        };
        Assert.Equal("100n / 3n", EmitExpr(expr));
    }

    [Fact]
    public void BinaryExpression_Hex_Long_Division_Not_Wrapped()
    {
        // hex long（bigint）不包 Math.trunc：Math.trunc 不接受 bigint（运行时 TypeError）
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("0xFFL"),
            Operator = "/",
            Right = new JavaLiteralExpression("2"),
        };
        Assert.Equal("0xFFn / 2", EmitExpr(expr));
    }

    [Fact]
    public void BinaryExpression_Binary_Literal_Division_Wrapped()
    {
        // 二进制整数字面量同样包装 Math.trunc
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("0b101"),
            Operator = "/",
            Right = new JavaLiteralExpression("2"),
        };
        Assert.Equal("Math.trunc(0b101 / 2)", EmitExpr(expr));
    }

    [Fact]
    public void MethodCall_String_ToUpper_Maps_To_ToUpperCase()
    {
        // IR 中已是 Java 风味：str.toUpperCase() （Java 方法名）
        // 内置表映射 java.lang.String.toUpperCase -> toUpperCase（直译）
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("str"),
            MethodName = "toUpperCase",
        };
        Assert.Equal("str.toUpperCase()", EmitExpr(call));
    }

    [Fact]
    public void MethodCall_System_Out_Println_Maps_To_ConsoleLog()
    {
        // IR: System.out.println(...) -> console.log(...)
        // 这里用 java.lang.System + out.println 测内置映射
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMemberAccessExpression
            {
                Target = new JavaIdentifierExpression("System"),
                MemberName = "out",
            },
            MethodName = "println",
            Arguments = { new JavaLiteralExpression("\"hi\"") },
        };
        Assert.Equal("console.log(\"hi\")", EmitExpr(call));
    }

    [Fact]
    public void System_Err_Println_Maps_To_Console_Error()
    {
        // System.err.println(...) -> console.error(...)
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMemberAccessExpression
            {
                Target = new JavaIdentifierExpression("System"),
                MemberName = "err",
            },
            MethodName = "println",
            Arguments = { new JavaLiteralExpression("\"x\"") },
        };
        Assert.Equal("console.error(\"x\")", EmitExpr(call));
    }

    [Fact]
    public void ThisExpression_Emits_As_This()
    {
        Assert.Equal("this", EmitExpr(new JavaThisExpression()));
    }

    [Fact]
    public void MemberAccess_Reserved_Word_Member_Passthrough()
    {
        // 点号后的属性名无需保留字转义：foo.type 合法 TS（声明处仍需 SafeIdentifier）
        var access = new JavaMemberAccessExpression
        {
            Target = new JavaIdentifierExpression("foo"),
            MemberName = "type",
        };
        Assert.Equal("foo.type", EmitExpr(access));
    }

    [Fact]
    public void LambdaExpression_Emits_As_Arrow_Function()
    {
        // JavaLambdaExpression.Parameters 为 List<string>（纯参数名），
        // 块体在 BlockBody（JavaBlockStatement）
        var lambda = new JavaLambdaExpression();
        lambda.Parameters.Add("x");
        var block = new JavaBlockStatement();
        block.Statements.Add(new JavaReturnStatement { Expression = new JavaLiteralExpression("42") });
        lambda.BlockBody = block;
        var result = EmitExpr(lambda);
        Assert.Contains("(x) =>", result);
        Assert.Contains("return 42;", result);
    }
}