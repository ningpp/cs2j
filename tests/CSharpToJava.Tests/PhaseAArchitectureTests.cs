using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Phase A architecture improvements:
/// - L1.1 ScopeTracker (scope-aware StreamLocalVariables)
/// - L1.4 Lambda Capture Registry
/// - L2.1 IR node extensions (HolderInfo, CapturedVariables, ResolvedType)
/// - L3.1 IR validation rewriters (CS2J5001, CS2J5002, CS2J5003)
/// - L3.2 Variable name deduplication rewriter
/// </summary>
public class PhaseAArchitectureTests
{
    // ═══════════════════════════════════════════════════════════
    // L1.1 ScopeTracker
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ScopeTracker_StreamVarRemovedOnPopScope()
    {
        var state = new MethodConversionState();
        state.PushScope();   // depth 1
        state.AddStreamVariable("pts");
        Assert.Contains("pts", state.StreamLocalVariables);
        state.PopScope();    // depth 0
        Assert.DoesNotContain("pts", state.StreamLocalVariables);
    }

    [Fact]
    public void ScopeTracker_StreamVarInParentScope_SurvivesChildPop()
    {
        var state = new MethodConversionState();
        state.PushScope();   // depth 1
        state.AddStreamVariable("stream1");
        state.PushScope();   // depth 2
        state.AddStreamVariable("stream2");
        Assert.Contains("stream1", state.StreamLocalVariables);
        Assert.Contains("stream2", state.StreamLocalVariables);
        state.PopScope();    // depth 1 — stream2 removed
        Assert.Contains("stream1", state.StreamLocalVariables);
        Assert.DoesNotContain("stream2", state.StreamLocalVariables);
        state.PopScope();    // depth 0 — stream1 removed
        Assert.DoesNotContain("stream1", state.StreamLocalVariables);
    }

    [Fact]
    public void ScopeTracker_SiblingScopes_NoLeak()
    {
        var state = new MethodConversionState();
        // First sibling block
        state.PushScope();
        state.AddStreamVariable("pts");
        state.PopScope();
        // Second sibling block — "pts" should NOT be in scope
        state.PushScope();
        Assert.DoesNotContain("pts", state.StreamLocalVariables);
        state.PopScope();
    }

    [Fact]
    public void ScopeTracker_ResetClearsAll()
    {
        var state = new MethodConversionState();
        state.PushScope();
        state.AddStreamVariable("x");
        state.Reset();
        Assert.Empty(state.StreamLocalVariables);
        Assert.Equal(0, state.ScopeDepth);
    }

    [Fact]
    public void ScopeTracker_StreamVarInSiblingScope_ForeachNotCollected()
    {
        // Integration test: same variable name in sibling scopes should not cause
        // the second usage (array type) to be treated as a stream.
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Point { public int X; public int Y; }
class Sample
{
    void Test(IEnumerable<Point> source, Point[] arr)
    {
        {
            var pts = source.Where(p => p.X > 0);
        }
        {
            Point[] pts = arr;
            foreach (var p in pts) { }
        }
    }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("pts.collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════
    // L1.4 Lambda Capture Registry
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CaptureRegistry_RegisterAndRetrieve()
    {
        var state = new MethodConversionState();
        var captures = new List<MethodConversionState.CaptureInfo>
        {
            new("x", "int", true),
            new("name", "String", false),
        };
        state.RegisterLambdaCaptures("lambda_1", captures);

        Assert.True(state.TryGetLambdaCaptures("lambda_1", out var retrieved));
        Assert.Equal(2, retrieved.Count);
        Assert.Equal("x", retrieved[0].VarName);
        Assert.True(retrieved[0].IsMutable);
    }

    [Fact]
    public void CaptureRegistry_UnknownKey_ReturnsFalse()
    {
        var state = new MethodConversionState();
        Assert.False(state.TryGetLambdaCaptures("nonexistent", out _));
    }

    [Fact]
    public void CaptureRegistry_ClearedOnReset()
    {
        var state = new MethodConversionState();
        state.RegisterLambdaCaptures("lambda_1", new List<MethodConversionState.CaptureInfo>());
        state.Reset();
        Assert.False(state.TryGetLambdaCaptures("lambda_1", out _));
    }

    // ═══════════════════════════════════════════════════════════
    // L2.1 IR Node Extensions
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void JavaVariableDeclaration_HolderInfo_RoundTrip()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "IntHolder",
            Name = "_xHolder1",
            Initializer = new JavaRawExpression("new IntHolder()"),
            HolderInfo = ("IntHolder", "new IntHolder()"),
        };
        Assert.NotNull(decl.HolderInfo);
        Assert.Equal("IntHolder", decl.HolderInfo.Value.HolderType);
        // Should still render normally
        var code = decl.ToString("");
        Assert.Contains("IntHolder _xHolder1 = new IntHolder();", code);
    }

    [Fact]
    public void JavaVariableDeclaration_ResolvedInitializerType()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "var",
            Name = "x",
            Initializer = new JavaLiteralExpression("42"),
            ResolvedInitializerType = "int",
        };
        Assert.Equal("int", decl.ResolvedInitializerType);
    }

    [Fact]
    public void JavaLambdaExpression_CapturedVariables()
    {
        var lambda = new JavaLambdaExpression();
        lambda.Parameters.Add("x");
        lambda.CapturedVariables.Add(("counter", "int", true));
        lambda.CapturedVariables.Add(("name", "String", false));
        lambda.ExpressionBody = new JavaRawExpression("counter[0]++");

        Assert.Equal(2, lambda.CapturedVariables.Count);
        Assert.True(lambda.CapturedVariables[0].IsMutable);
        // Rendering should work as before (CapturedVariables don't affect output)
        var code = lambda.ToInlineString();
        Assert.Contains("x -> counter[0]++", code);
    }

    [Fact]
    public void JavaRawExpression_ResolvedType()
    {
        var expr = new JavaRawExpression("list.size()", "int");
        Assert.Equal("int", expr.ResolvedType);
        Assert.Equal("list.size()", expr.ToInlineString());
    }

    // ═══════════════════════════════════════════════════════════
    // L3.1 IR Validation Rewriters
    // ═══════════════════════════════════════════════════════════

    // ── CS2J5001: Variable reference validation ──────────────

    [Fact]
    public void VarRefValidation_DeclaredVariable_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaVariableReferenceValidationRewriter(diagnostics);
        var cu = CreateCuWithMethod(new JavaBlockStatement
        {
            Statements =
            {
                new JavaVariableDeclarationStatement { Type = "int", Name = "x", Initializer = new JavaLiteralExpression("0") },
                new JavaExpressionStatement(new JavaIdentifierExpression { Name = "x" }),
            }
        });
        rewriter.VisitCompilationUnit(cu);
        Assert.DoesNotContain(diagnostics.Messages, d => d.Code == "CS2J5001");
    }

    [Fact]
    public void VarRefValidation_UndeclaredVariable_EmitsCS2J5001()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaVariableReferenceValidationRewriter(diagnostics);
        var cu = CreateCuWithMethod(new JavaBlockStatement
        {
            Statements =
            {
                new JavaExpressionStatement(new JavaIdentifierExpression { Name = "undeclaredVar" }),
            }
        });
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains(diagnostics.Messages, d => d.Code == "CS2J5001" && d.Message.Contains("undeclaredVar"));
    }

    [Fact]
    public void VarRefValidation_FieldAccess_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaVariableReferenceValidationRewriter(diagnostics);

        var clazz = new JavaClassDeclaration { Name = "MyClass" };
        clazz.Fields.Add(new JavaFieldDeclaration { Type = "int", Name = "count" });
        var method = new JavaMethodDeclaration { ReturnType = "void", Name = "test" };
        method.StructuredBody = new JavaMethodBody();
        method.StructuredBody.Statements.Add(
            new JavaExpressionStatement(new JavaIdentifierExpression { Name = "count" }));
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.DoesNotContain(diagnostics.Messages, d => d.Code == "CS2J5001");
    }

    [Fact]
    public void VarRefValidation_ParameterAccess_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaVariableReferenceValidationRewriter(diagnostics);

        var clazz = new JavaClassDeclaration { Name = "MyClass" };
        var method = new JavaMethodDeclaration { ReturnType = "void", Name = "test" };
        method.Parameters.Add(new JavaParameter("String", "input"));
        method.StructuredBody = new JavaMethodBody();
        method.StructuredBody.Statements.Add(
            new JavaExpressionStatement(new JavaIdentifierExpression { Name = "input" }));
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.DoesNotContain(diagnostics.Messages, d => d.Code == "CS2J5001");
    }

    [Fact]
    public void VarRefValidation_WellKnownIdentifiers_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaVariableReferenceValidationRewriter(diagnostics);
        var cu = CreateCuWithMethod(new JavaBlockStatement
        {
            Statements =
            {
                new JavaExpressionStatement(new JavaIdentifierExpression { Name = "Math" }),
                new JavaExpressionStatement(new JavaIdentifierExpression { Name = "System" }),
                new JavaExpressionStatement(new JavaIdentifierExpression { Name = "null" }),
            }
        });
        rewriter.VisitCompilationUnit(cu);
        Assert.DoesNotContain(diagnostics.Messages, d => d.Code == "CS2J5001");
    }

    // ── CS2J5002: Static context validation ──────────────────

    [Fact]
    public void StaticContext_ClassTypeParam_InStaticMethod_EmitsCS2J5002()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaStaticContextValidationRewriter(diagnostics);

        var clazz = new JavaClassDeclaration { Name = "Container" };
        clazz.TypeParameters.Add(new JavaTypeParameter("T"));

        var method = new JavaMethodDeclaration
        {
            ReturnType = "T",
            Name = "create",
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
        };
        method.StructuredBody = new JavaMethodBody();
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains(diagnostics.Messages, d => d.Code == "CS2J5002" && d.Message.Contains("T"));
    }

    [Fact]
    public void StaticContext_MethodOwnTypeParam_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaStaticContextValidationRewriter(diagnostics);

        var clazz = new JavaClassDeclaration { Name = "Utils" };

        var method = new JavaMethodDeclaration
        {
            ReturnType = "T",
            Name = "identity",
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
        };
        method.TypeParameters.Add(new JavaTypeParameter("T"));
        method.Parameters.Add(new JavaParameter("T", "value"));
        method.StructuredBody = new JavaMethodBody();
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.DoesNotContain(diagnostics.Messages, d => d.Code == "CS2J5002");
    }

    [Fact]
    public void StaticContext_InstanceMethod_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaStaticContextValidationRewriter(diagnostics);

        var clazz = new JavaClassDeclaration { Name = "Container" };
        clazz.TypeParameters.Add(new JavaTypeParameter("T"));

        var method = new JavaMethodDeclaration
        {
            ReturnType = "T",
            Name = "getValue",
            Modifiers = JavaModifiers.Public,
        };
        method.StructuredBody = new JavaMethodBody();
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.DoesNotContain(diagnostics.Messages, d => d.Code == "CS2J5002");
    }

    // ── CS2J5003: Type parameter validation ──────────────────

    [Fact]
    public void TypeParamValidation_GenericTypeWithoutArgs_EmitsCS2J5003()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaTypeParameterValidationRewriter(diagnostics);

        var inner = new JavaClassDeclaration { Name = "Inner" };
        inner.TypeParameters.Add(new JavaTypeParameter("T"));

        var outer = new JavaClassDeclaration { Name = "Outer" };
        outer.NestedTypes.Add(inner);

        var method = new JavaMethodDeclaration { ReturnType = "void", Name = "test" };
        method.StructuredBody = new JavaMethodBody();
        method.StructuredBody.Statements.Add(
            new JavaVariableDeclarationStatement
            {
                Type = "Inner",  // Missing <T>
                Name = "item",
                Initializer = new JavaNewExpression { Type = "Inner" }
            });
        outer.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(outer);
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains(diagnostics.Messages, d => d.Code == "CS2J5003" && d.Message.Contains("Inner"));
    }

    [Fact]
    public void TypeParamValidation_GenericTypeWithArgs_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaTypeParameterValidationRewriter(diagnostics);

        var inner = new JavaClassDeclaration { Name = "Inner" };
        inner.TypeParameters.Add(new JavaTypeParameter("T"));

        var outer = new JavaClassDeclaration { Name = "Outer" };
        outer.NestedTypes.Add(inner);

        var method = new JavaMethodDeclaration { ReturnType = "void", Name = "test" };
        method.StructuredBody = new JavaMethodBody();
        method.StructuredBody.Statements.Add(
            new JavaVariableDeclarationStatement
            {
                Type = "Inner<String>",
                Name = "item",
                Initializer = new JavaNewExpression { Type = "Inner<String>" }
            });
        outer.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(outer);
        rewriter.VisitCompilationUnit(cu);
        Assert.DoesNotContain(diagnostics.Messages, d => d.Code == "CS2J5003");
    }

    // ═══════════════════════════════════════════════════════════
    // L3.2 Variable Name Deduplication Rewriter
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void VarDedup_DuplicateInSameScope_Renamed()
    {
        var rewriter = new VariableNameDeduplicationRewriter();

        var cu = CreateCuWithMethod(new JavaBlockStatement
        {
            Statements =
            {
                new JavaVariableDeclarationStatement { Type = "int", Name = "x", Initializer = new JavaLiteralExpression("1") },
                new JavaVariableDeclarationStatement { Type = "int", Name = "x", Initializer = new JavaLiteralExpression("2") },
            }
        });

        rewriter.VisitCompilationUnit(cu);
        Assert.True(rewriter.RewriteCount > 0);

        var code = cu.ToString("");
        Assert.Contains("x_1", code);
    }

    [Fact]
    public void VarDedup_NoDuplicates_NoChanges()
    {
        var rewriter = new VariableNameDeduplicationRewriter();

        var cu = CreateCuWithMethod(new JavaBlockStatement
        {
            Statements =
            {
                new JavaVariableDeclarationStatement { Type = "int", Name = "x", Initializer = new JavaLiteralExpression("1") },
                new JavaVariableDeclarationStatement { Type = "String", Name = "y", Initializer = new JavaLiteralExpression("\"hello\"") },
            }
        });

        rewriter.VisitCompilationUnit(cu);
        Assert.Equal(0, rewriter.RewriteCount);
    }

    [Fact]
    public void VarDedup_DuplicateInNestedScope_Renamed()
    {
        var rewriter = new VariableNameDeduplicationRewriter();

        var cu = CreateCuWithMethod(new JavaBlockStatement
        {
            Statements =
            {
                new JavaVariableDeclarationStatement { Type = "int", Name = "x", Initializer = new JavaLiteralExpression("1") },
                new JavaBlockStatement
                {
                    Statements =
                    {
                        new JavaVariableDeclarationStatement { Type = "int", Name = "x", Initializer = new JavaLiteralExpression("2") },
                    }
                },
            }
        });

        rewriter.VisitCompilationUnit(cu);
        Assert.True(rewriter.RewriteCount > 0);
    }

    // ═══════════════════════════════════════════════════════════
    // Test Helpers
    // ═══════════════════════════════════════════════════════════

    private static JavaCompilationUnit CreateCuWithMethod(JavaBlockStatement body)
    {
        var clazz = new JavaClassDeclaration { Name = "TestClass" };
        var method = new JavaMethodDeclaration { ReturnType = "void", Name = "test" };
        method.StructuredBody = new JavaMethodBody();
        method.StructuredBody.Statements.AddRange(body.Statements);
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);
        return cu;
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
