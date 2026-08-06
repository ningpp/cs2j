using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsStatementWriterTests
{
    private readonly TsStatementWriter _writer = new(
        new TsExpressionWriter(new TsMemberMappingTable(null, null), new TsImportCollector()),
        "    ");

    private string EmitStmt(JavaStatement stmt) => _writer.Emit(stmt, 0);

    [Fact]
    public void BlockStatement_Emits_Braces()
    {
        var block = new JavaBlockStatement();
        block.Statements.Add(new JavaReturnStatement { Expression = new JavaLiteralExpression("1") });
        var result = EmitStmt(block);
        Assert.Contains("{", result);
        Assert.Contains("return 1;", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void VariableDeclaration_Emits_Let()
    {
        // 实际属性名：Type/Name（非 VariableType/VariableName）
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "number",
            Name = "x",
            Initializer = new JavaLiteralExpression("42"),
        };
        var result = EmitStmt(decl);
        Assert.Contains("let x: number = 42;", result);
    }

    [Fact]
    public void ReturnStatement_Emits_Return()
    {
        var ret = new JavaReturnStatement { Expression = new JavaIdentifierExpression("x") };
        Assert.Contains("return x;", EmitStmt(ret));
    }

    [Fact]
    public void IfStatement_Emits_If_Else()
    {
        // 实际属性名：ThenBody/ElseBody（非 ThenStatement/ElseStatement）
        var ifStmt = new JavaIfStatement
        {
            Condition = new JavaIdentifierExpression("flag"),
            ThenBody = new JavaBlockStatement(),
            ElseBody = new JavaBlockStatement(),
        };
        var result = EmitStmt(ifStmt);
        Assert.Contains("if (flag) {", result);
        Assert.Contains("} else {", result);
    }

    [Fact]
    public void ForEachStatement_Emits_ForOf()
    {
        var forEach = new JavaForEachStatement
        {
            VariableType = "number",
            VariableName = "item",
            Collection = new JavaIdentifierExpression("arr"),
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(forEach);
        Assert.Contains("for (const item of arr) {", result);
    }

    [Fact]
    public void WhileStatement_Emits_While()
    {
        var whileStmt = new JavaWhileStatement
        {
            Condition = new JavaIdentifierExpression("running"),
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(whileStmt);
        Assert.Contains("while (running) {", result);
    }

    [Fact]
    public void ThrowStatement_Emits_Throw()
    {
        var throwStmt = new JavaThrowStatement
        {
            Expression = new JavaNewExpression { Type = "Error", Arguments = { new JavaLiteralExpression("\"oops\"") } },
        };
        var result = EmitStmt(throwStmt);
        Assert.Contains("throw new Error(\"oops\");", result);
    }

    [Fact]
    public void BreakStatement_Emits_Break()
    {
        Assert.Contains("break;", EmitStmt(new JavaBreakStatement()));
    }

    [Fact]
    public void ContinueStatement_Emits_Continue()
    {
        Assert.Contains("continue;", EmitStmt(new JavaContinueStatement()));
    }
}