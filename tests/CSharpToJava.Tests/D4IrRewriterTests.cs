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
    //  Error 10: IntStreamBoxedRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IntStream_ArraysStreamFlatMap_InsertsBoxed()
    {
        var rewriter = new IntStreamBoxedRewriter();

        // Arrays.stream(intArr).flatMap(...)
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("Arrays"),
                MethodName = "stream",
                Arguments = { new JavaIdentifierExpression("intArr") },
            },
            MethodName = "flatMap",
        };
        call.Arguments.Add(new JavaIdentifierExpression("mapper"));

        rewriter.VisitMethodCallExpression(call);

        // Should become: Arrays.stream(intArr).boxed().flatMap(mapper)
        Assert.IsType<JavaMethodCallExpression>(call.Target);
        var boxedCall = (JavaMethodCallExpression)call.Target!;
        Assert.Equal("boxed", boxedCall.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void IntStream_IntStreamRange_Map_InsertsBoxed()
    {
        var rewriter = new IntStreamBoxedRewriter();

        // IntStream.range(0, n).map(...)
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("IntStream"),
                MethodName = "range",
                Arguments = { new JavaLiteralExpression("0"), new JavaIdentifierExpression("n") },
            },
            MethodName = "map",
        };
        call.Arguments.Add(new JavaIdentifierExpression("fn"));

        rewriter.VisitMethodCallExpression(call);

        var boxedCall = (JavaMethodCallExpression)call.Target!;
        Assert.Equal("boxed", boxedCall.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void IntStream_RegularStreamMap_Unchanged()
    {
        var rewriter = new IntStreamBoxedRewriter();

        // list.stream().map(...)  — not IntStream, should be unchanged
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("list"),
                MethodName = "stream",
            },
            MethodName = "map",
        };
        call.Arguments.Add(new JavaIdentifierExpression("fn"));

        rewriter.VisitMethodCallExpression(call);

        // Target should still be list.stream(), no boxed() inserted
        var streamCall = (JavaMethodCallExpression)call.Target!;
        Assert.Equal("stream", streamCall.MethodName);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 11/24e: ArrayIterableConversionRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ArrayIterable_SpliteratorOnArray_BecomesArraysSpliterator()
    {
        var rewriter = new ArrayIterableConversionRewriter();

        // graphs.spliterator() → Arrays.spliterator(graphs)
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("graphs"),
            MethodName = "spliterator",
        };

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("spliterator", call.MethodName);
        Assert.IsType<JavaIdentifierExpression>(call.Target);
        Assert.Equal("Arrays", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Single(call.Arguments);
        Assert.Equal("graphs", ((JavaIdentifierExpression)call.Arguments[0]).Name);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void ArrayIterable_StreamOnArray_BecomesArraysStream()
    {
        var rewriter = new ArrayIterableConversionRewriter();

        // nodeItems.stream() → Arrays.stream(nodeItems)
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("nodeItems"),
            MethodName = "stream",
        };

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("stream", call.MethodName);
        Assert.Equal("Arrays", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Single(call.Arguments);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void ArrayIterable_StreamOnCollection_Unchanged()
    {
        var rewriter = new ArrayIterableConversionRewriter();

        // list.stream() — "list" doesn't end in "s" or array-like suffix
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("collection"),
            MethodName = "stream",
        };

        rewriter.VisitMethodCallExpression(call);

        // Should be unchanged — "collection" doesn't match array heuristic
        Assert.Equal("collection", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Empty(call.Arguments);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    [Fact]
    public void ArrayIterable_StreamOnNewArrayExpr_BecomesArraysStream()
    {
        var rewriter = new ArrayIterableConversionRewriter();

        // (new int[5]).stream() → Arrays.stream(new int[5])
        var call = new JavaMethodCallExpression
        {
            Target = new JavaNewExpression { Type = "int[5]" },
            MethodName = "stream",
        };

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("Arrays", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Single(call.Arguments);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 15: CollectStreamRoundtripRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CollectStream_RoundTrip_RemovesBoth()
    {
        var rewriter = new CollectStreamRoundtripRewriter();

        // list.stream().filter(p).collect(toList()).stream()
        // Should become: list.stream().filter(p)
        var filterCall = new JavaMethodCallExpression
        {
            Target = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression("list"),
                MethodName = "stream",
            },
            MethodName = "filter",
        };
        filterCall.Arguments.Add(new JavaIdentifierExpression("p"));

        var collectCall = new JavaMethodCallExpression
        {
            Target = filterCall,
            MethodName = "collect",
        };
        collectCall.Arguments.Add(new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("Collectors"),
            MethodName = "toList",
        });

        var streamCall = new JavaMethodCallExpression
        {
            Target = collectCall,
            MethodName = "stream",
        };

        var result = rewriter.VisitExpression(streamCall);

        // Should return the filter call directly (stripping collect + stream)
        Assert.IsType<JavaMethodCallExpression>(result);
        var resultCall = (JavaMethodCallExpression)result;
        Assert.Equal("filter", resultCall.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void CollectStream_NoRoundTrip_Unchanged()
    {
        var rewriter = new CollectStreamRoundtripRewriter();

        // list.stream().filter(p).map(fn) — no collect/stream round-trip
        var call = new JavaMethodCallExpression
        {
            Target = new JavaMethodCallExpression
            {
                Target = new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression("list"),
                    MethodName = "stream",
                },
                MethodName = "filter",
                Arguments = { new JavaIdentifierExpression("p") },
            },
            MethodName = "map",
        };
        call.Arguments.Add(new JavaIdentifierExpression("fn"));

        var result = rewriter.VisitExpression(call);

        Assert.IsType<JavaMethodCallExpression>(result);
        Assert.Equal("map", ((JavaMethodCallExpression)result).MethodName);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  Error 17: StopwatchApiRewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Stopwatch_Frequency_BecomesNanoPrecisionLiteral()
    {
        var rewriter = new StopwatchApiRewriter();

        // StopwatchHelper.Frequency → 1_000_000_000L
        var memberAccess = new JavaMemberAccessExpression
        {
            Target = new JavaIdentifierExpression("StopwatchHelper"),
            MemberName = "Frequency",
        };

        var result = rewriter.VisitExpression(memberAccess);

        Assert.IsType<JavaLiteralExpression>(result);
        Assert.Equal("1_000_000_000L", ((JavaLiteralExpression)result).Value);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void Stopwatch_GetTimestamp_BecomesSystemNanoTime()
    {
        var rewriter = new StopwatchApiRewriter();

        // StopwatchHelper.getTimestamp() → System.nanoTime()
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("StopwatchHelper"),
            MethodName = "getTimestamp",
        };

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("System", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Equal("nanoTime", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void Stopwatch_PascalCaseGetTimestamp_AlsoRewrites()
    {
        var rewriter = new StopwatchApiRewriter();

        // StopwatchHelper.GetTimestamp() → System.nanoTime()
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("Stopwatch"),
            MethodName = "GetTimestamp",
        };

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("System", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Equal("nanoTime", call.MethodName);
        Assert.Equal(1, rewriter.RewriteCount);
    }

    [Fact]
    public void Stopwatch_RegularMethodCall_Unchanged()
    {
        var rewriter = new StopwatchApiRewriter();

        // StopwatchHelper.start() — not a rewrite target
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("StopwatchHelper"),
            MethodName = "start",
        };

        rewriter.VisitMethodCallExpression(call);

        Assert.Equal("StopwatchHelper", ((JavaIdentifierExpression)call.Target!).Name);
        Assert.Equal("start", call.MethodName);
        Assert.Equal(0, rewriter.RewriteCount);
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
        new IntStreamBoxedRewriter().VisitCompilationUnit(cu);
        new ArrayIterableConversionRewriter().VisitCompilationUnit(cu);
        new CollectStreamRoundtripRewriter().VisitCompilationUnit(cu);
        new StopwatchApiRewriter().VisitCompilationUnit(cu);

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
