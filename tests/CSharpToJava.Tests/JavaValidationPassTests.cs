using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Phase C: Java API validation, exception check, and module dependency passes.
/// </summary>
public class JavaValidationPassTests
{
    private static readonly string JavaConfigDir = TestPaths.JavaConfigDir;

    // ──────────────────────────────────────────────────────────
    // C1. JavaApiValidationRewriter
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ApiValidation_KnownImport_NoDiagnostic()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = CreateCompilationUnit("java.util.ArrayList");
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages.Where(d => d.Code == "CS2J4001"));
    }

    [Fact]
    public void ApiValidation_UnknownImport_EmitsCS2J4001()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = CreateCompilationUnit("com.example.NonExistentType");
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains(diagnostics.Messages, d => d.Code == "CS2J4001" && d.Message.Contains("com.example.NonExistentType"));
    }

    [Fact]
    public void ApiValidation_WildcardImport_Skipped()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        cu.Imports.Add(new JavaImport("com.example", isWildcard: true));
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages);
    }

    [Fact]
    public void ApiValidation_NewExpression_KnownType_NoDiagnostic()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        var clazz = CreateClassWithMethod(
            new JavaNewExpression { Type = "ArrayList<String>" });
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages.Where(d => d.Code == "CS2J4001" || d.Code == "CS2J4003"));
    }

    [Fact]
    public void ApiValidation_NewExpression_KnownTypeButWrongArgCount_EmitsCS2J4003()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        var newExpr = new JavaNewExpression { Type = "ArrayList<String>" };
        // ArrayList has constructors with 0 args and 1 arg — use 5 args to trigger mismatch
        for (int i = 0; i < 5; i++)
            newExpr.Arguments.Add(new JavaLiteralExpression("0"));
        var clazz = CreateClassWithMethod(newExpr);
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains(diagnostics.Messages, d => d.Code == "CS2J4003");
    }

    [Fact]
    public void ApiValidation_MethodCall_KnownMethod_NoDiagnostic()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        var call = new JavaMethodCallExpression
        {
            Target = new JavaNewExpression { Type = "ArrayList<String>" },
            MethodName = "add",
        };
        call.Arguments.Add(new JavaLiteralExpression("\"hello\""));
        var clazz = CreateClassWithMethod(call);
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages.Where(d => d.Code == "CS2J4002"));
    }

    [Fact]
    public void ApiValidation_MethodCall_UnknownMethod_EmitsCS2J4002()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        var call = new JavaMethodCallExpression
        {
            Target = new JavaNewExpression { Type = "ArrayList<String>" },
            MethodName = "nonExistentMethod",
        };
        var clazz = CreateClassWithMethod(call);
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains(diagnostics.Messages, d => d.Code == "CS2J4002" && d.Message.Contains("nonExistentMethod"));
    }

    [Fact]
    public void ApiValidation_InheritedMethod_NoDiagnostic()
    {
        // ArrayList inherits toString() from Object
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        var call = new JavaMethodCallExpression
        {
            Target = new JavaNewExpression { Type = "ArrayList<String>" },
            MethodName = "toString",
        };
        var clazz = CreateClassWithMethod(call);
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages.Where(d => d.Code == "CS2J4002"));
    }

    [Fact]
    public void ApiValidation_WithoutMetadata_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaApiValidationRewriter(null, diagnostics);
        var cu = CreateCompilationUnit("com.example.NonExistent");
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages);
    }

    [Fact]
    public void ApiValidation_StaticMethodOnIdentifier_Validates()
    {
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("Integer"),
            MethodName = "parseInt",
        };
        call.Arguments.Add(new JavaLiteralExpression("\"42\""));
        var clazz = CreateClassWithMethod(call);
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages.Where(d => d.Code == "CS2J4002"));
    }

    // ──────────────────────────────────────────────────────────
    // C2. JavaExceptionCheckRewriter
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ExceptionCheck_MethodWithCheckedException_AddsThrows()
    {
        // Thread.join() throws InterruptedException (checked)
        var (rewriter, diagnostics) = CreateExceptionChecker();
        var cu = new JavaCompilationUnit();
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression("Thread"),
            MethodName = "join",
        };
        var method = new JavaMethodDeclaration
        {
            Name = "testMethod",
            ReturnType = "void",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaExpressionStatement { Expression = call });

        var clazz = new JavaClassDeclaration { Name = "TestClass" };
        clazz.Methods.Add(method);
        cu.TypeDeclarations.Add(clazz);

        rewriter.VisitCompilationUnit(cu);

        Assert.Contains("InterruptedException", method.ThrownExceptions);
    }

    [Fact]
    public void ExceptionCheck_RuntimeException_NotAdded()
    {
        // ArrayList.add() doesn't throw checked exceptions
        var (rewriter, diagnostics) = CreateExceptionChecker();
        var cu = new JavaCompilationUnit();
        var call = new JavaMethodCallExpression
        {
            Target = new JavaNewExpression { Type = "ArrayList<String>" },
            MethodName = "add",
        };
        call.Arguments.Add(new JavaLiteralExpression("\"hello\""));
        var method = new JavaMethodDeclaration
        {
            Name = "testMethod",
            ReturnType = "void",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaExpressionStatement { Expression = call });

        var clazz = new JavaClassDeclaration { Name = "TestClass" };
        clazz.Methods.Add(method);
        cu.TypeDeclarations.Add(clazz);

        rewriter.VisitCompilationUnit(cu);

        Assert.Empty(method.ThrownExceptions);
    }

    [Fact]
    public void ExceptionCheck_WithoutMetadata_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaExceptionCheckRewriter(null, diagnostics);
        var cu = new JavaCompilationUnit();
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(diagnostics.Messages);
    }

    // ──────────────────────────────────────────────────────────
    // C3. JavaModuleDependencyRewriter
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ModuleDependency_JavaUtilImport_DetectsJavaBase()
    {
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var rewriter = new JavaModuleDependencyRewriter(javaLibrary);
        var cu = CreateCompilationUnit("java.util.ArrayList");
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains("java.base", rewriter.ModuleDependencies);
    }

    [Fact]
    public void ModuleDependency_MultipleDependencies_CollectedCorrectly()
    {
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var rewriter = new JavaModuleDependencyRewriter(javaLibrary);
        var cu = new JavaCompilationUnit();
        cu.Imports.Add(new JavaImport("java.util.ArrayList"));
        cu.Imports.Add(new JavaImport("java.lang.String"));
        rewriter.VisitCompilationUnit(cu);
        Assert.Contains("java.base", rewriter.ModuleDependencies);
    }

    [Fact]
    public void ModuleDependency_WithoutMetadata_Empty()
    {
        var rewriter = new JavaModuleDependencyRewriter(null);
        var cu = CreateCompilationUnit("java.util.ArrayList");
        rewriter.VisitCompilationUnit(cu);
        Assert.Empty(rewriter.ModuleDependencies);
    }

    // ──────────────────────────────────────────────────────────
    // Pipeline integration
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ConversionPipeline_WithMetadata_RunsApiValidation()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        var list = new List<string>();
        list.Add(""hello"");
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                JavaMetadataPath = JavaConfigDir,
            },
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        // The conversion should succeed — ArrayList.add is a known method
        Assert.Contains("add", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectPipeline_WithMetadata_RunsModuleDependencyPass()
    {
        var options = CreateOptions();
        options.JavaMetadataPath = JavaConfigDir;
        var pipeline = new ProjectConversionPipeline(options);
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = @"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        var list = new List<string>();
        list.Add(""hello"");
    }
}",
            }
        });

        var primaryResult = Assert.Single(results, r => r.FileName == "Sample.java");
        Assert.True(primaryResult.Success);
        // ProjectJavaModuleDependencyPass should be in pass metrics
        Assert.Contains("ProjectJavaModuleDependencyPass", primaryResult.PassMetrics.Select(m => m.Name));
    }

    [Fact]
    public async Task ProjectPipeline_WithMetadata_DetectsModuleDependencies()
    {
        var options = CreateOptions();
        options.JavaMetadataPath = JavaConfigDir;
        var pipeline = new ProjectConversionPipeline(options);
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = @"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        var list = new List<string>();
        list.Add(""hello"");
    }
}",
            }
        });

        var primaryResult = Assert.Single(results, r => r.FileName == "Sample.java");
        Assert.True(primaryResult.Success);
        // Should detect java.base as a module dependency (java.util.ArrayList is in java.base)
        Assert.NotNull(primaryResult.JavaModuleDependencies);
        Assert.Contains("java.base", primaryResult.JavaModuleDependencies);
    }

    [Fact]
    public void ApiValidation_GenericType_StrippedCorrectly()
    {
        // Validates that generic type parameters are stripped when validating new expressions
        var (rewriter, diagnostics) = CreateApiValidator();
        var cu = new JavaCompilationUnit();
        // Using "HashMap<String, Integer>" should resolve to HashMap correctly
        var newExpr = new JavaNewExpression { Type = "HashMap<String, Integer>" };
        var clazz = CreateClassWithMethod(newExpr);
        cu.TypeDeclarations.Add(clazz);
        rewriter.VisitCompilationUnit(cu);
        // HashMap exists in java.util — no CS2J4001 diagnostic
        Assert.Empty(diagnostics.Messages.Where(d => d.Code == "CS2J4001" && d.Message.Contains("HashMap")));
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────

    private static (JavaApiValidationRewriter rewriter, DiagnosticCollector diagnostics) CreateApiValidator()
    {
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaApiValidationRewriter(javaLibrary, diagnostics);
        return (rewriter, diagnostics);
    }

    private static (JavaExceptionCheckRewriter rewriter, DiagnosticCollector diagnostics) CreateExceptionChecker()
    {
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaExceptionCheckRewriter(javaLibrary, diagnostics);
        return (rewriter, diagnostics);
    }

    private static JavaCompilationUnit CreateCompilationUnit(string importName)
    {
        var cu = new JavaCompilationUnit();
        cu.Imports.Add(new JavaImport(importName));
        return cu;
    }

    private static JavaClassDeclaration CreateClassWithMethod(JavaExpression expression)
    {
        var method = new JavaMethodDeclaration
        {
            Name = "testMethod",
            ReturnType = "void",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaExpressionStatement { Expression = expression });

        var clazz = new JavaClassDeclaration { Name = "TestClass" };
        clazz.Methods.Add(method);
        return clazz;
    }

    private static ConversionOptions CreateOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            EmitCompatibilityHelpers = false,
        };
    }
}
