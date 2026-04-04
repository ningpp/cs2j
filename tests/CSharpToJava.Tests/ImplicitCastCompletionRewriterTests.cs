using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;
using Xunit;

namespace CSharpToJava.Tests;

public class ImplicitCastCompletionRewriterTests
{
    // ── NarrowingCast helper tests ──────────────────────────────────

    [Theory]
    [InlineData("long", "int", true)]
    [InlineData("long", "short", true)]
    [InlineData("long", "byte", true)]
    [InlineData("double", "float", true)]
    [InlineData("double", "int", true)]
    [InlineData("float", "int", true)]
    [InlineData("int", "short", true)]
    [InlineData("int", "byte", true)]
    public void NeedsNarrowingCast_NarrowingPairs_ReturnsTrue(string source, string target, bool expected)
    {
        Assert.Equal(expected, ImplicitCastCompletionRewriter.NeedsNarrowingCast(source, target));
    }

    [Theory]
    [InlineData("int", "long", false)]   // widening — no cast needed
    [InlineData("int", "int", false)]    // same type
    [InlineData("float", "double", false)] // widening
    [InlineData("String", "int", false)]   // not numeric
    public void NeedsNarrowingCast_WideningOrSame_ReturnsFalse(string source, string target, bool expected)
    {
        Assert.Equal(expected, ImplicitCastCompletionRewriter.NeedsNarrowingCast(source, target));
    }

    [Theory]
    [InlineData("Integer", "int")]
    [InlineData("Long", "long")]
    [InlineData("Double", "double")]
    [InlineData("Float", "float")]
    [InlineData("Boolean", "boolean")]
    [InlineData("Character", "char")]
    [InlineData("Short", "short")]
    [InlineData("Byte", "byte")]
    [InlineData("int", "int")]       // primitives stay as-is
    [InlineData("String", "String")] // non-wrapper stays as-is
    public void NormalizeType_UnboxesWrappers(string input, string expected)
    {
        Assert.Equal(expected, ImplicitCastCompletionRewriter.NormalizeType(input));
    }

    // ── Structured IR rewriter tests ────────────────────────────────

    [Fact]
    public void VisitVariableDeclaration_LongToInt_InsertsCast()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaRawExpression("getValue()", "long"),
            ResolvedInitializerType = "long",
        };

        var block = new JavaBlockStatement();
        block.Statements.Add(decl);

        var method = CreateMethodWithBlock(block);
        var cu = CreateCompilationUnit(method);

        new ImplicitCastCompletionRewriter().VisitCompilationUnit(cu);

        // The initializer should now be wrapped in a cast
        var resultDecl = GetFirstVarDecl(cu);
        Assert.Contains("(int)", resultDecl.Initializer!.ToInlineString());
    }

    [Fact]
    public void VisitVariableDeclaration_SameType_NoCastInserted()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaRawExpression("getValue()", "int"),
            ResolvedInitializerType = "int",
        };

        var block = new JavaBlockStatement();
        block.Statements.Add(decl);

        var method = CreateMethodWithBlock(block);
        var cu = CreateCompilationUnit(method);

        new ImplicitCastCompletionRewriter().VisitCompilationUnit(cu);

        var resultDecl = GetFirstVarDecl(cu);
        Assert.Equal("getValue()", resultDecl.Initializer!.ToInlineString());
    }

    [Fact]
    public void VisitVariableDeclaration_BoxedWrapperToNarrowed_DetectsNarrowing()
    {
        // Long → int: should detect narrowing even through wrapper name
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaRawExpression("getLongValue()"),
            ResolvedInitializerType = "Long",
        };

        var block = new JavaBlockStatement();
        block.Statements.Add(decl);

        var method = CreateMethodWithBlock(block);
        var cu = CreateCompilationUnit(method);

        new ImplicitCastCompletionRewriter().VisitCompilationUnit(cu);

        var resultDecl = GetFirstVarDecl(cu);
        Assert.Contains("(int)", resultDecl.Initializer!.ToInlineString());
    }

    [Fact]
    public void VisitVariableDeclaration_NoResolvedType_Skipped()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaRawExpression("someExpr"),
            // ResolvedInitializerType not set
        };

        var block = new JavaBlockStatement();
        block.Statements.Add(decl);

        var method = CreateMethodWithBlock(block);
        var cu = CreateCompilationUnit(method);

        new ImplicitCastCompletionRewriter().VisitCompilationUnit(cu);

        var resultDecl = GetFirstVarDecl(cu);
        Assert.Equal("someExpr", resultDecl.Initializer!.ToInlineString());
    }

    [Fact]
    public void VisitVariableDeclaration_DoubleToFloat_InsertsCast()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "float",
            Name = "f",
            Initializer = new JavaRawExpression("getDouble()", "double"),
            ResolvedInitializerType = "double",
        };

        var block = new JavaBlockStatement();
        block.Statements.Add(decl);

        var method = CreateMethodWithBlock(block);
        var cu = CreateCompilationUnit(method);

        new ImplicitCastCompletionRewriter().VisitCompilationUnit(cu);

        var resultDecl = GetFirstVarDecl(cu);
        Assert.Contains("(float)", resultDecl.Initializer!.ToInlineString());
    }

    [Fact]
    public void VisitVariableDeclaration_AlreadyCast_NoDoubleCast()
    {
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaRawExpression("(int) (getValue())", "long"),
            ResolvedInitializerType = "long",
        };

        var block = new JavaBlockStatement();
        block.Statements.Add(decl);

        var method = CreateMethodWithBlock(block);
        var cu = CreateCompilationUnit(method);

        new ImplicitCastCompletionRewriter().VisitCompilationUnit(cu);

        var resultDecl = GetFirstVarDecl(cu);
        // Should not double-cast
        Assert.Equal("(int) (getValue())", resultDecl.Initializer!.ToInlineString());
    }

    [Fact]
    public void VisitVariableDeclaration_WideningConversion_NoCast()
    {
        // int → long: widening, no cast needed
        var decl = new JavaVariableDeclarationStatement
        {
            Type = "long",
            Name = "x",
            Initializer = new JavaRawExpression("getInt()", "int"),
            ResolvedInitializerType = "int",
        };

        var block = new JavaBlockStatement();
        block.Statements.Add(decl);

        var method = CreateMethodWithBlock(block);
        var cu = CreateCompilationUnit(method);

        new ImplicitCastCompletionRewriter().VisitCompilationUnit(cu);

        var resultDecl = GetFirstVarDecl(cu);
        Assert.Equal("getInt()", resultDecl.Initializer!.ToInlineString());
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static JavaCompilationUnit CreateCompilationUnit(JavaMethodDeclaration method)
    {
        var cu = new JavaCompilationUnit { Package = "test" };
        var type = new JavaClassDeclaration { Name = "Test" };
        type.Methods.Add(method);
        cu.TypeDeclarations.Add(type);
        return cu;
    }

    private static JavaMethodDeclaration CreateMethodWithBlock(JavaBlockStatement block)
    {
        var method = new JavaMethodDeclaration
        {
            Name = "test",
            ReturnType = "void",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(block);
        return method;
    }

    private static JavaVariableDeclarationStatement GetFirstVarDecl(JavaCompilationUnit cu)
    {
        var cls = (JavaClassDeclaration)cu.TypeDeclarations[0];
        var method = cls.Methods[0];
        var block = (JavaBlockStatement)method.StructuredBody!.Statements[0];
        return (JavaVariableDeclarationStatement)block.Statements[0];
    }
}
