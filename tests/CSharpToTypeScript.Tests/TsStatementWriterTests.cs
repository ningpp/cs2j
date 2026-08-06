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

    [Fact]
    public void TryCatch_Emits_Catch_Clauses()
    {
        var tryBody = new JavaBlockStatement();
        tryBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("a")));
        var catchBody = new JavaBlockStatement();
        catchBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("b")));

        var tryCatch = new JavaTryCatchStatement { TryBody = tryBody };
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IOException",
            VariableName = "e",
            Body = catchBody,
        });

        var result = EmitStmt(tryCatch);
        Assert.Contains("} catch (e) {", result);
        Assert.Contains("if (e instanceof IOException) {", result);
        Assert.Contains("} else {", result);
        Assert.Contains("throw e;", result);
    }

    [Fact]
    public void TryCatch_Multiple_Typed_Clauses_Guard_Chain()
    {
        var tryBody = new JavaBlockStatement();
        tryBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("a")));
        var body1 = new JavaBlockStatement();
        body1.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("b")));
        var body2 = new JavaBlockStatement();
        body2.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("c")));

        var tryCatch = new JavaTryCatchStatement { TryBody = tryBody };
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IOException",
            VariableName = "e",
            Body = body1,
        });
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IllegalStateException",
            VariableName = "e",
            Body = body2,
        });

        var result = EmitStmt(tryCatch);
        Assert.Contains("if (e instanceof IOException) {", result);
        Assert.Contains("} else if (e instanceof IllegalStateException) {", result);
        Assert.Contains("} else {", result);
        Assert.Contains("throw e;", result);
    }

    [Fact]
    public void TryCatch_Typed_Then_Bare_Clause()
    {
        var tryBody = new JavaBlockStatement();
        tryBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("a")));
        var body1 = new JavaBlockStatement();
        body1.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("b")));
        var body2 = new JavaBlockStatement();
        body2.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("c")));

        var tryCatch = new JavaTryCatchStatement { TryBody = tryBody };
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IOException",
            VariableName = "e",
            Body = body1,
        });
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "Exception",
            VariableName = "e",
            Body = body2,
        });

        var result = EmitStmt(tryCatch);
        Assert.Contains("if (e instanceof IOException) {", result);
        Assert.Contains("} else {", result);
        Assert.DoesNotContain("throw e;", result);
    }

    [Fact]
    public void TryCatch_Bare_Only_Emits_Body()
    {
        var tryBody = new JavaBlockStatement();
        tryBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("a")));
        var catchBody = new JavaBlockStatement();
        catchBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("b")));

        var tryCatch = new JavaTryCatchStatement { TryBody = tryBody };
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "Exception",
            VariableName = "e",
            Body = catchBody,
        });

        var result = EmitStmt(tryCatch);
        Assert.Contains("} catch (e) {", result);
        Assert.Contains("b;", result);
        Assert.DoesNotContain("instanceof", result);
    }

    [Fact]
    public void TryCatch_With_Finally()
    {
        var tryBody = new JavaBlockStatement();
        tryBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("a")));
        var catchBody = new JavaBlockStatement();
        catchBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("b")));
        var finallyBody = new JavaBlockStatement();
        finallyBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("c")));

        var tryCatch = new JavaTryCatchStatement
        {
            TryBody = tryBody,
            FinallyBody = finallyBody,
        };
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IOException",
            VariableName = "e",
            Body = catchBody,
        });

        var result = EmitStmt(tryCatch);
        Assert.Contains("finally {", result);
        Assert.Contains("c;", result);
    }

    [Fact]
    public void TryCatch_Different_Variable_Names_Emits_Alias()
    {
        var tryBody = new JavaBlockStatement();
        tryBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("a")));
        var body1 = new JavaBlockStatement();
        body1.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("b")));
        var body2 = new JavaBlockStatement();
        body2.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("ex")));

        var tryCatch = new JavaTryCatchStatement { TryBody = tryBody };
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IOException",
            VariableName = "e",
            Body = body1,
        });
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IllegalStateException",
            VariableName = "ex",
            Body = body2,
        });

        var result = EmitStmt(tryCatch);
        Assert.Contains("const ex = e;", result);
    }

    [Fact]
    public void TryCatch_Same_Variable_Names_No_Alias()
    {
        var tryBody = new JavaBlockStatement();
        tryBody.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("a")));
        var body1 = new JavaBlockStatement();
        body1.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("b")));
        var body2 = new JavaBlockStatement();
        body2.Statements.Add(new JavaExpressionStatement(new JavaIdentifierExpression("c")));

        var tryCatch = new JavaTryCatchStatement { TryBody = tryBody };
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IOException",
            VariableName = "e",
            Body = body1,
        });
        tryCatch.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "IllegalStateException",
            VariableName = "e",
            Body = body2,
        });

        var result = EmitStmt(tryCatch);
        Assert.DoesNotContain("const e = e;", result);
    }
    [Fact]
    public void Switch_Emits_Case_Labels_With_Colons()
    {
        var sw = new JavaSwitchStatement
        {
            Expression = new JavaIdentifierExpression("x"),
        };
        var section = new JavaSwitchSection();
        section.Labels.Add("case 1:");
        section.Labels.Add("default:");
        section.Statements.Add(new JavaBreakStatement());
        sw.Sections.Add(section);

        var result = EmitStmt(sw);
        Assert.Contains("switch (x) {", result);
        Assert.Contains("case 1:", result);
        Assert.Contains("default:", result);
    }

    [Fact]
    public void For_Initializer_Long_Literal_Becomes_Bigint()
    {
        var forStmt = new JavaForStatement
        {
            Initializer = "long i = 100L",
            Condition = new JavaIdentifierExpression("cond"),
            Increment = "i++",
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(forStmt);
        Assert.Contains("let i: bigint = 100n", result);
    }

    [Fact]
    public void For_Initializer_Unmapped_Type_Emits_Diagnostic()
    {
        var forStmt = new JavaForStatement
        {
            Initializer = "Map<String, Integer> m = new HashMap<>()",
            Condition = new JavaIdentifierExpression("cond"),
            Increment = "i++",
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(forStmt);
        Assert.Contains("cs2j: unmapped for-initializer type", result);
    }

    [Fact]
    public void For_Initializer_Multi_Variable_Emits_Diagnostic()
    {
        var forStmt = new JavaForStatement
        {
            Initializer = "int i = 0, j = 1",
            Condition = new JavaIdentifierExpression("cond"),
            Increment = "i++",
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(forStmt);
        Assert.Contains("cs2j: multi-variable", result);
    }

    [Fact]
    public void For_Initializer_String_Literal_Not_Rewritten()
    {
        var forStmt = new JavaForStatement
        {
            Initializer = "String s = \"100L\"",
            Condition = new JavaIdentifierExpression("cond"),
            Increment = "i++",
            Body = new JavaBlockStatement(),
        };
        var result = EmitStmt(forStmt);
        Assert.DoesNotContain("100n", result);
        Assert.Contains("100L", result);
    }
}