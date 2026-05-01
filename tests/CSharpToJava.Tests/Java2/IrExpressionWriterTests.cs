// tests/CSharpToJava.Tests/Java2/IrExpressionWriterTests.cs
using Xunit;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;

namespace CSharpToJava.Tests.Java2;

public class IrExpressionWriterTests
{
    private readonly ExpressionWriter _writer = new();

    [Fact]
    public void BinaryExpression_WrapsLowerPrecedenceLeftInParens()
    {
        var add = new IrBinaryExpression
        {
            Left = new IrLiteralExpression { Value = "1" },
            Operator = IrBinaryOp.Add,
            Right = new IrLiteralExpression { Value = "2" },
        };
        var mul = new IrBinaryExpression
        {
            Left = add,
            Operator = IrBinaryOp.Multiply,
            Right = new IrLiteralExpression { Value = "3" },
        };
        var result = _writer.Write(mul);
        Assert.Equal("(1 + 2) * 3", result);
    }

    [Fact]
    public void BinaryExpression_NoExtraParensForHighPrecLeft()
    {
        var mul = new IrBinaryExpression
        {
            Left = new IrLiteralExpression { Value = "1" },
            Operator = IrBinaryOp.Multiply,
            Right = new IrLiteralExpression { Value = "2" },
        };
        var add = new IrBinaryExpression
        {
            Left = mul,
            Operator = IrBinaryOp.Add,
            Right = new IrLiteralExpression { Value = "3" },
        };
        var result = _writer.Write(add);
        Assert.Equal("1 * 2 + 3", result);
    }

    [Fact]
    public void MethodCall_RendersCorrectly()
    {
        var call = new IrInvocationExpression
        {
            Target = new IrIdentifierExpression { Name = "obj" },
            MethodName = "toString",
        };
        Assert.Equal("obj.toString()", _writer.Write(call));
    }

    [Fact]
    public void MethodCall_WithArguments()
    {
        var call = new IrInvocationExpression
        {
            Target = new IrIdentifierExpression { Name = "list" },
            MethodName = "add",
            Arguments = { new IrLiteralExpression { Value = "42" } },
        };
        Assert.Equal("list.add(42)", _writer.Write(call));
    }

    [Fact]
    public void NewExpression_RendersCorrectly()
    {
        var ne = new IrNewExpression
        {
            TypeName = "ArrayList",
            Arguments = { new IrLiteralExpression { Value = "10" } },
        };
        Assert.Equal("new ArrayList(10)", _writer.Write(ne));
    }
}
