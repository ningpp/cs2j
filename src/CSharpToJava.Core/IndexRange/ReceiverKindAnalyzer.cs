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

/// <summary>
/// How the compiler bound an element-access site carrying a <c>^</c>/<c>..</c>
/// argument.
/// </summary>
internal enum ReceiverBinding
{
    /// <summary>
    /// Built-in pattern downgrade (<see cref="IImplicitIndexerReferenceOperation"/>):
    /// the compiler itself resolved the Length/Count and int indexer/Slice
    /// members.
    /// </summary>
    Pattern,

    /// <summary>Bound to an explicit user-defined <c>this[System.Index]</c>.</summary>
    ExplicitIndex,

    /// <summary>Bound to an explicit user-defined <c>this[System.Range]</c>.</summary>
    ExplicitRange,

    /// <summary>No index/range-specific binding available.</summary>
    None,
}

/// <summary>Analysis result for an element-access receiver.</summary>
internal readonly record struct ReceiverInfo(
    ReceiverKind Kind,
    IPropertySymbol? LengthProperty,
    ISymbol? SliceSymbol,
    ReceiverBinding BindingStyle = ReceiverBinding.None,
    IPropertySymbol? IntIndexer = null,
    IMethodSymbol? IntSlice = null)
{
    /// <summary>Bound Length/Count property name; defaults to Length (arrays).</summary>
    public string LengthMemberName => LengthProperty?.Name ?? "Length";
}

/// <summary>Status of a <c>x.M(a..b)</c> range-argument invocation site.</summary>
internal enum RangeInvocationStatus
{
    /// <summary>Not a single-System.Range-argument method; C31 does not own it.</summary>
    NotApplicable,

    /// <summary>A two-int overload is bound: rewritable to int arguments.</summary>
    Rewritable,

    /// <summary>
    /// Bound to a single-Range method without a discoverable int overload:
    /// keep the explicit IR120 diagnostic.
    /// </summary>
    NoIntOverload,
}

/// <summary>Analysis result for a range-argument method invocation site.</summary>
internal sealed record RangeInvocationInfo(
    RangeInvocationStatus Status,
    ExpressionSyntax Receiver,
    RangeExpressionSyntax Range,
    IMethodSymbol? IntOverload,
    IPropertySymbol? LengthProperty)
{
    public static readonly RangeInvocationInfo NotApplicable =
        new(RangeInvocationStatus.NotApplicable, null!, null!, null, null);
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

        var operation = UnwrapConversions(model.GetOperation(node));

        if (operation is IImplicitIndexerReferenceOperation ii)
        {
            // Compiler pattern downgrade: the bound symbols are authoritative
            // (custom int indexer/Slice + Length/Count, Span/AsSpan chains,
            // List<T>.Slice on .NET 10, read-only views, ...).
            return new ReceiverInfo(
                kind,
                ii.LengthSymbol as IPropertySymbol,
                ii.IndexerSymbol,
                ReceiverBinding.Pattern);
        }

        if (operation is IPropertyReferenceOperation property
            && property.Property.IsIndexer
            && property.Property.Parameters.Length == 1)
        {
            var parameterType = property.Property.Parameters[0].Type;
            if (IsSystemIndex(parameterType))
            {
                var members = PatternDiscovery.Discover(
                    node, node.Expression, model);
                return new ReceiverInfo(
                    kind,
                    members.LengthProperty,
                    null,
                    ReceiverBinding.ExplicitIndex,
                    members.IntIndexer,
                    members.IntSlice);
            }

            if (IsSystemRange(parameterType))
            {
                var members = PatternDiscovery.Discover(
                    node, node.Expression, model);
                return new ReceiverInfo(
                    kind,
                    members.LengthProperty,
                    members.IntSlice,
                    ReceiverBinding.ExplicitRange,
                    members.IntIndexer,
                    members.IntSlice);
            }

            // Ordinary this[int] binding (e.g. string Chars): built-in kinds
            // keep their default Length member; nothing else to discover.
            return new ReceiverInfo(kind, null, null, ReceiverBinding.None);
        }

        return new ReceiverInfo(kind, null, null);
    }

    /// <summary>
    /// Analyzes an explicit <c>receiver.Method(range)</c> invocation whose
    /// bound method takes a single <see cref="System.Range"/> argument
    /// (including extension methods, in reduced or static form). A two-int
    /// overload is resolved through speculative overload resolution.
    /// </summary>
    internal static RangeInvocationInfo AnalyzeRangeInvocation(
        InvocationExpressionSyntax node,
        SemanticModel model)
    {
        if (node.Expression is not MemberAccessExpressionSyntax memberAccess)
            return RangeInvocationInfo.NotApplicable;

        if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol bound)
            return RangeInvocationInfo.NotApplicable;

        var arguments = node.ArgumentList.Arguments;

        // Reduced instance/extension form: receiver.Method(range).
        if (bound.Parameters.Length == 1
            && arguments.Count == 1
            && IsSystemRange(bound.Parameters[0].Type)
            && arguments[0].Expression is RangeExpressionSyntax range1)
        {
            var receiver = memberAccess.Expression;
            var intOverload = PatternDiscovery.BindIntOverload(
                node, receiver, bound.Name, model);
            var length = DiscoverLength(node, receiver, model);
            return new RangeInvocationInfo(
                intOverload != null
                    ? RangeInvocationStatus.Rewritable
                    : RangeInvocationStatus.NoIntOverload,
                receiver, range1, intOverload, length);
        }

        // Static extension form: Ext.Method(receiver, range). In this form
        // GetSymbolInfo returns the ordinary static definition (the
        // ExtensionAttribute is not visible on it); IMethodSymbol.IsExtensionMethod
        // is the reliable marker.
        if (bound.IsStatic
            && bound.IsExtensionMethod
            && bound.Parameters.Length == 2
            && arguments.Count == 2
            && IsSystemRange(bound.Parameters[1].Type)
            && arguments[1].Expression is RangeExpressionSyntax range2)
        {
            var receiver = arguments[0].Expression;
            var intOverload = PatternDiscovery.BindStaticIntOverload(
                node, memberAccess.Expression, receiver, bound.Name, model);
            var length = DiscoverLength(node, receiver, model);
            return new RangeInvocationInfo(
                intOverload != null
                    ? RangeInvocationStatus.Rewritable
                    : RangeInvocationStatus.NoIntOverload,
                receiver, range2, intOverload, length);
        }

        return RangeInvocationInfo.NotApplicable;
    }

    /// <summary>
    /// Whether the enclosing invocation of <paramref name="rangeArgument"/>
    /// binds to a single-Range-parameter method owned by C31 (rewritable or
    /// not). Such range arguments must not get the standalone IR120.
    /// </summary>
    internal static bool IsRangeInvocationArgument(
        RangeExpressionSyntax rangeArgument,
        SemanticModel model)
    {
        if (rangeArgument.Parent is not ArgumentSyntax argument
            || argument.Parent is not BaseArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        var info = AnalyzeRangeInvocation(invocation, model);
        return info.Status != RangeInvocationStatus.NotApplicable;
    }

    private static IPropertySymbol? DiscoverLength(
        SyntaxNode site,
        ExpressionSyntax receiver,
        SemanticModel model)
        => PatternDiscovery.Discover(site, receiver, model).LengthProperty;

    private static IOperation? UnwrapConversions(IOperation? operation)
    {
        while (operation is IConversionOperation conversion
               && conversion.Operand != null)
        {
            operation = conversion.Operand;
        }

        return operation;
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

    /// <summary>
    /// Whether a single-Range-tail method has a sibling overload with the
    /// same parameter prefix plus two trailing int parameters
    /// (<c>Slice(Range)</c> vs <c>Slice(int, int)</c>;
    /// <c>Ext.Slice(this T, Range)</c> vs <c>Ext.Slice(this T, int, int)</c>).
    /// Used to exempt the Range-accepting member signature itself from the
    /// IR120 declaration diagnostic when M4 rewrites all its call sites.
    /// </summary>
    internal static bool HasIntSiblingOverload(IMethodSymbol method)
    {
        var count = method.Parameters.Length;
        if (count < 1 || !IsSystemRange(method.Parameters[count - 1].Type))
            return false;

        foreach (var candidate in method.ContainingType
                     .GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, method)
                || candidate.Parameters.Length != count + 1)
            {
                continue;
            }

            var match = true;
            for (var i = 0; i < count - 1; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(
                        candidate.Parameters[i].Type,
                        method.Parameters[i].Type))
                {
                    match = false;
                    break;
                }
            }

            if (match
                && candidate.Parameters[count - 1].Type.SpecialType
                    == SpecialType.System_Int32
                && candidate.Parameters[count].Type.SpecialType
                    == SpecialType.System_Int32)
            {
                return true;
            }
        }

        return false;
    }
}
