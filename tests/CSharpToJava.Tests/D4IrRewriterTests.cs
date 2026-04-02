using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for D4 IR rewriters that address documented error patterns.
/// </summary>
public class D4IrRewriterTests
{
    // ═══════════════════════════════════════════════════════════
    //  Error 23: OperatorPrecedenceRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void OperatorPrecedence_NotLengthGreaterThanZero_WrapsInParens()
    {
        // !collection.length > 0  →  !(collection.length > 0)
        var rewriter = new OperatorPrecedenceRewriter();

        var binary = new JavaBinaryExpression
        {
            Left = new JavaUnaryExpression
            {
                Operator = "!",
                IsPostfix = false,
                Operand = new JavaMemberAccessExpression
                {
                    Target = new JavaIdentifierExpression("collection"),
                    MemberName = "length",
                },
            },
            Operator = ">",
            Right = new JavaLiteralExpression("0"),
        };

        var result = rewriter.VisitExpression(binary);

        // Should become: !(collection.length > 0)
        Assert.IsType<JavaUnaryExpression>(result);
        var unary = (JavaUnaryExpression)result;
        Assert.Equal("!", unary.Operator);
        Assert.IsType<JavaParenthesizedExpression>(unary.Operand);
        var inner = ((JavaParenthesizedExpression)unary.Operand).InnerExpression;
        Assert.IsType<JavaBinaryExpression>(inner);
        var innerBinary = (JavaBinaryExpression)inner;
        Assert.Equal(">", innerBinary.Operator);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void OperatorPrecedence_NotSizeCallGreaterThanZero_WrapsInParens()
    {
        // !list.size() > 0  →  !(list.size() > 0)
        var rewriter = new OperatorPrecedenceRewriter();

        var binary = new JavaBinaryExpression
        {
            Left = new JavaUnaryExpression
            {
                Operator = "!",
                IsPostfix = false,
                Operand = new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression("list"),
                    MethodName = "size",
                },
            },
            Operator = ">",
            Right = new JavaLiteralExpression("0"),
        };

        var result = rewriter.VisitExpression(binary);
        Assert.IsType<JavaUnaryExpression>(result);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void OperatorPrecedence_NormalBooleanNot_Unchanged()
    {
        // !flag > 0 where flag is not length/size should be unchanged
        var rewriter = new OperatorPrecedenceRewriter();

        var binary = new JavaBinaryExpression
        {
            Left = new JavaUnaryExpression
            {
                Operator = "!",
                IsPostfix = false,
                Operand = new JavaIdentifierExpression("flag"),
            },
            Operator = ">",
            Right = new JavaLiteralExpression("0"),
        };

        var result = rewriter.VisitExpression(binary);
        Assert.IsType<JavaBinaryExpression>(result);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    [Fact]
    public void OperatorPrecedence_InFullCompilationUnit_Works()
    {
        var rewriter = new OperatorPrecedenceRewriter();
        var cu = BuildCompilationUnit(
            new JavaExpressionStatement
            {
                Expression = new JavaBinaryExpression
                {
                    Left = new JavaUnaryExpression
                    {
                        Operator = "!",
                        IsPostfix = false,
                        Operand = new JavaMemberAccessExpression
                        {
                            Target = new JavaIdentifierExpression("arr"),
                            MemberName = "length",
                        },
                    },
                    Operator = ">",
                    Right = new JavaLiteralExpression("0"),
                },
            });

        rewriter.VisitCompilationUnit(cu);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 14: MapEntryTypeRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void MapEntryType_ForEachWithEntrySet_ReplacesSimpleEntry()
    {
        var rewriter = new MapEntryTypeRewriter();

        var forEach = new JavaForEachStatement
        {
            VariableType = "AbstractMap.SimpleEntry<String, Integer>",
            VariableName = "entry",
            Collection = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("map"),
                MethodName = "entrySet",
            },
            Body = new JavaBlockStatement(),
        };

        rewriter.VisitForEachStatement(forEach);
        Assert.Equal("Map.Entry<String, Integer>", forEach.VariableType);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void MapEntryType_VariableDecl_ReplacesSimpleEntry()
    {
        var rewriter = new MapEntryTypeRewriter();

        var varDecl = new JavaVariableDeclarationStatement
        {
            Type = "AbstractMap.SimpleEntry<String, Integer>",
            Name = "entry",
        };

        rewriter.VisitVariableDeclarationStatement(varDecl);
        Assert.Equal("Map.Entry<String, Integer>", varDecl.Type);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void MapEntryType_CastExpression_ReplacesSimpleEntry()
    {
        var rewriter = new MapEntryTypeRewriter();

        var cast = new JavaCastExpression
        {
            Type = "AbstractMap.SimpleEntry<String, Integer>",
            Expression = new JavaIdentifierExpression("obj"),
        };

        rewriter.VisitCastExpression(cast);
        Assert.Equal("Map.Entry<String, Integer>", cast.Type);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void MapEntryType_NonSimpleEntry_Unchanged()
    {
        var rewriter = new MapEntryTypeRewriter();

        var varDecl = new JavaVariableDeclarationStatement
        {
            Type = "Map.Entry<String, Integer>",
            Name = "entry",
        };

        rewriter.VisitVariableDeclarationStatement(varDecl);
        Assert.Equal("Map.Entry<String, Integer>", varDecl.Type);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 09: MemberwiseCloneRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void MemberwiseClone_RenamesToClone()
    {
        var rewriter = new MemberwiseCloneRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaThisExpression(),
            MethodName = "memberwiseClone",
        };

        var method = new JavaMethodDeclaration
        {
            Name = "copy",
            ReturnType = "Object",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaReturnStatement
        {
            Expression = call,
        });

        rewriter.VisitMethodDeclaration(method);

        Assert.Equal("clone", call.MethodName);
        Assert.Contains("CloneNotSupportedException", method.ThrownExceptions);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void MemberwiseClone_PascalCase_AlsoRenames()
    {
        var rewriter = new MemberwiseCloneRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaThisExpression(),
            MethodName = "MemberwiseClone",
        };

        var method = new JavaMethodDeclaration
        {
            Name = "copy",
            ReturnType = "Object",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaExpressionStatement { Expression = call });

        rewriter.VisitMethodDeclaration(method);
        Assert.Equal("clone", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void MemberwiseClone_NoTargetCall_Unchanged()
    {
        var rewriter = new MemberwiseCloneRewriter();

        // Static call without target — should not be rewritten
        var call = new JavaMethodCallExpression
        {
            MethodName = "memberwiseClone",
        };

        var method = new JavaMethodDeclaration
        {
            Name = "test",
            ReturnType = "void",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaExpressionStatement { Expression = call });

        rewriter.VisitMethodDeclaration(method);
        Assert.Equal("memberwiseClone", call.MethodName);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 22: MathMethodRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void MathMethod_SignumWithIntLiteral_BecomesIntegerSignum()
    {
        var rewriter = new MathMethodRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("Math"),
            MethodName = "signum",
        };
        call.Arguments.Add(new JavaLiteralExpression("42"));

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("Integer", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Equal("signum", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void MathMethod_CastIntSignum_DropsRedundantCast()
    {
        var rewriter = new MathMethodRewriter();

        var cast = new JavaCastExpression
        {
            Type = "int",
            Expression = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("Math"),
                MethodName = "signum",
            },
        };

        var result = rewriter.VisitExpression(cast);

        // The cast should be dropped, leaving just Integer.signum()
        Assert.IsType<JavaMethodCallExpression>(result);
        var call = (JavaMethodCallExpression)result;
        Assert.Equal("Integer", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Equal("signum", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void MathMethod_SignumWithDoubleLiteral_Unchanged()
    {
        var rewriter = new MathMethodRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("Math"),
            MethodName = "signum",
        };
        call.Arguments.Add(new JavaLiteralExpression("3.14"));

        rewriter.VisitMethodCallExpression(call);

        // Double argument should keep Math.signum
        Assert.Equal("Math", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 16: DelegateInvocationRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DelegateInvocation_InvokeWithOneArg_BecomesApply()
    {
        var rewriter = new DelegateInvocationRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("converter"),
            MethodName = "Invoke",
        };
        call.Arguments.Add(new JavaIdentifierExpression("value"));

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("apply", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void DelegateInvocation_InvokeWithNoArgs_BecomesRun()
    {
        var rewriter = new DelegateInvocationRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("action"),
            MethodName = "invoke",
        };

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("run", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void DelegateInvocation_InvokeWithTwoArgs_BecomesApply()
    {
        var rewriter = new DelegateInvocationRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("biFunc"),
            MethodName = "Invoke",
        };
        call.Arguments.Add(new JavaIdentifierExpression("a"));
        call.Arguments.Add(new JavaIdentifierExpression("b"));

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("apply", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void DelegateInvocation_RegularMethodCall_Unchanged()
    {
        var rewriter = new DelegateInvocationRewriter();

        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("list"),
            MethodName = "add",
        };
        call.Arguments.Add(new JavaIdentifierExpression("item"));

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("add", call.MethodName);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 21: GenericArrayCreationRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GenericArray_ParameterizedArrayType_RewritesToRawType()
    {
        var rewriter = new GenericArrayCreationRewriter();

        var newExpr = new JavaNewExpression
        {
            Type = "Map.Entry<String, Integer>[10]",
        };

        rewriter.VisitNewExpression(newExpr);

        // Should become new Map.Entry[10] (raw type)
        Assert.Equal("Map.Entry[10]", newExpr.Type);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void GenericArray_SimpleArrayType_Unchanged()
    {
        var rewriter = new GenericArrayCreationRewriter();

        var newExpr = new JavaNewExpression
        {
            Type = "String[10]",
        };

        rewriter.VisitNewExpression(newExpr);

        Assert.Equal("String[10]", newExpr.Type);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    [Fact]
    public void GenericArray_NonArrayGenericType_Unchanged()
    {
        var rewriter = new GenericArrayCreationRewriter();

        var newExpr = new JavaNewExpression
        {
            Type = "ArrayList<String>",
        };

        rewriter.VisitNewExpression(newExpr);

        Assert.Equal("ArrayList<String>", newExpr.Type);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    [Fact]
    public void GenericArray_IsGenericArrayType_DetectsCorrectly()
    {
        Assert.True(GenericArrayCreationRewriter.IsGenericArrayType("Map.Entry<K,V>[]"));
        Assert.True(GenericArrayCreationRewriter.IsGenericArrayType("SimpleEntry<A,B>[5]"));
        Assert.False(GenericArrayCreationRewriter.IsGenericArrayType("String[]"));
        Assert.False(GenericArrayCreationRewriter.IsGenericArrayType("ArrayList<String>"));
        Assert.False(GenericArrayCreationRewriter.IsGenericArrayType("int[10]"));
    }

    // ═══════════════════════════════════════════════════════════
    //  Integration: all rewriters run in pipeline
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void AllRewriters_RunWithoutErrors_OnSimpleCompilationUnit()
    {
        // Verify the built-in rewriters can run on a typical compilation unit without crashing
        var cu = BuildCompilationUnit(
            new JavaVariableDeclarationStatement
            {
                Type = "ArrayList<String>",
                Name = "list",
                Initializer = new JavaNewExpression { Type = "ArrayList<String>" },
            },
            new JavaExpressionStatement
            {
                Expression = new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression("list"),
                    MethodName = "add",
                    Arguments = { new JavaLiteralExpression("\"hello\"") },
                },
            });

        // Run all built-in rewriters (same as SingleFileJavaEmitPass.RunBuiltInRewriters)
        new OperatorPrecedenceRewriter().VisitCompilationUnit(cu);
        new MapEntryTypeRewriter().VisitCompilationUnit(cu);
        new MemberwiseCloneRewriter().VisitCompilationUnit(cu);
        new MathMethodRewriter().VisitCompilationUnit(cu);
        new DelegateInvocationRewriter().VisitCompilationUnit(cu);
        new GenericArrayCreationRewriter().VisitCompilationUnit(cu);

        // Should generate valid code
        var code = cu.ToString("");
        Assert.Contains("ArrayList<String>", code);
        Assert.Contains("list.add", code);
    }

    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    private static JavaCompilationUnit BuildCompilationUnit(params JavaStatement[] statements)
    {
        var method = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Name = "test",
            StructuredBody = new JavaMethodBody(),
        };
        foreach (var stmt in statements)
            method.StructuredBody.Statements.Add(stmt);

        var classDecl = new JavaClassDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Name = "TestClass",
        };
        classDecl.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(classDecl);
        return cu;
    }
}
