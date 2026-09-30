using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.IndexRange;

/// <summary>Receiver kinds of the C31 matrix (design §1.2).</summary>
internal enum ReceiverKind
{
    SzArray,
    String,
    Span,
    Memory,
    ArraySegment,
    ImmutableArray,
    Custom,
    MultiDim,
    Pointer,
    Unknown,
}

/// <summary>Analysis result for an element-access receiver.</summary>
internal readonly record struct ReceiverInfo(
    ReceiverKind Kind,
    IPropertySymbol? LengthProperty,
    ISymbol? SliceSymbol)
{
    /// <summary>Bound Length/Count property name; defaults to Length (arrays).</summary>
    public string LengthMemberName => LengthProperty?.Name ?? "Length";
}

/// <summary>
/// Classifies element-access receivers and resolves the compiler-bound
/// length property / slice member via IOperation. Read-only.
/// </summary>
internal static class ReceiverKindAnalyzer
{
    internal static ReceiverInfo Analyze(
        ElementAccessExpressionSyntax node,
        SemanticModel model)
    {
        var receiverType = model.GetTypeInfo(node.Expression).Type;
        if (receiverType == null
            || receiverType.TypeKind == TypeKind.Error
            || receiverType.TypeKind == TypeKind.Dynamic)
        {
            return new ReceiverInfo(ReceiverKind.Unknown, null, null);
        }

        if (receiverType.TypeKind == TypeKind.Pointer)
            return new ReceiverInfo(ReceiverKind.Pointer, null, null);

        if (receiverType is IArrayTypeSymbol array)
        {
            return new ReceiverInfo(
                array.Rank == 1 ? ReceiverKind.SzArray : ReceiverKind.MultiDim,
                null,
                null);
        }

        var kind = Classify(receiverType);

        IPropertySymbol? length = null;
        ISymbol? slice = null;
        if (model.GetOperation(node) is IImplicitIndexerReferenceOperation ii)
        {
            length = ii.LengthSymbol as IPropertySymbol;
            slice = ii.IndexerSymbol;
        }

        return new ReceiverInfo(kind, length, slice);
    }

    internal static ReceiverKind Classify(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
            return ReceiverKind.String;

        var name = (type as INamedTypeSymbol)?.OriginalDefinition.ToDisplayString()
                   ?? type.ToDisplayString();
        if (name is "System.Span<T>" or "System.ReadOnlySpan<T>")
            return ReceiverKind.Span;
        if (name is "System.Memory<T>" or "System.ReadOnlyMemory<T>")
            return ReceiverKind.Memory;
        if (name == "System.ArraySegment<T>")
            return ReceiverKind.ArraySegment;
        if (name == "System.Collections.Immutable.ImmutableArray<T>")
            return ReceiverKind.ImmutableArray;

        return ReceiverKind.Custom;
    }

    internal static bool IsSystemIndex(ITypeSymbol? type)
        => type != null && type.ToDisplayString() == "System.Index";

    internal static bool IsSystemRange(ITypeSymbol? type)
        => type != null && type.ToDisplayString() == "System.Range";

    internal static bool IsIndexOrRangeType(ITypeSymbol? type)
        => IsSystemIndex(type) || IsSystemRange(type);
}
