// src/CSharpToJava.Core/Java2/IrNode.cs
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Java2;

/// <summary>
/// Base class for all Java IR nodes.
/// Does NOT contain ToString — CodeGen is separate.
/// </summary>
public abstract class IrNode
{
    /// <summary>
    /// Optional Roslyn symbol bound to this node. Used by Lowering passes
    /// for type-level decisions without string matching.
    /// </summary>
    public ISymbol? Symbol { get; set; }

    /// <summary>
    /// Optional resolved Java type for this expression node.
    /// </summary>
    public string? JavaType { get; set; }
}

public interface IIrVisitor<out T>
{
    T Visit(IrNode node);
}

public interface IIrVisitor
{
    void Visit(IrNode node);
}
