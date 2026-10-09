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
        var needsLength = RangeNeedsLength(range);
        if (needsLength && info.LengthProperty == null
            && info.Kind is ReceiverKind.Custom)
        {
            diagnostics.Add(UnresolvedRangeDiagnostic(info));
            return null;
        }

        var plan = PlanRange(
            visited.Expression, range, info.LengthProperty,
            anchor, names, pending, stats);

        switch (info.Kind)
        {
            case ReceiverKind.SzArray:
                stats.RangeSliceSites++;
                stats.ArrayHelperCalls++;
                return ArrayHelperSlice(plan.Receiver, plan.Offset, plan.Count);

            case ReceiverKind.String:
                stats.RangeSliceSites++;
                if (plan.FullRange)
                    return plan.Receiver;

                stats.StringSpecializations++;
                return range.RightOperand == null
                    ? InstanceCall(plan.Receiver, "Substring", plan.Offset)
                    : InstanceCall(plan.Receiver, "Substring", plan.Offset, plan.Count);

            case ReceiverKind.Span:
            case ReceiverKind.Memory:
                stats.RangeSliceSites++;
                return plan.FullRange
                    ? plan.Receiver
                    : InstanceCall(plan.Receiver, "Slice", plan.Offset, plan.Count);

            case ReceiverKind.ArraySegment:
                stats.RangeSliceSites++;
                var segmentMethod =
                    (info.SliceSymbol as IMethodSymbol)?.Name ?? "Slice";
                return InstanceCall(
                    plan.Receiver, segmentMethod, plan.Offset, plan.Count);

            default:
                switch (info.SliceSymbol)
                {
                    case IMethodSymbol method:
                        stats.RangeSliceSites++;
                        return EmitBoundSliceCall(
                            method, plan.Receiver, plan.Offset, plan.Count);

                    case IPropertySymbol property:
                        diagnostics.Add(new IndexRangeDiagnostic(
                            "IR120",
                            IndexRangeSeverity.Error,
                            "Range expression is bound to a custom Range indexer " +
                            $"'{property.ContainingType.ToDisplayString()}.this[Range]' " +
                            "and no int-based Slice(int, int) member is bound on " +
                            "the receiver; rewrite the call site manually to " +
                            "explicit int offset/count calls."));
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

    /// <summary>
    /// M4: rewrites an explicit <c>receiver.Method(a..b)</c> invocation bound
    /// to a single-Range-argument method into the resolved two-int overload
    /// (instance or extension static form).
    /// </summary>
    internal static ExpressionSyntax? RewriteRangeInvocation(
        RangeInvocationInfo site,
        SyntaxNode anchor,
        TempNameAllocator names,
        List<TempLifter.PendingTemp> pending,
        IndexRangeStatistics stats,
        List<IndexRangeDiagnostic> diagnostics)
    {
        if (site.Status != RangeInvocationStatus.Rewritable
            || site.IntOverload == null)
        {
            diagnostics.Add(new IndexRangeDiagnostic(
                "IR120",
                IndexRangeSeverity.Error,
                "The range-argument method has no bound int-based " +
                "(int, int) overload; rewrite the call site manually to " +
                "explicit int offset/count values."));
            return null;
        }

        if (RangeNeedsLength(site.Range) && site.LengthProperty == null)
        {
            diagnostics.Add(new IndexRangeDiagnostic(
                "IR120",
                IndexRangeSeverity.Error,
                "The range uses a from-end ('^') or open boundary but no " +
                "Length/Count member is bound on the receiver; rewrite the " +
                "call site manually to explicit int offset/count values."));
            return null;
        }

        var plan = PlanRange(
            site.Receiver, site.Range, site.LengthProperty,
            anchor, names, pending, stats);

        stats.RangeSliceSites++;
        stats.RangeInvocationSites++;
        return EmitBoundSliceCall(
            site.IntOverload, plan.Receiver, plan.Offset, plan.Count);
    }

    internal static bool RangeNeedsLength(RangeExpressionSyntax range)
        => range.LeftOperand == null
           || range.RightOperand == null
           || range.LeftOperand.RawKind == IndexRangeSyntaxFacts.IndexExpressionKind
           || range.RightOperand.RawKind == IndexRangeSyntaxFacts.IndexExpressionKind;

    private static IndexRangeDiagnostic UnresolvedRangeDiagnostic(ReceiverInfo info)
        => new(
            "IR120",
            IndexRangeSeverity.Error,
            "The range site is bound to a custom System.Index/System.Range " +
            "member but its int pattern is incomplete (no bound Length/Count " +
            "and int indexer/Slice(int, int) members); rewrite the call site " +
            "manually to explicit int offset/count calls.");

    private static InvocationExpressionSyntax EmitBoundSliceCall(
        IMethodSymbol method,
        ExpressionSyntax receiver,
        ExpressionSyntax offset,
        ExpressionSyntax count)
    {
        if (method.MethodKind == MethodKind.ReducedExtension
            && method.ReducedFrom != null)
        {
            return ExtensionCall(method.ReducedFrom, receiver, offset, count);
        }

        if (method.IsExtensionMethod)
            return ExtensionCall(method, receiver, offset, count);

        return InstanceCall(receiver, method.Name, offset, count);
    }

    /// <summary>
    /// Shared (offset, count) planning: lifts the receiver and impure
    /// boundary operands into single-evaluation temps and reads the length
    /// member once, in the compiler's evaluation order.
    /// </summary>
    private static RangePlan PlanRange(
        ExpressionSyntax originalReceiver,
        RangeExpressionSyntax range,
        IPropertySymbol? lengthProperty,
        SyntaxNode anchor,
        TempNameAllocator names,
        List<TempLifter.PendingTemp> pending,
        IndexRangeStatistics stats)
    {
        var receiverPure = IndexRangeSyntaxFacts.IsPure(originalReceiver);

        var order = 0;
        string? receiverName = null;

        ExpressionSyntax receiverExpression;
        if (receiverPure)
        {
            receiverExpression = originalReceiver;
        }
        else
        {
            receiverName = names.Allocate("r");
            pending.Add(new TempLifter.PendingTemp(
                TempLifter.DeclareVar(receiverName, originalReceiver),
                anchor, order++));
            stats.ReceiverTempsLifted++;
            receiverExpression = IdentifierName(receiverName);
        }

        // Length is read once: inline when the receiver is pure, otherwise a
        // temp declared after the boundary operands (evaluation order).
        string? lengthName = receiverPure ? null : names.Allocate("l");
        var lengthMember = lengthProperty?.Name ?? "Length";
        var lengthExpression = lengthName == null
            ? (ExpressionSyntax)SimpleMemberAccess(receiverExpression, lengthMember)
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
                    SimpleMemberAccess(receiverExpression, lengthMember)),
                anchor, order++));
            stats.LengthTempReads++;
        }

        if (receiverName != null)
            stats.ReceiverTempsLifted++;

        var count = Subtract(end, offset);
        var fullRange = range.LeftOperand == null && range.RightOperand == null;
        return new RangePlan(receiverExpression, offset, count, fullRange);
    }

    private sealed record RangePlan(
        ExpressionSyntax Receiver,
        ExpressionSyntax Offset,
        ExpressionSyntax Count,
        bool FullRange);

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
    {
        var folded = IndexRangeSyntaxFacts.TryFoldSubtract(left, right);
        if (folded != null)
        {
            return folded;
        }

        // 右操作数本身是复合算术表达式时必须加括号：减法左结合，否则
        // count = (len-1) - (len-2) 会被发射成 len-1-len-2（=-3），
        // 双 hat 边界（a[^2..^1]）与 hat-left 开区间（a[^2..]）全部算错。
        if (right is BinaryExpressionSyntax or ParenthesizedExpressionSyntax)
        {
            right = ParenthesizedExpression(right);
        }

        return BinaryExpression(SyntaxKind.SubtractExpression, left, right);
    }

    private static MemberAccessExpressionSyntax SimpleMemberAccess(
        ExpressionSyntax expression, string memberName)
        => MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            expression,
            IdentifierName(memberName));
}
