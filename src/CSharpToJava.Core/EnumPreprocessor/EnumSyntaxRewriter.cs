using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EnumPreprocessor;

/// <summary>
/// Single-pass CSharpSyntaxRewriter. Rewrites enum bitwise operations
/// (|, &amp;, ^, ~), compound assignments (|=, &amp;=, ^=), and HasFlag
/// calls into explicit underlying-type integer operations + (E) casts.
/// Only queries SemanticModel on original (pre-rewrite) nodes.
/// </summary>
internal sealed class EnumSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly EnumPreprocessorStatistics _stats;
    private readonly List<EnumPreprocessorDiagnostic> _diagnostics;
    private readonly EnumPreprocessorOptions _options;

    public int Rewrites { get; private set; }
    public int Skipped { get; private set; }

    public EnumSyntaxRewriter(
        SemanticModel? semanticModel,
        EnumPreprocessorOptions options,
        EnumPreprocessorStatistics stats,
        List<EnumPreprocessorDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    // ── Type resolution helpers ──

    /// <summary>
    /// Returns the enum type symbol (INamedTypeSymbol with TypeKind.Enum)
    /// for the given expression, or null if the expression is not an enum.
    /// Handles nullable enums (E? → E).
    /// </summary>
    private INamedTypeSymbol? GetEnumType(ExpressionSyntax expr)
    {
        if (_semanticModel == null) return null;
        var type = _semanticModel.GetTypeInfo(expr).Type;
        if (type == null) return null;

        // Unwrap nullable
        if (type is INamedTypeSymbol nts
            && nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && nts.TypeArguments.Length == 1
            && nts.TypeArguments[0] is INamedTypeSymbol inner
            && inner.TypeKind == TypeKind.Enum)
        {
            return inner;
        }

        if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
            return named;

        return null;
    }

    /// <summary>
    /// Returns true if the expression's static type is a nullable enum (E?).
    /// </summary>
    private bool IsNullableEnum(ExpressionSyntax expr)
    {
        if (_semanticModel == null) return false;
        var type = _semanticModel.GetTypeInfo(expr).Type;
        if (type is INamedTypeSymbol nts
            && nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && nts.TypeArguments.Length == 1
            && nts.TypeArguments[0] is INamedTypeSymbol inner
            && inner.TypeKind == TypeKind.Enum)
            return true;
        return false;
    }

    /// <summary>
    /// Returns the underlying type symbol (e.g. int, byte) for an enum type symbol.
    /// </summary>
    private static INamedTypeSymbol? GetUnderlyingType(INamedTypeSymbol enumType)
        => enumType.EnumUnderlyingType;

    /// <summary>
    /// Returns the SpecialType of the underlying type, or None.
    /// </summary>
    private static SpecialType GetUnderlyingSpecialType(INamedTypeSymbol enumType)
        => enumType.EnumUnderlyingType?.SpecialType ?? SpecialType.None;

    /// <summary>
    /// Returns true if the underlying type is a narrow integer type
    /// that requires unchecked() for ~ operations: byte, sbyte, short, ushort.
    /// </summary>
    private static bool IsNarrowType(INamedTypeSymbol enumType)
    {
        var st = GetUnderlyingSpecialType(enumType);
        return st is SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16;
    }

    /// <summary>
    /// Creates a (U) cast expression around the given expression,
    /// where U is the underlying type (int, byte, etc.).
    /// </summary>
    private static ExpressionSyntax CastToUnderlying(
        ExpressionSyntax expr, INamedTypeSymbol underlyingType)
    {
        var typeSyntax = SyntaxFactory.PredefinedType(
            SyntaxFactory.Token(GetTypeKeywordKind(underlyingType.SpecialType)));
        return SyntaxFactory.CastExpression(typeSyntax, EnsureParenthesized(expr));
    }

    private static SyntaxKind GetTypeKeywordKind(SpecialType st) => st switch
    {
        SpecialType.System_Int32 => SyntaxKind.IntKeyword,
        SpecialType.System_UInt32 => SyntaxKind.UIntKeyword,
        SpecialType.System_Int64 => SyntaxKind.LongKeyword,
        SpecialType.System_UInt64 => SyntaxKind.ULongKeyword,
        SpecialType.System_Int16 => SyntaxKind.ShortKeyword,
        SpecialType.System_UInt16 => SyntaxKind.UShortKeyword,
        SpecialType.System_Byte => SyntaxKind.ByteKeyword,
        SpecialType.System_SByte => SyntaxKind.SByteKeyword,
        _ => SyntaxKind.IntKeyword,
    };

    /// <summary>
    /// Creates a (E) cast expression around the given expression.
    /// Uses the enum type name from the symbol.
    /// </summary>
    private static ExpressionSyntax CastToEnum(
        ExpressionSyntax expr, INamedTypeSymbol enumType)
    {
        var enumName = SyntaxFactory.IdentifierName(enumType.Name);
        return SyntaxFactory.CastExpression(enumName, EnsureParenthesized(expr));
    }

    // ── Stable reference check (spec §3.9) ──

    /// <summary>
    /// Returns true if the expression is a "stable reference":
    /// local variable, parameter, field, this.f, or Type.F.
    /// The receiver chain must not contain properties/indexers/calls.
    /// </summary>
    private bool IsStableReference(ExpressionSyntax expr)
    {
        // Strip parentheses
        expr = StripParentheses(expr);

        if (_semanticModel == null) return false;

        // Simple identifier: local / parameter / field
        if (expr is IdentifierNameSyntax)
        {
            var symbol = _semanticModel.GetSymbolInfo(expr).Symbol;
            return symbol is ILocalSymbol
                or IParameterSymbol
                or IFieldSymbol;
        }

        // Member access: this.F, Type.F, Outer.Static.F
        if (expr is MemberAccessExpressionSyntax ma)
        {
            var symbol = _semanticModel.GetSymbolInfo(expr).Symbol;
            if (symbol is not IFieldSymbol) return false;

            // The receiver chain must be stable too
            return IsStableReceiver(ma.Expression);
        }

        return false;
    }

    private bool IsStableReceiver(ExpressionSyntax expr)
    {
        expr = StripParentheses(expr);

        // this
        if (expr is ThisExpressionSyntax) return true;

        // Simple identifier (could be a type name or a local/field)
        if (expr is IdentifierNameSyntax)
        {
            var symbol = _semanticModel?.GetSymbolInfo(expr).Symbol;
            if (symbol is IFieldSymbol) return true;
            // Could be a type name — check if it's a type
            if (symbol is INamedTypeSymbol) return true;
            // Could be local/parameter — that's also stable as a receiver chain
            if (symbol is ILocalSymbol or IParameterSymbol) return true;
            return false;
        }

        // Nested member access: recurse
        if (expr is MemberAccessExpressionSyntax ma)
        {
            var symbol = _semanticModel?.GetSymbolInfo(expr).Symbol;
            // Only fields and types are stable in the receiver chain
            if (symbol is IFieldSymbol or INamedTypeSymbol)
                return IsStableReceiver(ma.Expression);
            return false;
        }

        return false;
    }

    /// <summary>
    /// Wraps an expression in unchecked(...) if the underlying type is narrow.
    /// </summary>
    private static ExpressionSyntax MaybeUnchecked(
        ExpressionSyntax expr, INamedTypeSymbol enumType)
    {
        if (!IsNarrowType(enumType)) return expr;
        return SyntaxFactory.CheckedExpression(
            SyntaxKind.UncheckedExpression, expr);
    }

    private void AddWarning(string message)
    {
        _diagnostics.Add(new EnumPreprocessorDiagnostic(
            EnumPreprocessorSeverity.Warning, message));
        Skipped++;
        _stats.SkippedUnsupported++;
    }

    private void IncrementRewrite()
    {
        Rewrites++;
    }

    // ── Parentheses helpers (no extension methods available) ──

    private static ExpressionSyntax EnsureParenthesized(ExpressionSyntax expr)
    {
        if (expr is ParenthesizedExpressionSyntax)
            return expr;
        // For simple expressions (identifiers, member access, literals) no parens needed
        if (expr is IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or LiteralExpressionSyntax
            or ThisExpressionSyntax)
            return expr;
        return SyntaxFactory.ParenthesizedExpression(expr);
    }

    private static ExpressionSyntax StripParentheses(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax pe)
            expr = pe.Expression;
        return expr;
    }

    // ════════════════════════════════════════════════════════════════
    // Visit overrides: binary, unary, compound assignment, HasFlag
    // ════════════════════════════════════════════════════════════════

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        // Process children first (bottom-up)
        var visitedLeft = (ExpressionSyntax?)Visit(node.Left);
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var left = visitedLeft ?? node.Left;
        var right = visitedRight ?? node.Right;

        // Only handle enum bitwise operations
        if (!node.IsKind(SyntaxKind.BitwiseOrExpression)
            && !node.IsKind(SyntaxKind.BitwiseAndExpression)
            && !node.IsKind(SyntaxKind.ExclusiveOrExpression))
            return node.Update(left, node.OperatorToken, right);

        // Determine if either operand is an enum type
        var leftEnum = GetEnumType(node.Left);
        var rightEnum = GetEnumType(node.Right);
        var enumType = leftEnum ?? rightEnum;

        if (enumType == null)
            return node.Update(left, node.OperatorToken, right);

        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
        {
            AddWarning($"Cannot resolve underlying type for enum '{enumType.Name}' in bitwise expression; skipping.");
            return node.Update(left, node.OperatorToken, right);
        }

        // Handle nullable (lifted) enums
        var leftIsNullable = IsNullableEnum(node.Left);
        var rightIsNullable = IsNullableEnum(node.Right);

        if (leftIsNullable || rightIsNullable)
        {
            return RewriteLiftedBinary(node, left, right, enumType, underlying,
                leftIsNullable, rightIsNullable);
        }

        // Non-nullable: (E)(L' | R')
        var leftCasted = MaybeCastToUnderlying(left, node.Left, enumType, underlying);
        var rightCasted = MaybeCastToUnderlying(right, node.Right, enumType, underlying);

        var innerOp = node.Kind() switch
        {
            SyntaxKind.BitwiseOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseOrExpression, leftCasted, SyntaxFactory.Token(SyntaxKind.BarToken), rightCasted),
            SyntaxKind.BitwiseAndExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseAndExpression, leftCasted, SyntaxFactory.Token(SyntaxKind.AmpersandToken), rightCasted),
            SyntaxKind.ExclusiveOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.ExclusiveOrExpression, leftCasted, SyntaxFactory.Token(SyntaxKind.CaretToken), rightCasted),
            _ => throw new InvalidOperationException(),
        };

        // Outer (E) cast — skip if parent is already (E) cast
        var result = ShouldSkipOuterCast(node)
            ? innerOp
            : CastToEnum(innerOp, enumType);

        // Update stats
        switch (node.Kind())
        {
            case SyntaxKind.BitwiseOrExpression: _stats.BitwiseOrRewritten++; break;
            case SyntaxKind.BitwiseAndExpression: _stats.BitwiseAndRewritten++; break;
            case SyntaxKind.ExclusiveOrExpression: _stats.BitwiseXorRewritten++; break;
        }
        IncrementRewrite();
        return EnsureParenthesized(result);
    }

    private ExpressionSyntax MaybeCastToUnderlying(
        ExpressionSyntax visited,
        ExpressionSyntax original,
        INamedTypeSymbol enumType,
        INamedTypeSymbol underlying)
    {
        // If the original operand's type was the enum, we need (U) cast
        var origEnum = GetEnumType(original);
        if (origEnum != null)
            return CastToUnderlying(visited, underlying);

        // Check if original is already the underlying type
        if (_semanticModel != null)
        {
            var origType = _semanticModel.GetTypeInfo(original).Type;
            if (origType != null && origType.Equals(underlying, SymbolEqualityComparer.Default))
                return visited; // already U
        }

        // Literal 0 or default → leave as-is
        return visited;
    }

    private bool ShouldSkipOuterCast(ExpressionSyntax node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax pe)
            parent = pe.Parent;

        if (parent is CastExpressionSyntax cast)
        {
            var castType = _semanticModel?.GetTypeInfo(cast.Type).Type;
            var nodeEnum = GetEnumType(node);
            return castType != null && nodeEnum != null
                && castType.Equals(nodeEnum, SymbolEqualityComparer.Default);
        }
        return false;
    }

    private SyntaxNode RewriteLiftedBinary(
        BinaryExpressionSyntax node,
        ExpressionSyntax left,
        ExpressionSyntax right,
        INamedTypeSymbol enumType,
        INamedTypeSymbol underlying,
        bool leftIsNullable,
        bool rightIsNullable)
    {
        // Check stability of nullable operands
        if (leftIsNullable && !IsStableReference(node.Left))
        {
            AddWarning($"Nullable enum operand is not a stable reference; skipping lifted bitwise rewrite.");
            return node.Update(left, node.OperatorToken, right);
        }
        if (rightIsNullable && !IsStableReference(node.Right))
        {
            AddWarning($"Nullable enum operand is not a stable reference; skipping lifted bitwise rewrite.");
            return node.Update(left, node.OperatorToken, right);
        }

        var leftExpr = leftIsNullable
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                left,
                SyntaxFactory.IdentifierName("Value"))
            : MaybeCastToUnderlying(left, node.Left, enumType, underlying);
        if (leftIsNullable)
            leftExpr = CastToUnderlying(leftExpr, underlying);

        var rightExpr = rightIsNullable
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                right,
                SyntaxFactory.IdentifierName("Value"))
            : MaybeCastToUnderlying(right, node.Right, enumType, underlying);
        if (rightIsNullable)
            rightExpr = CastToUnderlying(rightExpr, underlying);

        var opKind = node.Kind();
        var innerOp = opKind switch
        {
            SyntaxKind.BitwiseOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseOrExpression, leftExpr, SyntaxFactory.Token(SyntaxKind.BarToken), rightExpr),
            SyntaxKind.BitwiseAndExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.BitwiseAndExpression, leftExpr, SyntaxFactory.Token(SyntaxKind.AmpersandToken), rightExpr),
            SyntaxKind.ExclusiveOrExpression => SyntaxFactory.BinaryExpression(
                SyntaxKind.ExclusiveOrExpression, leftExpr, SyntaxFactory.Token(SyntaxKind.CaretToken), rightExpr),
            _ => throw new InvalidOperationException(),
        };

        var enumName = SyntaxFactory.IdentifierName(enumType.Name);
        var nullableEnum = SyntaxFactory.NullableType(enumName);

        ExpressionSyntax hasValueCondition = rightIsNullable
            ? SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalAndExpression,
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    left,
                    SyntaxFactory.IdentifierName("HasValue")),
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    right,
                    SyntaxFactory.IdentifierName("HasValue")))
            : SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                left,
                SyntaxFactory.IdentifierName("HasValue"));

        var trueExpr = SyntaxFactory.CastExpression(
            nullableEnum,
            EnsureParenthesized(CastToEnum(innerOp, enumType)));
        var falseExpr = SyntaxFactory.CastExpression(
            nullableEnum,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

        var ternary = SyntaxFactory.ConditionalExpression(
            hasValueCondition,
            trueExpr,
            falseExpr);

        switch (opKind)
        {
            case SyntaxKind.BitwiseOrExpression: _stats.BitwiseOrRewritten++; break;
            case SyntaxKind.BitwiseAndExpression: _stats.BitwiseAndRewritten++; break;
            case SyntaxKind.ExclusiveOrExpression: _stats.BitwiseXorRewritten++; break;
        }
        _stats.NullableLiftedRewritten++;
        IncrementRewrite();
        return SyntaxFactory.ParenthesizedExpression(ternary);
    }

    // ── Unary ~ ──

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.BitwiseNotExpression))
            return base.VisitPrefixUnaryExpression(node);

        var visitedOperand = (ExpressionSyntax?)Visit(node.Operand);
        var operand = visitedOperand ?? node.Operand;

        var enumType = GetEnumType(node.Operand);
        if (enumType == null)
            return node.Update(node.OperatorToken, operand);

        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
        {
            AddWarning($"Cannot resolve underlying type for enum '{enumType.Name}' in ~ expression; skipping.");
            return node.Update(node.OperatorToken, operand);
        }

        // Handle nullable enum
        if (IsNullableEnum(node.Operand))
        {
            if (!IsStableReference(node.Operand))
            {
                AddWarning($"Nullable enum operand is not a stable reference; skipping lifted ~ rewrite.");
                return node.Update(node.OperatorToken, operand);
            }
            // ~x (x: E?) → x.HasValue ? (E?)unchecked(...) : (E?)null
            var valueAccess = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                operand,
                SyntaxFactory.IdentifierName("Value"));
            var castedValue = CastToUnderlying(valueAccess, underlying);
            var notExpr = SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.BitwiseNotExpression,
                SyntaxFactory.Token(SyntaxKind.TildeToken),
                EnsureParenthesized(castedValue));
            ExpressionSyntax narrowed = IsNarrowType(enumType)
                ? SyntaxFactory.CheckedExpression(
                    SyntaxKind.UncheckedExpression, CastToUnderlying(notExpr, underlying))
                : CastToUnderlying(notExpr, underlying);
            var enumName = SyntaxFactory.IdentifierName(enumType.Name);
            var nullableEnum = SyntaxFactory.NullableType(enumName);
            var trueExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                EnsureParenthesized(CastToEnum(narrowed, enumType)));
            var falseExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
            var ternary = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    operand,
                    SyntaxFactory.IdentifierName("HasValue")),
                trueExpr,
                falseExpr);

            _stats.BitwiseNotRewritten++;
            _stats.NullableLiftedRewritten++;
            IncrementRewrite();
            return SyntaxFactory.ParenthesizedExpression(ternary);
        }

        // Non-nullable: ~X → (E)(~(U)X) or (E)unchecked((U)(~(U)X)) for narrow types
        var operandCasted = CastToUnderlying(operand, underlying);
        var notExpr2 = SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.BitwiseNotExpression,
            SyntaxFactory.Token(SyntaxKind.TildeToken),
            EnsureParenthesized(operandCasted));

        ExpressionSyntax result;
        if (IsNarrowType(enumType))
        {
            // (E)unchecked((U)(~(U)X))
            var innerCast = CastToUnderlying(notExpr2, underlying);
            var uncheckedExpr = SyntaxFactory.CheckedExpression(
                SyntaxKind.UncheckedExpression, innerCast);
            result = ShouldSkipOuterCast(node)
                ? uncheckedExpr
                : CastToEnum(uncheckedExpr, enumType);
        }
        else
        {
            // (E)(~(U)X)
            result = ShouldSkipOuterCast(node)
                ? notExpr2
                : CastToEnum(notExpr2, enumType);
        }

        _stats.BitwiseNotRewritten++;
        IncrementRewrite();
        return EnsureParenthesized(result);
    }

    // ── Compound assignment ──

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var right = visitedRight ?? node.Right;

        if (!node.IsKind(SyntaxKind.OrAssignmentExpression)
            && !node.IsKind(SyntaxKind.AndAssignmentExpression)
            && !node.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression))
            return node.Update(node.Left, node.OperatorToken, right);

        // Compound assignment to enum — only rewrite if target is stable
        var enumType = GetEnumType(node.Left);
        if (enumType == null)
            return node.Update(node.Left, node.OperatorToken, right);

        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
        {
            AddWarning($"Cannot resolve underlying type for enum '{enumType.Name}' in compound assignment; skipping.");
            return node.Update(node.Left, node.OperatorToken, right);
        }

        if (!IsStableReference(node.Left))
        {
            AddWarning($"Compound assignment target is not a stable reference; skipping rewrite.");
            return node.Update(node.Left, node.OperatorToken, right);
        }

        var (binKind, binToken) = node.Kind() switch
        {
            SyntaxKind.OrAssignmentExpression => (SyntaxKind.BitwiseOrExpression, SyntaxFactory.Token(SyntaxKind.BarToken)),
            SyntaxKind.AndAssignmentExpression => (SyntaxKind.BitwiseAndExpression, SyntaxFactory.Token(SyntaxKind.AmpersandToken)),
            SyntaxKind.ExclusiveOrAssignmentExpression => (SyntaxKind.ExclusiveOrExpression, SyntaxFactory.Token(SyntaxKind.CaretToken)),
            _ => throw new InvalidOperationException(),
        };

        // Handle nullable enum compound assignment
        if (IsNullableEnum(node.Left))
        {
            var rightCasted = MaybeCastToUnderlying(right, node.Right, enumType, underlying);
            var leftValue = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                node.Left,
                SyntaxFactory.IdentifierName("Value"));
            var leftCasted = CastToUnderlying(leftValue, underlying);

            var innerOp = SyntaxFactory.BinaryExpression(binKind, leftCasted, binToken, rightCasted);
            var enumName = SyntaxFactory.IdentifierName(enumType.Name);
            var nullableEnum = SyntaxFactory.NullableType(enumName);
            var trueExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                EnsureParenthesized(CastToEnum(innerOp, enumType)));
            var falseExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

            var ternary = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    node.Left,
                    SyntaxFactory.IdentifierName("HasValue")),
                trueExpr,
                falseExpr);

            _stats.CompoundAssignRewritten++;
            _stats.NullableLiftedRewritten++;
            IncrementRewrite();
            return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                node.Left,
                SyntaxFactory.ParenthesizedExpression(ternary));
        }

        // Non-nullable: x |= R → x = (E)((U)x | R')
        var leftCasted2 = CastToUnderlying(node.Left, underlying);
        var rightCasted2 = MaybeCastToUnderlying(right, node.Right, enumType, underlying);

        var binExpr = SyntaxFactory.BinaryExpression(binKind, leftCasted2, binToken, rightCasted2);
        var castedResult = CastToEnum(binExpr, enumType);

        _stats.CompoundAssignRewritten++;
        IncrementRewrite();
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            node.Left,
            EnsureParenthesized(castedResult));
    }

    // ── HasFlag ──

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Process arguments first (bottom-up)
        var visitedArgs = Visit(node.ArgumentList);
        var args = visitedArgs as ArgumentListSyntax ?? node.ArgumentList;

        // Check if this is a HasFlag call: x.HasFlag(f)
        if (node.Expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.ValueText == "HasFlag"
            && args.Arguments.Count == 1)
        {
            var receiverEnum = GetEnumType(ma.Expression);
            if (receiverEnum == null)
                return node.Update(node.Expression, args);

            var argExpr = args.Arguments[0].Expression;
            var argEnum = GetEnumType(argExpr);

            // Both must be the same enum type
            if (argEnum == null
                || !argEnum.Equals(receiverEnum, SymbolEqualityComparer.Default))
            {
                AddWarning($"HasFlag argument type does not match receiver enum type; skipping rewrite.");
                return node.Update(node.Expression, args);
            }

            var underlying = GetUnderlyingType(receiverEnum);
            if (underlying == null)
            {
                AddWarning($"Cannot resolve underlying type for enum '{receiverEnum.Name}' in HasFlag; skipping.");
                return node.Update(node.Expression, args);
            }

            // Handle nullable receiver
            if (IsNullableEnum(ma.Expression))
            {
                AddWarning($"Nullable receiver in HasFlag call; skipping rewrite.");
                return node.Update(node.Expression, args);
            }

            // Rewrite: x.HasFlag(f) → ((U)x & (U)f) == (U)f
            var visitedReceiver = (ExpressionSyntax?)Visit(ma.Expression);
            var receiver = visitedReceiver ?? ma.Expression;
            var visitedArg = (ExpressionSyntax?)Visit(argExpr);
            var arg = visitedArg ?? argExpr;

            var receiverCasted = MaybeCastToUnderlying(receiver, ma.Expression, receiverEnum, underlying);
            var argCasted = MaybeCastToUnderlying(arg, argExpr, receiverEnum, underlying);
            // Use the same casted arg for comparison
            var argCastedForCompare = MaybeCastToUnderlying(arg, argExpr, receiverEnum, underlying);

            // BitwiseAnd 的优先级低于 Equals：语法树落盘为文本后会被重新解析为
            // a & (b == c)（byte & bool 编译错误），内层 & 表达式必须显式加括号。
            var andExpr = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.BitwiseAndExpression,
                    receiverCasted,
                    SyntaxFactory.Token(SyntaxKind.AmpersandToken),
                    argCasted));
            var eqExpr = SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                andExpr,
                SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken),
                argCastedForCompare);

            _stats.HasFlagRewritten++;
            IncrementRewrite();
            return SyntaxFactory.ParenthesizedExpression(eqExpr);
        }

        return node.Update(node.Expression, args);
    }
}
