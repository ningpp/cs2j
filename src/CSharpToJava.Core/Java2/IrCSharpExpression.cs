// src/CSharpToJava.Core/Java2/IrCSharpExpression.cs
namespace CSharpToJava.Core.Java2;

/// <summary>
/// C#-only expression nodes. These must NOT exist in the IR tree after Lowering completes.
/// </summary>

/// <summary>Property access: obj.Property -> LowerProperty converts to getter/setter call</summary>
public class IrCSharpPropertyAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public string PropertyName { get; set; } = "";
    public bool IsSetter { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>Indexer access: obj[index] -> LowerIndexer converts to get/set method</summary>
public class IrCSharpIndexerAccessExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public List<IrExpression> Indices { get; } = new();
    public bool IsSetter { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>Operator overload call -> LowerOperator converts to static method</summary>
public class IrCSharpOperatorCallExpression : IrExpression
{
    public IrExpression? Left { get; set; }
    public string OperatorMethodName { get; set; } = "";
    public IrExpression? Right { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>ref/out parameter passing -> LowerRefOut wraps in Holder</summary>
public class IrCSharpRefOutExpression : IrExpression
{
    public IrExpression Inner { get; set; } = null!;
    public bool IsRef { get; set; }
    public bool IsOut { get; set; }
    public bool IsReadOnlyRef { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>struct value copy -> LowerStruct converts to clone() call</summary>
public class IrCSharpStructCopyExpression : IrExpression
{
    public IrExpression Source { get; set; } = null!;
    public string StructType { get; set; } = "";
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>Delegate creation -> LowerDelegate converts to anonymous class/lambda</summary>
public class IrCSharpDelegateCreationExpression : IrExpression
{
    public string DelegateType { get; set; } = "";
    public IrExpression Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>Event subscribe/raise -> LowerEvent converts to listener pattern</summary>
public class IrCSharpEventExpression : IrExpression
{
    public IrExpression Target { get; set; } = null!;
    public string EventName { get; set; } = "";
    public bool IsSubscribe { get; set; }
    public bool IsUnsubscribe { get; set; }
    public bool IsRaise { get; set; }
    public IrExpression? Handler { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>Pattern match -> LowerPatternMatch expands</summary>
public class IrCSharpPatternExpression : IrExpression
{
    public IrExpression Subject { get; set; } = null!;
    public string PatternKind { get; set; } = "";
    public string? MatchedType { get; set; }
    public string? PatternVariable { get; set; }
    public List<(string Property, IrExpression Value)> PropertyChecks { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}
