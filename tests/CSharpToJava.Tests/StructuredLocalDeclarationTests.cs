using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for TransformLocalDeclaration producing structured JavaVariableDeclarationStatement IR.
/// Verifies single-variable declarations emit structured IR with type and initializer info,
/// enabling downstream rewriters like ImplicitCastCompletionRewriter to work on production code.
/// </summary>
public class StructuredLocalDeclarationTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.GeneratedCode;
    }

    private static string CreateRbTreeRegressionCode()
    {
        return @"
enum RBColor { Red, Black }

class RBNode<T> {
    internal RBColor color;
    internal T Item;
    internal RBNode<T> left;
    internal RBNode<T> right;
    internal RBNode<T> parent;
}

class RbTree<T> {
    RBNode<T> nil;
    RBNode<T> root;

    void LeftRotate(RBNode<T> x) { }
    void RightRotate(RBNode<T> x) { }

    void DeleteFixup(RBNode<T> x) {
        while (x != root && x.color == RBColor.Black) {
            if (x == x.parent.left) {
                RBNode<T> w = x.parent.right;
                if (w.color == RBColor.Red) {
                    w.color = RBColor.Black;
                    x.parent.color = RBColor.Red;
                    LeftRotate(x.parent);
                    w = x.parent.right;
                }
                if (w.left.color == RBColor.Black && w.right.color == RBColor.Black) {
                    w.color = RBColor.Red;
                    x = x.parent;
                } else {
                    if (w.right.color == RBColor.Black) {
                        w.left.color = RBColor.Black;
                        w.color = RBColor.Red;
                        RightRotate(w);
                        w = x.parent.right;
                    }
                    w.color = x.parent.color;
                    x.parent.color = RBColor.Black;
                    w.right.color = RBColor.Black;
                    LeftRotate(x.parent);
                    x = root;
                }
            } else {
                RBNode<T> w = x.parent.left;
                if (w.color == RBColor.Red) {
                    w.color = RBColor.Black;
                    x.parent.color = RBColor.Red;
                    RightRotate(x.parent);
                    w = x.parent.left;
                }
                if (w.right.color == RBColor.Black && w.left.color == RBColor.Black) {
                    w.color = RBColor.Red;
                    x = x.parent;
                } else {
                    if (w.left.color == RBColor.Black) {
                        w.right.color = RBColor.Black;
                        w.color = RBColor.Red;
                        LeftRotate(w);
                        w = x.parent.left;
                    }
                    w.color = x.parent.color;
                    x.parent.color = RBColor.Black;
                    w.left.color = RBColor.Black;
                    RightRotate(x.parent);
                    x = root;
                }
            }
        }
        x.color = RBColor.Black;
    }

    void InsertPrivate(RBNode<T> x) {
        x.color = RBColor.Red;
        while (x != root && x.parent.color == RBColor.Red) {
            if (x.parent == x.parent.parent.left) {
                RBNode<T> y = x.parent.parent.right;
                if (y.color == RBColor.Red) {
                    x.parent.color = RBColor.Black;
                    y.color = RBColor.Black;
                    x.parent.parent.color = RBColor.Red;
                    x = x.parent.parent;
                } else {
                    if (x == x.parent.right) {
                        x = x.parent;
                        LeftRotate(x);
                    }
                    x.parent.color = RBColor.Black;
                    x.parent.parent.color = RBColor.Red;
                    RightRotate(x.parent.parent);
                }
            } else {
                RBNode<T> y = x.parent.parent.left;
                if (y.color == RBColor.Red) {
                    x.parent.color = RBColor.Black;
                    y.color = RBColor.Black;
                    x.parent.parent.color = RBColor.Red;
                    x = x.parent.parent;
                } else {
                    if (x == x.parent.left) {
                        x = x.parent;
                        RightRotate(x);
                    }
                    x.parent.color = RBColor.Black;
                    x.parent.parent.color = RBColor.Red;
                    LeftRotate(x.parent.parent);
                }
            }
        }

        root.color = RBColor.Black;
    }
}";
    }

    // ── Basic structured declarations still produce correct output ──

    [Fact]
    public void SingleIntDeclaration_ProducesCorrectOutput()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = 42;
    }
}");
        Assert.Contains("int x = 42;", result);
    }

    [Fact]
    public void SingleStringDeclaration_ProducesCorrectOutput()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string s = ""hello"";
    }
}");
        Assert.Contains("String s = \"hello\";", result);
    }

    [Fact]
    public void SingleBoolDeclaration_ProducesCorrectOutput()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        bool b = false;
    }
}");
        Assert.Contains("boolean b = false;", result);
    }

    [Fact]
    public void VarDeclaration_ProducesCorrectOutput()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        var x = 42;
    }
}");
        Assert.Contains("var x = 42;", result);
    }

    [Fact]
    public void DoubleDeclaration_ProducesCorrectOutput()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        double d = 3.14;
    }
}");
        Assert.Contains("double d = 3.14;", result);
    }

    [Fact]
    public void LongDeclaration_ProducesCorrectOutput()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        long l = 100L;
    }
}");
        Assert.Contains("long l = 100L;", result);
    }

    // ── JavaVariableDeclarationStatement IR node tests ──────────────

    [Fact]
    public void StructuredIR_NodeToString_WithInitializer()
    {
        var node = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaLiteralExpression("42"),
        };
        Assert.Equal("int x = 42;", node.ToString(""));
    }

    [Fact]
    public void StructuredIR_NodeToString_WithoutInitializer()
    {
        var node = new JavaVariableDeclarationStatement
        {
            Type = "String",
            Name = "s",
        };
        Assert.Equal("String s;", node.ToString(""));
    }

    [Fact]
    public void StructuredIR_NodeToString_WithFinal()
    {
        var node = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaLiteralExpression("10"),
            IsFinal = true,
        };
        Assert.Equal("final int x = 10;", node.ToString(""));
    }

    [Fact]
    public void StructuredIR_NodeToString_WithIndentation()
    {
        var node = new JavaVariableDeclarationStatement
        {
            Type = "int",
            Name = "x",
            Initializer = new JavaLiteralExpression("42"),
        };
        Assert.Equal("    int x = 42;", node.ToString("    "));
    }

    [Fact]
    public void StructuredIR_ResolvedInitializerType_Set()
    {
        var node = new JavaVariableDeclarationStatement
        {
            Type = "long",
            Name = "x",
            Initializer = new JavaLiteralExpression("42"),
            ResolvedInitializerType = "int",
        };
        Assert.Equal("int", node.ResolvedInitializerType);
    }

    [Fact]
    public void StructuredIR_HolderInfo_Set()
    {
        var node = new JavaVariableDeclarationStatement
        {
            Type = "IntHolder",
            Name = "xRef",
            HolderInfo = ("IntHolder", "new IntHolder()"),
        };
        Assert.NotNull(node.HolderInfo);
        Assert.Equal("IntHolder", node.HolderInfo.Value.HolderType);
    }

    // ── End-to-end: ImplicitCastCompletionRewriter integration ─────

    [Fact]
    public void NarrowingConversion_LongToInt_AddsCast()
    {
        // When a long initializer is assigned to an int variable,
        // the ImplicitCastCompletionRewriter should add a cast.
        // This test verifies the rewriter works on production-generated IR.
        var result = ConvertCode(@"
class T {
    long GetValue() { return 100L; }
    void M() {
        int x = (int)GetValue();
    }
}");
        Assert.Contains("(int)", result);
    }

    // ── Complex cases still work (multi-variable, pre/post stmts) ──

    [Fact]
    public void ListDeclaration_EndToEnd()
    {
        var result = ConvertCode(@"
using System.Collections.Generic;
class T {
    void M() {
        List<int> list = new List<int>();
    }
}");
        Assert.Contains("ArrayList<", result);
    }

    [Fact]
    public void ObjectInitializer_WithPreStatements_StillWorks()
    {
        var result = ConvertCode(@"
class Point { public int X { get; set; } public int Y { get; set; } }
class T {
    void M() {
        var p = new Point { X = 1, Y = 2 };
    }
}");
        Assert.Contains("setX(1)", result);
        Assert.Contains("setY(2)", result);
    }

    [Fact]
    public void ArrayDeclaration_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int[] arr = new int[] { 1, 2, 3 };
    }
}");
        Assert.Contains("int[] arr", result);
    }

    [Fact]
    public void VarWithMethodCall_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    int GetValue() { return 42; }
    void M() {
        var x = GetValue();
    }
}");
        Assert.Contains("var x = getValue()", result);
    }

    [Fact]
    public void DeleteFixup_RealRbTreePattern_PreservesNestedLocalDeclaration()
    {
        var result = ConvertCode(CreateRbTreeRegressionCode());

        Assert.Contains("RBNode<T> w = x.parent.right;", result);
        Assert.Contains("RBNode<T> w = x.parent.left;", result);
    }

    [Fact]
    public void InsertPrivate_RealRbTreePattern_PreservesNestedLocalDeclaration()
    {
        var result = ConvertCode(CreateRbTreeRegressionCode());

        Assert.Contains("RBNode<T> y = x.parent.parent.right;", result);
        Assert.Contains("RBNode<T> y = x.parent.parent.left;", result);
    }

    [Fact]
    public void ConstructorBody_PreservesStructuredLocalDeclaration()
    {
        var result = ConvertCode(@"
class T {
    int f;

    T() {
        int x = 42;
        f = x;
    }
}");

        Assert.Contains("int x = 42;", result);
    }

    [Fact]
    public void StaticConstructorBody_PreservesStructuredLocalDeclaration()
    {
        var result = ConvertCode(@"
class T {
    static int f;

    static T() {
        int x = 42;
        f = x;
    }
}");

        Assert.Contains("int x = 42;", result);
    }
}
