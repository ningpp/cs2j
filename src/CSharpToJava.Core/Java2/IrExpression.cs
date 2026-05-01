// src/CSharpToJava.Core/Java2/IrExpression.cs
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Java2;

// ─── Binary operators ───

public enum IrBinaryOp
{
    Add, Subtract, Multiply, Divide, Modulo,
    LogicalAnd, LogicalOr,
    BitwiseAnd, BitwiseOr, BitwiseXor,
    ShiftLeft, ShiftRight, UnsignedShiftRight,
    Equals, NotEquals,
    LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual,
    NullCoalescing,
}

// ─── Unary operators ───

public enum IrUnaryOp
{
    Plus, Minus, Not, BitwiseNot, PreIncrement, PreDecrement, PostIncrement, PostDecrement,
}

// ─── Assignment operators ───

public enum IrAssignmentOp
{
    Assign,
    AddAssign, SubtractAssign, MultiplyAssign, DivideAssign,
    AndAssign, OrAssign, XorAssign,
    LeftShiftAssign, RightShiftAssign,
}

/// <summary>
/// Shared expression nodes that exist in both C#-flavored HIR and Pure Java IR.
/// These are NEVER raw strings — every field is a structured IrNode.
/// </summary>

public class IrLiteralExpression : IrExpression
{
    public string Value { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrIdentifierExpression : IrExpression
{
    public string Name { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrThisExpression : IrExpression
{
    public bool IsSuper { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrBinaryExpression : IrExpression
{
    public IrExpression Left { get; set; } = null!;
    public IrBinaryOp Operator { get; set; }
    public IrExpression Right { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrUnaryExpression : IrExpression
{
    public IrUnaryOp Operator { get; set; }
    public IrExpression Operand { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrConditionalExpression : IrExpression
{
    public IrExpression Condition { get; set; } = null!;
    public IrExpression WhenTrue { get; set; } = null!;
    public IrExpression WhenFalse { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrCastExpression : IrExpression
{
    public string TargetType { get; set; } = "";
    public IrExpression Expression { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrNewExpression : IrExpression
{
    public string TypeName { get; set; } = "";
    public List<IrExpression> Arguments { get; } = new();
    public string? ArrayInitializer { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrMemberAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public string MemberName { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrInvocationExpression : IrExpression
{
    public IrExpression? Target { get; set; }
    public string MethodName { get; set; } = "";
    public List<IrExpression> Arguments { get; } = new();
    public List<string> TypeArguments { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrAssignmentExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public IrAssignmentOp Operator { get; set; } = IrAssignmentOp.Assign;
    public IrExpression Value { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrArrayAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public IrExpression Index { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrLambdaExpression : IrExpression
{
    public List<IrLambdaParameter> Parameters { get; } = new();
    public IrExpression? ExpressionBody { get; set; }
    public IrNode? BlockBody { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrLambdaParameter
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
}

public class IrInstanceOfExpression : IrExpression
{
    public IrExpression Expression { get; set; } = null!;
    public string TypeName { get; set; } = "";
    public string? PatternVariable { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public abstract class IrExpression : IrNode
{
    public abstract void Accept(IIrVisitor visitor);
    public abstract T Accept<T>(IIrVisitor<T> visitor);
}
