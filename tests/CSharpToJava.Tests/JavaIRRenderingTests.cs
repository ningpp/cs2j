using CSharpToJava.Core.Java;

namespace CSharpToJava.Tests;

/// <summary>
/// Java IR 节点渲染测试 — 验证结构化 IR 生成正确的 Java 源代码
/// </summary>
public class JavaIRRenderingTests
{
    [Fact]
    public void JavaMethodBody_WithRawStatements_RendersCorrectly()
    {
        var body = new JavaMethodBody();
        body.Statements.Add(new JavaRawStatement("int x = 1;"));
        body.Statements.Add(new JavaRawStatement("return x;"));

        var output = body.ToString("    ");
        Assert.Contains("int x = 1;", output);
        Assert.Contains("return x;", output);
    }

    [Fact]
    public void JavaIfStatement_WithElse_RendersCorrectly()
    {
        var ifStmt = new JavaIfStatement
        {
            Condition = new JavaIdentifierExpression("x > 0"),
            ThenBody = new JavaBlockStatement
            {
                Statements = { new JavaReturnStatement { Expression = new JavaLiteralExpression("true") } }
            },
            ElseBody = new JavaBlockStatement
            {
                Statements = { new JavaReturnStatement { Expression = new JavaLiteralExpression("false") } }
            }
        };

        var output = ifStmt.ToString("");
        Assert.Contains("if (x > 0)", output);
        Assert.Contains("return true;", output);
        Assert.Contains("} else {", output);
        Assert.Contains("return false;", output);
    }

    [Fact]
    public void JavaForEachStatement_RendersCorrectly()
    {
        var forEach = new JavaForEachStatement
        {
            VariableType = "String",
            VariableName = "item",
            Collection = new JavaIdentifierExpression("items"),
            Body = new JavaBlockStatement
            {
                Statements =
                {
                    new JavaExpressionStatement(new JavaMethodCallExpression
                    {
                        Target = new JavaIdentifierExpression("System.out"),
                        MethodName = "println",
                        Arguments = { new JavaIdentifierExpression("item") }
                    })
                }
            }
        };

        var output = forEach.ToString("");
        Assert.Contains("for (String item : items)", output);
        Assert.Contains("System.out.println(item);", output);
    }

    [Fact]
    public void JavaTryCatchStatement_WithResources_RendersCorrectly()
    {
        var tryCatch = new JavaTryCatchStatement
        {
            Resources = { "InputStream is = new FileInputStream(f)" },
            TryBody = new JavaBlockStatement
            {
                Statements = { new JavaRawStatement("process(is);") }
            },
            CatchClauses =
            {
                new JavaCatchClause
                {
                    ExceptionType = "IOException",
                    VariableName = "e",
                    Body = new JavaBlockStatement
                    {
                        Statements = { new JavaRawStatement("e.printStackTrace();") }
                    }
                }
            }
        };

        var output = tryCatch.ToString("");
        Assert.Contains("try (InputStream is = new FileInputStream(f))", output);
        Assert.Contains("catch (IOException e)", output);
    }

    [Fact]
    public void JavaMethodCallExpression_WithChainedCalls_RendersCorrectly()
    {
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("list"),
                MethodName = "stream"
            },
            MethodName = "filter",
            Arguments = { new JavaLambdaExpression
            {
                Parameters = { "x" },
                ExpressionBody = new JavaBinaryExpression
                {
                    Left = new JavaIdentifierExpression("x"),
                    Operator = ">",
                    Right = new JavaLiteralExpression("0")
                }
            }}
        };

        var output = call.ToInlineString();
        Assert.Equal("list.stream().filter(x -> x > 0)", output);
    }

    [Fact]
    public void JavaMethodDeclaration_WithStructuredBody_RendersCorrectly()
    {
        var method = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = "int",
            Name = "add",
            Parameters = { new JavaParameter("int", "a"), new JavaParameter("int", "b") },
            StructuredBody = new JavaMethodBody(new JavaStatement[]
            {
                new JavaReturnStatement
                {
                    Expression = new JavaBinaryExpression
                    {
                        Left = new JavaIdentifierExpression("a"),
                        Operator = "+",
                        Right = new JavaIdentifierExpression("b")
                    }
                }
            })
        };

        var output = method.ToString("");
        Assert.Contains("public static int add(int a, int b)", output);
        Assert.Contains("return a + b;", output);
        Assert.Contains("{", output);
        Assert.Contains("}", output);
    }

    [Fact]
    public void JavaSyntaxRewriter_CanRenameMethodCalls()
    {
        var body = new JavaMethodBody(new JavaStatement[]
        {
            new JavaExpressionStatement(new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("list"),
                MethodName = "add",
                Arguments = { new JavaLiteralExpression("42") }
            }),
            new JavaReturnStatement
            {
                Expression = new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression("list"),
                    MethodName = "size"
                }
            }
        });

        var rewriter = new MethodRenameRewriter("add", "put");
        var rewritten = rewriter.VisitMethodBody(body);

        var output = rewritten.ToString("");
        Assert.Contains("list.put(42);", output);
        Assert.Contains("list.size()", output); // "size" unchanged
    }

    [Fact]
    public void JavaVariableDeclarationStatement_WithInitializer_RendersCorrectly()
    {
        var stmt = new JavaVariableDeclarationStatement
        {
            Type = "List<String>",
            Name = "names",
            Initializer = new JavaNewExpression
            {
                Type = "ArrayList<>",
            },
            IsFinal = true
        };

        var output = stmt.ToString("");
        Assert.Equal("final List<String> names = new ArrayList<>();", output);
    }

    [Fact]
    public void JavaSwitchStatement_RendersCorrectly()
    {
        var switchStmt = new JavaSwitchStatement
        {
            Expression = new JavaIdentifierExpression("value"),
            Sections =
            {
                new JavaSwitchSection
                {
                    Labels = { "case 1:" },
                    Statements =
                    {
                        new JavaReturnStatement { Expression = new JavaLiteralExpression("\"one\"") }
                    }
                },
                new JavaSwitchSection
                {
                    Labels = { "default:" },
                    Statements =
                    {
                        new JavaReturnStatement { Expression = new JavaLiteralExpression("\"other\"") }
                    }
                }
            }
        };

        var output = switchStmt.ToString("");
        Assert.Contains("switch (value)", output);
        Assert.Contains("case 1:", output);
        Assert.Contains("return \"one\";", output);
        Assert.Contains("default:", output);
    }

    [Fact]
    public void JavaInstanceOfExpression_WithPatternVariable_RendersCorrectly()
    {
        var expr = new JavaInstanceOfExpression
        {
            Expression = new JavaIdentifierExpression("obj"),
            Type = "String",
            PatternVariable = "s"
        };

        Assert.Equal("obj instanceof String s", expr.ToInlineString());
    }

    [Fact]
    public void JavaLambdaExpression_WithBlockBody_RendersCorrectly()
    {
        var lambda = new JavaLambdaExpression
        {
            Parameters = { "x", "y" },
            BlockBody = new JavaBlockStatement
            {
                Statements =
                {
                    new JavaVariableDeclarationStatement { Type = "int", Name = "sum", Initializer = new JavaBinaryExpression { Left = new JavaIdentifierExpression("x"), Operator = "+", Right = new JavaIdentifierExpression("y") } },
                    new JavaReturnStatement { Expression = new JavaIdentifierExpression("sum") }
                }
            }
        };

        var output = lambda.ToInlineString();
        Assert.Contains("(x, y) ->", output);
        Assert.Contains("int sum = x + y;", output);
        Assert.Contains("return sum;", output);
    }

    /// <summary>
    /// 自定义 Rewriter：重命名方法调用
    /// </summary>
    private class MethodRenameRewriter : JavaSyntaxRewriter
    {
        private readonly string _oldName;
        private readonly string _newName;

        public MethodRenameRewriter(string oldName, string newName)
        {
            _oldName = oldName;
            _newName = newName;
        }

        public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
        {
            if (node.MethodName == _oldName)
            {
                node.MethodName = _newName;
            }
            return base.VisitMethodCallExpression(node);
        }
    }
}
