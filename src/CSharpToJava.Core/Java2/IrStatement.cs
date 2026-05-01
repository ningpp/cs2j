// src/CSharpToJava.Core/Java2/IrStatement.cs
namespace CSharpToJava.Core.Java2;

public abstract class IrStatement : IrNode
{
    public string? LeadingComment { get; set; }
    public abstract void Accept(IIrVisitor visitor);
    public abstract T Accept<T>(IIrVisitor<T> visitor);
}

public class IrBlockStatement : IrStatement
{
    public List<IrStatement> Statements { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrExpressionStatement : IrStatement
{
    public IrExpression Expression { get; set; } = null!;
    public IrExpressionStatement() { }
    public IrExpressionStatement(IrExpression expression) { Expression = expression; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrVariableDeclarationStatement : IrStatement
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFinal { get; set; }
    public IrExpression? Initializer { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrReturnStatement : IrStatement
{
    public IrExpression? Expression { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrIfStatement : IrStatement
{
    public IrExpression Condition { get; set; } = null!;
    public IrStatement ThenBody { get; set; } = null!;
    public IrStatement? ElseBody { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrForEachStatement : IrStatement
{
    public string VariableType { get; set; } = "";
    public string VariableName { get; set; } = "";
    public IrExpression Collection { get; set; } = null!;
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrForStatement : IrStatement
{
    public string? Initializer { get; set; }
    public IrExpression? Condition { get; set; }
    public string? Increment { get; set; }
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrWhileStatement : IrStatement
{
    public IrExpression Condition { get; set; } = null!;
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrDoWhileStatement : IrStatement
{
    public IrExpression Condition { get; set; } = null!;
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrTryCatchStatement : IrStatement
{
    public List<string> Resources { get; } = new();
    public IrBlockStatement TryBody { get; set; } = new();
    public List<IrCatchClause> CatchClauses { get; } = new();
    public IrBlockStatement? FinallyBody { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrCatchClause
{
    public string ExceptionType { get; set; } = "Exception";
    public string? VariableName { get; set; }
    public IrBlockStatement Body { get; set; } = new();
}

public class IrThrowStatement : IrStatement
{
    public IrExpression Expression { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrSwitchStatement : IrStatement
{
    public IrExpression Expression { get; set; } = null!;
    public List<IrSwitchSection> Sections { get; } = new();
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrSwitchSection
{
    public List<string> Labels { get; } = new();
    public List<IrStatement> Statements { get; } = new();
}

public class IrBreakStatement : IrStatement
{
    public string? Label { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

public class IrContinueStatement : IrStatement
{
    public string? Label { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

// ─── C#-only statements (must not exist after Lowering) ───

/// <summary>yield return x; -> LowerYield converts to state machine</summary>
public class IrCSharpYieldReturnStatement : IrStatement
{
    public IrExpression? Expression { get; set; }
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>yield break; -> LowerYield converts to state machine</summary>
public class IrCSharpYieldBreakStatement : IrStatement
{
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>using (var x = ...) { ... } -> LowerUsing converts to try-finally</summary>
public class IrCSharpUsingStatement : IrStatement
{
    public IrVariableDeclarationStatement? Resource { get; set; }
    public IrExpression? ResourceExpression { get; set; }
    public IrStatement Body { get; set; } = null!;
    public override void Accept(IIrVisitor visitor) => visitor.Visit(this);
    public override T Accept<T>(IIrVisitor<T> visitor) => visitor.Visit(this);
}
