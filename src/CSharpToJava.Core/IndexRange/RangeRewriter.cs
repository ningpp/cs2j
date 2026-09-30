using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.IndexRange;

/// <summary>
/// IR-2/IR-3: normalizes the four range boundary forms into
/// <c>(offset, count)</c> and dispatches by receiver kind — arrays to
/// <c>Cs2jArray.ArrayHelper.Slice</c> (copy), strings to <c>Substring</c>,
/// Span/Memory and other bound Slice members to instance/extension calls.
/// Returns null (with an IR120/IR110 diagnostic) when no int-based rewrite
/// is possible.
/// </summary>
internal static class RangeRewriter
{
    internal static ExpressionSyntax? RewriteRangeElement(
        ElementAccessExpressionSyntax visited,
        RangeExpressionSyntax range,
        ReceiverInfo info,
        SyntaxNode anchor,
        TempNameAllocator names,
        List<TempLifter.PendingTemp> pending,
        IndexRangeStatistics stats,
        List<IndexRangeDiagnostic> diagnostics)
    {
        var receiverPure = IndexRangeSyntaxFacts.IsPure(visited.Expression);

        var order = 0;
        string? receiverName = null;

        ExpressionSyntax receiverExpression;
        if (receiverPure)
        {
            receiverExpression = visited.Expression;
        }
        else
        {
            receiverName = names.Allocate("r");
            pending.Add(new TempLifter.PendingTemp(
                TempLifter.DeclareVar(receiverName, visited.Expression),
                anchor, order++));
            receiverExpression = IdentifierName(receiverName);
        }

        // Length is read once: inline when the receiver is pure, otherwise a
        // temp declared after the boundary operands (evaluation order).
        string? lengthName = receiverPure ? null : names.Allocate("l");
        var lengthExpression = lengthName == null
            ? (ExpressionSyntax)SimpleMemberAccess(
                receiverExpression, info.LengthMemberName)
            : IdentifierName(lengthName);

        ExpressionSyntax ResolveBound(ExpressionSyntax? operand, bool isLeft)
        {
            if (operand == null)
                return isLeft ? ZeroLiteral() : lengthExpression;

            if (operand is PrefixUnaryExpressionSyntax index
                && index.RawKind == IndexRangeSyntaxFacts.IndexExpressionKind)
            {
                var fromEnd = index.Operand;
                if (IndexRangeSyntaxFacts.IsPure(fromEnd))
                    return Subtract(lengthExpression, fromEnd);

                var operandName = names.Allocate("n");
                pending.Add(new TempLifter.PendingTemp(
                    TempLifter.DeclareVar(operandName, fromEnd),
                    anchor, order++));
                stats.BoundTempsLifted++;
                return Subtract(lengthExpression, IdentifierName(operandName));
            }

            if (IndexRangeSyntaxFacts.IsPure(operand))
                return operand;

            var boundName = names.Allocate("b");
            pending.Add(new TempLifter.PendingTemp(
                TempLifter.DeclareVar(boundName, operand),
                anchor, order++));
            stats.BoundTempsLifted++;
            return IdentifierName(boundName);
        }

        var offset = ResolveBound(range.LeftOperand, isLeft: true);
        var end = ResolveBound(range.RightOperand, isLeft: false);

        if (lengthName != null)
        {
            pending.Add(new TempLifter.PendingTemp(
                TempLifter.DeclareInt(
                    lengthName,
                    SimpleMemberAccess(receiverExpression, info.LengthMemberName)),
                anchor, order++));
            stats.LengthTempReads++;
        }

        if (receiverName != null)
            stats.ReceiverTempsLifted++;

        var count = Subtract(end, offset);
        var fullRange = range.LeftOperand == null && range.RightOperand == null;

        switch (info.Kind)
        {
            case ReceiverKind.SzArray:
                stats.RangeSliceSites++;
                stats.ArrayHelperCalls++;
                return ArrayHelperSlice(receiverExpression, offset, count);

            case ReceiverKind.String:
                stats.RangeSliceSites++;
                if (fullRange)
                    return receiverExpression;

                stats.StringSpecializations++;
                return range.RightOperand == null
                    ? InstanceCall(receiverExpression, "Substring", offset)
                    : InstanceCall(receiverExpression, "Substring", offset, count);

            case ReceiverKind.Span:
            case ReceiverKind.Memory:
                stats.RangeSliceSites++;
                return fullRange
                    ? receiverExpression
                    : InstanceCall(receiverExpression, "Slice", offset, count);

            case ReceiverKind.ArraySegment:
                stats.RangeSliceSites++;
                var segmentMethod =
                    (info.SliceSymbol as IMethodSymbol)?.Name ?? "Slice";
                return InstanceCall(
                    receiverExpression, segmentMethod, offset, count);

            default:
                switch (info.SliceSymbol)
                {
                    case IMethodSymbol method:
                        stats.RangeSliceSites++;
                        if (method.MethodKind == MethodKind.ReducedExtension
                            && method.ReducedFrom != null)
                        {
                            return ExtensionCall(
                                method.ReducedFrom,
                                receiverExpression, offset, count);
                        }

                        if (IsExtensionMethod(method))
                        {
                            return ExtensionCall(
                                method, receiverExpression, offset, count);
                        }

                        return InstanceCall(
                            receiverExpression, method.Name, offset, count);

                    case IPropertySymbol property:
                        diagnostics.Add(new IndexRangeDiagnostic(
                            "IR120",
                            IndexRangeSeverity.Error,
                            "Range expression is bound to a custom Range indexer " +
                            $"'{property.ContainingType.ToDisplayString()}.this[Range]'; " +
                            "only int-based Slice(int, int) patterns are supported " +
                            "in this milestone. Rewrite the call site manually to " +
                            "an explicit int offset/count call."));
                        return null;

                    default:
                        diagnostics.Add(new IndexRangeDiagnostic(
                            "IR110",
                            IndexRangeSeverity.Error,
                            "Range slice member could not be resolved to an " +
                            "int-based Slice(int, int) member; cannot rewrite."));
                        return null;
                }
        }
    }

    private static InvocationExpressionSyntax ArrayHelperSlice(
        ExpressionSyntax receiver, ExpressionSyntax offset, ExpressionSyntax count)
    {
        var helperType = SimpleMemberAccess(
            IdentifierName("Cs2jArray"), "ArrayHelper");
        var sliceMember = SimpleMemberAccess(helperType, "Slice");
        return InvocationExpression(
            sliceMember, ArgumentList(receiver, offset, count));
    }

    private static InvocationExpressionSyntax InstanceCall(
        ExpressionSyntax target, string methodName, params ExpressionSyntax[] args)
        => InvocationExpression(
            SimpleMemberAccess(target, methodName),
            ArgumentList(args));

    private static bool IsExtensionMethod(IMethodSymbol method)
        => method.IsStatic
           && method.GetAttributes().Any(a =>
               a.AttributeClass != null
               && a.AttributeClass.ToDisplayString()
                   == "System.Runtime.CompilerServices.ExtensionAttribute");

    private static InvocationExpressionSyntax ExtensionCall(
        IMethodSymbol symbol,
        ExpressionSyntax receiver,
        ExpressionSyntax offset,
        ExpressionSyntax count)
    {
        var typeName = ParseName(symbol.ContainingType.ToDisplayString());
        var member = SimpleMemberAccess(typeName, symbol.Name);
        return InvocationExpression(
            member, ArgumentList(new[] { receiver, offset, count }));
    }

    private static ArgumentListSyntax ArgumentList(
        params ExpressionSyntax[] arguments)
        => SyntaxFactory.ArgumentList(
            SeparatedList(arguments.Select(SyntaxFactory.Argument)));

    private static LiteralExpressionSyntax ZeroLiteral()
        => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0));

    private static ExpressionSyntax Subtract(
        ExpressionSyntax left, ExpressionSyntax right)
        => IndexRangeSyntaxFacts.TryFoldSubtract(left, right)
           ?? BinaryExpression(SyntaxKind.SubtractExpression, left, right);

    private static MemberAccessExpressionSyntax SimpleMemberAccess(
        ExpressionSyntax expression, string memberName)
        => MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            expression,
            IdentifierName(memberName));
}
