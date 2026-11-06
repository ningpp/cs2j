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
    /// True when the underlying type is a narrow integer type
    /// that requires unchecked() for ~ operations: byte, sbyte, short, ushort.
    /// </summary>
    private static bool IsNarrowType(INamedTypeSymbol enumType)
    {
        var st = GetUnderlyingSpecialType(enumType);
        return st is SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16;
    }

    /// <summary>
    /// True for enums declared in source. External metadata enums are left
    /// untouched: their arithmetic/use sites are owned by the compat surface,
    /// not by this pass.
    /// </summary>
    private static bool IsSourceDeclaredEnum(INamedTypeSymbol? enumType)
        => enumType is { TypeKind: TypeKind.Enum }
           && enumType.DeclaringSyntaxReferences.Length > 0;

    private static bool IsIntegralType(ITypeSymbol? type)
        => type is not null && type.SpecialType is SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Char;

    /// <summary>
    /// 普通真 enum（plain real enum）：源内声明、底层 int、成员常量值与声明序对齐
    /// （0..n-1）。最终侧把它直译为 Java enum（仅 ==/!= 合法），算术/关系二元运算
    /// 必须在本 pass 降为 (int) 形态。判定口径与 Transformers 的
    /// EnumTransformer.IsValueEnum 逐字对齐：值形态（显式/非对齐值或非 int 底层）
    /// 最终侧以值类 getValue()/fromValue() 承载，外部元数据 enum 由 compat 常量
    /// 承载，都不适用 (int) 直译面以外的额外处理。
    /// </summary>
    private bool IsPlainRealEnum(INamedTypeSymbol? enumType)
    {
        if (enumType is not { TypeKind: TypeKind.Enum }) return false;
        if (enumType.DeclaringSyntaxReferences.Length == 0) return false;
        if (enumType.EnumUnderlyingType?.SpecialType != SpecialType.System_Int32) return false;
        var members = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .OrderBy(f => f.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start
                           ?? int.MaxValue)
            .ToList();
        for (var i = 0; i < members.Count; i++)
        {
            if (Convert.ToInt64(members[i].ConstantValue,
                    System.Globalization.CultureInfo.InvariantCulture) != i)
            {
                return false;
            }
        }
        return true;
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
    private ExpressionSyntax CastToEnum(
        ExpressionSyntax expr, INamedTypeSymbol enumType, SyntaxNode origin)
    {
        var enumName = EnumTypeSyntax(enumType, origin.SpanStart);
        return SyntaxFactory.CastExpression(enumName, EnsureParenthesized(expr));
    }

    /// <summary>
    /// Builds the cast target type for an enum at <paramref name="position"/>.
    /// The simple metadata name (<c>enumType.Name</c>) is not usable when the
    /// enum is nested inside another type and the use site lives outside that
    /// type (different namespace or even just a sibling class): the simple
    /// name then fails to bind (CS0246) and, worse, poisons the semantic
    /// model of downstream passes so the cast can no longer be classified.
    /// Roslyn's minimal display emits the shortest name that binds at the use
    /// site (<c>E</c> in scope, <c>Outer.E</c> in a sibling type,
    /// <c>Ns.Outer.E</c> cross namespace); without a semantic model the fully
    /// qualified <c>global::</c> form is used.
    /// </summary>
    private TypeSyntax EnumTypeSyntax(INamedTypeSymbol enumType, int position)
    {
        var text = _semanticModel is { } model
            ? enumType.ToMinimalDisplayString(
                model, position, SymbolDisplayFormat.MinimallyQualifiedFormat)
            : enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return SyntaxFactory.ParseTypeName(text);
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
    /// True when the compound-assignment target may be duplicated (read once,
    /// written once) without changing observable behavior. Covers, beyond
    /// <see cref="IsStableReference"/>: property access with a deterministic
    /// receiver (local/parameter/field/this/type chain — the property getter
    /// is still invoked exactly once, as with <c>|=</c>), and array element
    /// access with a deterministic receiver and side-effect-free indices.
    /// Indexers/properties with non-deterministic receivers, method-call
    /// receivers or side-effecting index expressions are NOT accepted:
    /// observationally-correct lowering for those would require statement
    /// level temp lifting, which this expression-only pass does not do.
    /// </summary>
    private bool IsSafelyExpandableTarget(ExpressionSyntax expr)
    {
        expr = StripParentheses(expr);

        if (IsStableReference(expr))
            return true;

        switch (expr)
        {
            case IdentifierNameSyntax:
                // Implicit-this property: a bare `Prop` target is `this.Prop`
                // (or a static property of the containing type), so the
                // receiver is just as deterministic as the explicit
                // `this.Prop` form below. Locals/parameters/fields never reach
                // this point (IsStableReference already accepted them), and
                // inside an enum member initializer `Prop` would bind to a
                // constant field, not a property.
                return _semanticModel?.GetSymbolInfo(expr).Symbol is IPropertySymbol;

            case MemberAccessExpressionSyntax ma:
                return _semanticModel?.GetSymbolInfo(expr).Symbol
                           is IFieldSymbol or IPropertySymbol
                       && IsStableReceiver(ma.Expression);

            case ElementAccessExpressionSyntax ea:
                // Array elements (receiver type is an array) with deterministic
                // base and pure indices; user indexers invoke getter/setter
                // pairs and stay unsupported.
                var receiverType = _semanticModel?.GetTypeInfo(ea.Expression).Type;
                return receiverType is IArrayTypeSymbol
                       && IsStableReceiver(ea.Expression)
                       && ea.ArgumentList.Arguments.All(
                           a => IsSideEffectFreeArgument(a.Expression));

            default:
                return false;
        }
    }

    /// <summary>
    /// Conservative purity check for index arguments that get evaluated
    /// twice when a compound assignment is expanded to read + write.
    /// </summary>
    private bool IsSideEffectFreeArgument(ExpressionSyntax? expr)
    {
        if (expr == null || _semanticModel == null)
            return false;

        foreach (var node in expr.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case AssignmentExpressionSyntax:
                case ConditionalAccessExpressionSyntax:
                case ThrowExpressionSyntax:
                case AwaitExpressionSyntax:
                case StackAllocArrayCreationExpressionSyntax:
                case AnonymousFunctionExpressionSyntax:
                    return false;

                case PrefixUnaryExpressionSyntax prefix
                    when prefix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken)
                         || prefix.OperatorToken.IsKind(SyntaxKind.MinusMinusToken):
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken)
                         || postfix.OperatorToken.IsKind(SyntaxKind.MinusMinusToken):
                    return false;

                // Property/indexer reads invoke accessors; only fields (and
                // locals/parameters) are guaranteed side-effect-free reads.
                case MemberAccessExpressionSyntax memberAccess:
                    if (_semanticModel.GetSymbolInfo(memberAccess).Symbol
                        is not (IFieldSymbol or INamedTypeSymbol))
                        return false;
                    break;

                case ElementAccessExpressionSyntax elementAccess:
                    if (_semanticModel.GetTypeInfo(elementAccess.Expression).Type
                        is not IArrayTypeSymbol)
                        return false;
                    break;
            }
        }

        return true;
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

        var kind = node.Kind();

        // 枚举成员初始化器里的二元表达式：成员引用在该上下文按底层整数绑定，
        // node 自身类型是底层整数而不是 enum。这里只把 enum 成员操作数降为
        // (U) 常量 cast，不回挂 (E)：enum 成员值必须是底层类型常量，
        // unchecked((E)...) / (E) 都不能出现在此处。
        if (IsInEnumMemberInitializer(node)
            && RewriteEnumMemberInitializerBinary(node, left, right) is { } memberInit)
        {
            return memberInit;
        }

        // 经典 is 表达式 p is E.M（语法树是 IsExpression）：Java 侧只认同类
        // enum 的 ==/!=，因此降为 p == E.M。外部元数据 enum 保持原样。
        if (kind is SyntaxKind.IsExpression
            && RewriteEnumIsExpression(node, left, right) is { } isExpr)
        {
            return isExpr;
        }

        // Equality / relational with an enum operand: lower the enum operand to its
        // underlying (ordinal) integer type. The Java-conversion gate
        // (BinaryExpressionTransformer.EnsureDirectBinaryOperands) allows only
        // same-enum ==/!=; any other enum operand (enum vs int, relational, etc.)
        // must be lowered here — otherwise lowering a parent bitwise expr produces
        // (E)((U)a op (U)b) compared against a non-enum value and trips the gate.
        if (kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression)
        {
            return RewriteEnumComparison(node, left, right, kind);
        }

        // 算术站点（E - E / E + i）：Java enum 不能参与算术，必须在预处理期降为
        // 底层整数形态。plain 真 enum 走既有口径；其余源内声明 enum（非 int 底层、
        // 显式/非对齐值）走通用分支。外部元数据 enum 保持原样。
        if (kind is SyntaxKind.SubtractExpression or SyntaxKind.AddExpression)
        {
            if (RewritePlainEnumArithmetic(node, left, right) is { } arith)
            {
                return arith;
            }
            if (RewriteSourceEnumArithmetic(node, left, right) is { } sourceArith)
            {
                return sourceArith;
            }
        }

        // Only handle enum bitwise operations
        if (!node.IsKind(SyntaxKind.BitwiseOrExpression)
            && !node.IsKind(SyntaxKind.BitwiseAndExpression)
            && !node.IsKind(SyntaxKind.ExclusiveOrExpression))
            return node.Update(left, node.OperatorToken, right);

        // Rewrite only when the ORIGINAL node itself binds to an enum bitwise
        // operator (its own type is E / E?). Inside enum member initializers
        // member references are bound as the enum's UNDERLYING integer type,
        // so the chosen operator is plain integer arithmetic even though the
        // identifiers look enum-typed. Rewriting those nodes nests an E cast
        // back under another bitwise operator, producing invalid `E | int`
        // code (CS0019) in the output.
        var enumType = GetEnumType(node);
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

        // Outer (E) cast — skip if parent is already (E) cast. 窄底层必须用
        // unchecked 包裹：C# 位运算本身从不溢出检查，而 (E)(intExpr) 的窄化
        // cast 在 checked 上下文会抛异常，unchecked 保住原语义。
        var result = ShouldSkipOuterCast(node)
            ? innerOp
            : MaybeUnchecked(CastToEnum(innerOp, enumType, node), enumType);

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

    // ── is pattern（x is not E.M） ──

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
        ExpressionSyntax? originalConstant = null;
        var negated = false;
        UnaryPatternSyntax? notPattern = null;
        if (node.Pattern is ConstantPatternSyntax constant)
        {
            originalConstant = constant.Expression;
        }
        else if (node.Pattern is UnaryPatternSyntax unary
                 && unary.Pattern is ConstantPatternSyntax inner
                 && unary.OperatorToken.IsKind(SyntaxKind.NotKeyword))
        {
            originalConstant = inner.Expression;
            negated = true;
            notPattern = unary;
        }

        if (originalConstant == null)
            return base.VisitIsPatternExpression(node);

        // `expr is not E.M` → `expr != E.M` / `expr is E.M` → `expr == E.M`.
        // The constant must bind to a member of the same enum the left side is
        // typed as. Metadata (BCL) enums are included: their use sites are
        // lowered to the compat constant surface too, and the Java gate accepts
        // same-enum ==/!=. Nullable left operands keep the pattern: `E?` is not
        // an enum-typed operand for the downstream gate, so the rewrite would
        // only move the residual.
        var enumType = GetEnumType(node.Expression);
        if (enumType == null || IsNullableEnum(node.Expression))
            return base.VisitIsPatternExpression(node);

        var symbol = _semanticModel?.GetSymbolInfo(StripParentheses(originalConstant)).Symbol;
        if (symbol is not IFieldSymbol field
            || !SymbolEqualityComparer.Default.Equals(field.ContainingType, enumType))
        {
            return base.VisitIsPatternExpression(node);
        }

        var visitedExpr = (ExpressionSyntax?)Visit(node.Expression) ?? node.Expression;
        var visitedConstant = (ExpressionSyntax?)Visit(originalConstant) ?? originalConstant;

        var token = SyntaxFactory.Token(negated
            ? SyntaxKind.ExclamationEqualsToken
            : SyntaxKind.EqualsEqualsToken);
        token = token
            .WithLeadingTrivia(node.IsKeyword.LeadingTrivia)
            .WithTrailingTrivia(
                notPattern != null
                    ? notPattern.OperatorToken.TrailingTrivia
                    : node.IsKeyword.TrailingTrivia);

        IncrementRewrite();
        return SyntaxFactory.BinaryExpression(
            negated ? SyntaxKind.NotEqualsExpression : SyntaxKind.EqualsExpression,
            visitedExpr,
            token,
            visitedConstant);
    }

    private bool IsInCheckedContext(SyntaxNode node)
        => node.Ancestors().Any(a =>
            (a is CheckedExpressionSyntax ce && ce.IsKind(SyntaxKind.CheckedExpression))
            || (a is CheckedStatementSyntax cs && cs.IsKind(SyntaxKind.CheckedStatement)));

    private static bool IsInEnumMemberInitializer(SyntaxNode node)
        => node.Ancestors().OfType<EqualsValueClauseSyntax>()
            .Any(eq => eq.Parent is EnumMemberDeclarationSyntax);

    /// <summary>
    /// 把枚举成员初始化器内的 enum 成员操作数降为 (U) 常量 cast。
    /// 初始化器上下文里成员引用本身已是底层整数常量，不能再回挂 (E)
    /// （成员值必须是底层类型常量）。返回 null 表示无需改写。
    /// 幂等：第二轮操作数已是 cast 表达式，识别不到 enum 成员符号。
    /// </summary>
    private BinaryExpressionSyntax? RewriteEnumMemberInitializerBinary(
        BinaryExpressionSyntax node, ExpressionSyntax left, ExpressionSyntax right)
    {
        var kind = node.Kind();
        if (kind is not (SyntaxKind.BitwiseOrExpression
            or SyntaxKind.BitwiseAndExpression
            or SyntaxKind.ExclusiveOrExpression
            or SyntaxKind.AddExpression
            or SyntaxKind.SubtractExpression))
        {
            return null;
        }

        var newLeft = CastEnumMemberToUnderlying(node.Left, left) ?? left;
        var newRight = CastEnumMemberToUnderlying(node.Right, right) ?? right;
        if (newLeft == left && newRight == right)
            return null;

        var rebuilt = (BinaryExpressionSyntax)node.Update(
            newLeft, node.OperatorToken, newRight);
        CountBinaryRewrite(kind);
        IncrementRewrite();
        return rebuilt;
    }

    private ExpressionSyntax? CastEnumMemberToUnderlying(
        ExpressionSyntax original, ExpressionSyntax visited)
    {
        var stripped = StripParentheses(original);
        if (stripped is CastExpressionSyntax) return null;
        if (_semanticModel?.GetSymbolInfo(stripped).Symbol is not IFieldSymbol field)
            return null;
        if (field.ContainingType is not { TypeKind: TypeKind.Enum } enumType)
            return null;
        if (!IsSourceDeclaredEnum(enumType)) return null;
        var underlying = GetUnderlyingType(enumType);
        if (underlying == null) return null;
        return CastToUnderlying(visited, underlying);
    }

    /// <summary>
    /// p is E.M → p == E.M。只处理左值同 enum 类型、右值为该 enum 成员常量
    /// 的经典 IsExpression；p is E（类型测试）与可空左值保持原样。
    /// 外部元数据 enum 同样改写：Java 侧只允许同类 enum 的 ==/!=，兼容常量面
    /// 已把成员映射为常量，常量模式残留会被门禁硬失败。
    /// </summary>
    private BinaryExpressionSyntax? RewriteEnumIsExpression(
        BinaryExpressionSyntax node, ExpressionSyntax left, ExpressionSyntax right)
    {
        var enumType = GetEnumType(node.Left);
        if (enumType == null || IsNullableEnum(node.Left))
            return null;

        var symbol = _semanticModel?.GetSymbolInfo(StripParentheses(node.Right)).Symbol;
        if (symbol is not IFieldSymbol field
            || !SymbolEqualityComparer.Default.Equals(field.ContainingType, enumType))
        {
            return null;
        }

        var token = SyntaxFactory.Token(SyntaxKind.EqualsEqualsToken)
            .WithLeadingTrivia(node.OperatorToken.LeadingTrivia)
            .WithTrailingTrivia(node.OperatorToken.TrailingTrivia);
        IncrementRewrite();
        return SyntaxFactory.BinaryExpression(
            SyntaxKind.EqualsExpression, left, token, right);
    }

    /// <summary>
    /// 所有源内声明 enum 的算术降级（plain 路径之后的通用分支）：
    /// <list type="bullet">
    /// <item><c>E - E</c> → <c>(U)a - (U)b</c>（结果为 U，不再回挂 (E)；
    /// 窄底层用 <c>unchecked((U)(...))</c> 保住按底层宽度回绕的语义）。</item>
    /// <item><c>E +/- n</c> / <c>n +/- E</c> → <c>(E)((U)e +/- n)</c> /
    /// <c>(E)(n +/- (U)e)</c>（窄底层 <c>unchecked</c>）。</item>
    /// </list>
    /// 结果为 enum 才回挂 (E)，结果为整数时只保留 (U)。无法由语义模型确认
    /// 结果类型的站点不改写（宁可 residual，也不产出坏代码）。
    /// </summary>
    private SyntaxNode? RewriteSourceEnumArithmetic(
        BinaryExpressionSyntax node, ExpressionSyntax left, ExpressionSyntax right)
    {
        // 与 plain 路径一致：checked 上下文内原 enum 算术的溢出语义与降级后的
        // 整数算术不同，保持原样由后续 gate 处理。
        if (IsInCheckedContext(node))
            return null;

        var kind = node.Kind();
        var leftEnum = GetEnumType(node.Left);
        var rightEnum = GetEnumType(node.Right);

        // E - E → (U)a - (U)b
        if (kind is SyntaxKind.SubtractExpression
            && leftEnum != null && rightEnum != null)
        {
            if (!SymbolEqualityComparer.Default.Equals(leftEnum, rightEnum)
                || !IsSourceDeclaredEnum(leftEnum)
                || IsNullableEnum(node.Left) || IsNullableEnum(node.Right))
            {
                return null;
            }

            var resultType = _semanticModel?.GetTypeInfo(node).Type;
            if (!IsIntegralType(resultType))
                return null;

            var underlying = GetUnderlyingType(leftEnum);
            if (underlying == null) return null;

            var l = CastEnumToUnderlying(left, node.Left, leftEnum);
            var r = CastEnumToUnderlying(right, node.Right, leftEnum);
            var sub = SyntaxFactory.BinaryExpression(
                SyntaxKind.SubtractExpression, l, node.OperatorToken, r);

            ExpressionSyntax result = IsNarrowType(leftEnum)
                ? SyntaxFactory.CheckedExpression(
                    SyntaxKind.UncheckedExpression, CastToUnderlying(sub, underlying))
                : sub;

            _stats.PlainEnumArithmeticRewritten++;
            IncrementRewrite();
            return EnsureParenthesized(result);
        }

        // 恰一侧 enum、另一侧整数：E +/- n / n +/- E。E + E 不是合法 C#。
        if (leftEnum != null && rightEnum != null)
            return null;

        var enumSideIsLeft = leftEnum != null;
        var enumSide = enumSideIsLeft ? node.Left : node.Right;
        var otherSide = enumSideIsLeft ? node.Right : node.Left;
        var enumType = enumSideIsLeft ? leftEnum : rightEnum;
        if (enumType == null || !IsSourceDeclaredEnum(enumType)
            || IsNullableEnum(enumSide))
        {
            return null;
        }

        var otherType = _semanticModel?.GetTypeInfo(otherSide).Type;
        if (!IsIntegralType(otherType))
            return null;

        var nodeType = _semanticModel?.GetTypeInfo(node).Type;
        if (nodeType == null
            || !nodeType.Equals(enumType, SymbolEqualityComparer.Default))
        {
            return null;
        }

        var enumUnderlying = GetUnderlyingType(enumType);
        if (enumUnderlying == null) return null;

        var visitedEnum = enumSideIsLeft ? left : right;
        var visitedOther = enumSideIsLeft ? right : left;
        var castedEnum = CastToUnderlying(visitedEnum, enumUnderlying);
        var inner = enumSideIsLeft
            ? SyntaxFactory.BinaryExpression(kind, castedEnum, node.OperatorToken, visitedOther)
            : SyntaxFactory.BinaryExpression(kind, visitedOther, node.OperatorToken, castedEnum);

        ExpressionSyntax enumResult = CastToEnum(inner, enumType, node);
        if (IsNarrowType(enumType))
        {
            enumResult = SyntaxFactory.CheckedExpression(
                SyntaxKind.UncheckedExpression, enumResult);
        }

        _stats.PlainEnumArithmeticRewritten++;
        IncrementRewrite();
        return EnsureParenthesized(enumResult);
    }

    private void CountBinaryRewrite(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.BitwiseOrExpression: _stats.BitwiseOrRewritten++; break;
            case SyntaxKind.BitwiseAndExpression: _stats.BitwiseAndRewritten++; break;
            case SyntaxKind.ExclusiveOrExpression: _stats.BitwiseXorRewritten++; break;
        }
    }

    /// <summary>
    /// plain 真 enum 的算术降级：
    /// <c>E - E</c> → <c>(int)a - (int)b</c>（C# 结果本就是底层 int，语义不变；
    /// 回归 xml-tiny XPathNavigator 的 <c>(uint)(type - XPathNodeType.Text)</c>）；
    /// <c>E + i / i + E</c> → <c>(E)((int)e + i)</c>（C# 结果是 enum，必须回挂 (E)
    /// 转换才能保持赋值/传参面）。可空操作数、非 plain enum 返回 null（站点让位）。
    /// </summary>
    private SyntaxNode? RewritePlainEnumArithmetic(
        BinaryExpressionSyntax node, ExpressionSyntax left, ExpressionSyntax right)
    {
        var leftEnum = GetEnumType(node.Left);
        var rightEnum = GetEnumType(node.Right);

        // C# 的 E - E / E + i 按规范永不溢出检查（E-E 在底层域无检查计算）；降为
        // (int) 算术后在 checked 上下文里可能新抛 OverflowException，语义漂移——
        // checked 上下文内的算术站点保持原样。
        if (IsInCheckedContext(node))
        {
            return null;
        }

        if (node.IsKind(SyntaxKind.SubtractExpression))
        {
            if (leftEnum == null || rightEnum == null
                || !IsPlainRealEnum(leftEnum) || !IsPlainRealEnum(rightEnum)
                || IsNullableEnum(node.Left) || IsNullableEnum(node.Right))
            {
                return null;
            }
            var newLeft = CastEnumToUnderlying(left, node.Left, leftEnum);
            var newRight = CastEnumToUnderlying(right, node.Right, rightEnum);
            _stats.PlainEnumArithmeticRewritten++;
            IncrementRewrite();
            return node.Update(newLeft, node.OperatorToken, newRight);
        }

        // E + i / i + E：恰一侧 plain enum，另一侧为参与加法的非 enum 操作数。
        var enumSideIsLeft = leftEnum != null && rightEnum == null;
        var enumSide = enumSideIsLeft ? node.Left : node.Right;
        var enumType = enumSideIsLeft ? leftEnum : rightEnum;
        if (enumType == null
            || (leftEnum != null && rightEnum != null)
            || !IsPlainRealEnum(enumType)
            || IsNullableEnum(enumSide))
        {
            return null;
        }

        // 另一侧是 string 时整个站点是字符串拼接（string + enum 是合法 C#，
        // enum 操作数走 ToString/装箱面），不是 enum 算术：既不能交换操作数
        // （拼接不满足交换律）也不能套 (E) cast。回归 xml-tiny XmlEventCache/
        // WriterOutput/XsdBuilder 的 Debug.Assert(false, "..." + eventType)——
        // 曾被降级为 (E)((int)e + "...") 触发 CS0030。
        var otherSide = enumSideIsLeft ? node.Right : node.Left;
        if (_semanticModel?.GetTypeInfo(otherSide).Type?.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        var visitedEnum = enumSideIsLeft ? left : right;
        var visitedOther = enumSideIsLeft ? right : left;
        var castedEnum = CastEnumToUnderlying(visitedEnum, enumSide, enumType);
        var sum = SyntaxFactory.BinaryExpression(
            SyntaxKind.AddExpression, castedEnum,
            SyntaxFactory.Token(SyntaxKind.PlusToken), visitedOther);
        _stats.PlainEnumArithmeticRewritten++;
        IncrementRewrite();
        return CastToEnum(sum, enumType, node);
    }

    /// <summary>
    /// Lowers enum operands in equality/relational binary expressions to their
    /// underlying integer type. The only case left untouched is same-type-enum
    /// == / != (Java enums support reference equality there). Everything else
    /// (enum vs int, enum vs other enum, relational comparisons) trips the
    /// BinaryExpressionTransformer gate unless lowered here.
    /// </summary>
    private BinaryExpressionSyntax RewriteEnumComparison(
        BinaryExpressionSyntax node, ExpressionSyntax left, ExpressionSyntax right, SyntaxKind kind)
    {
        var leftEnum = GetEnumType(node.Left);
        var rightEnum = GetEnumType(node.Right);

        if (leftEnum == null && rightEnum == null)
            return (BinaryExpressionSyntax)node.Update(left, node.OperatorToken, right);

        var isEquality = kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

        // 同 enum 相等/不等 → Java enums 支持 == / !=；保持原样。
        if (isEquality && leftEnum != null && rightEnum != null
            && SymbolEqualityComparer.Default.Equals(leftEnum, rightEnum))
        {
            return (BinaryExpressionSyntax)node.Update(left, node.OperatorToken, right);
        }

        // 可空 enum vs null 字面量是 HasValue 判空（Java 引用判空直译），不是值
        // 比较：降为 (U)x.Value != null 既不合法也改变语义，必须保持原样。
        // （回归 regular-expressions RegexParserTests.ParseTree 的 `error != null`：
        // 脏检查纳入关系站点后，该形态曾是 (int)error.Value != null 残留，触发
        // numeric-boundary 的 CS2J5355。）
        if (node.Left.IsKind(SyntaxKind.NullLiteralExpression)
            || node.Right.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return (BinaryExpressionSyntax)node.Update(left, node.OperatorToken, right);
        }

        // Lower any enum operand to its underlying type.
        var newLeft = leftEnum != null ? CastEnumToUnderlying(left, node.Left, leftEnum) : left;
        var newRight = rightEnum != null ? CastEnumToUnderlying(right, node.Right, rightEnum) : right;

        IncrementRewrite();
        return (BinaryExpressionSyntax)node.Update(newLeft, node.OperatorToken, newRight);
    }

    /// <summary>
    /// Casts an enum-typed operand to its underlying type. Handles nullable
    /// enums (E?) by lifting through .Value before casting.
    /// </summary>
    private ExpressionSyntax CastEnumToUnderlying(
        ExpressionSyntax visited, ExpressionSyntax original, INamedTypeSymbol enumType)
    {
        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
            return visited;

        // Nullable enum: (E?)x → (U)x.Value  (null comparison is already unwrapped
        // by lifted C# semantics, but here we just need the ordinal of the value).
        if (IsNullableEnum(original))
        {
            var valueAccess = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                visited,
                SyntaxFactory.IdentifierName("Value"));
            return CastToUnderlying(valueAccess, underlying);
        }

        return CastToUnderlying(visited, underlying);
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

        var enumName = EnumTypeSyntax(enumType, node.SpanStart);
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
            EnsureParenthesized(CastToEnum(innerOp, enumType, node)));
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

        // Gate on the node's OWN bound type: ~Member inside an enum member
        // initializer binds to the underlying integer operator and must be
        // left untouched (see VisitBinaryExpression).
        var enumType = GetEnumType(node);
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
            var enumName = EnumTypeSyntax(enumType, node.SpanStart);
            var nullableEnum = SyntaxFactory.NullableType(enumName);
            var trueExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                EnsureParenthesized(CastToEnum(narrowed, enumType, node)));
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
                : CastToEnum(uncheckedExpr, enumType, node);
        }
        else
        {
            // (E)(~(U)X)
            result = ShouldSkipOuterCast(node)
                ? notExpr2
                : CastToEnum(notExpr2, enumType, node);
        }

        _stats.BitwiseNotRewritten++;
        IncrementRewrite();
        return EnsureParenthesized(result);
    }

    // ── Compound assignment ──

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // Visit both sides: the target expression can itself carry enum
        // operations (e.g. an enum bitwise index argument of an indexer
        // compound assignment).
        var visitedLeft = (ExpressionSyntax?)Visit(node.Left);
        var left = visitedLeft ?? node.Left;
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var right = visitedRight ?? node.Right;

        var isBitwiseCompound = node.IsKind(SyntaxKind.OrAssignmentExpression)
            || node.IsKind(SyntaxKind.AndAssignmentExpression)
            || node.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression);
        var isArithmeticCompound = node.IsKind(SyntaxKind.AddAssignmentExpression)
            || node.IsKind(SyntaxKind.SubtractAssignmentExpression);
        if (!isBitwiseCompound && !isArithmeticCompound)
            return node.Update(left, node.OperatorToken, right);

        // Compound assignment to enum — only rewrite if target is stable
        var enumType = GetEnumType(node.Left);
        if (enumType == null)
            return node.Update(left, node.OperatorToken, right);

        // 算术复合赋值（E += n / E -= n）只处理源内声明 enum；checked 上下文
        // 的溢出语义变化不做降级。
        if (isArithmeticCompound
            && (!IsSourceDeclaredEnum(enumType) || IsInCheckedContext(node)))
        {
            return node.Update(left, node.OperatorToken, right);
        }

        var underlying = GetUnderlyingType(enumType);
        if (underlying == null)
        {
            AddWarning($"Cannot resolve underlying type for enum '{enumType.Name}' in compound assignment; skipping.");
            return node.Update(left, node.OperatorToken, right);
        }

        // 算术复合赋值的右操作数必须是整数（E += E / E += string 不降级）。
        if (isArithmeticCompound)
        {
            if (GetEnumType(node.Right) != null
                || !IsIntegralType(_semanticModel?.GetTypeInfo(node.Right).Type))
            {
                return node.Update(left, node.OperatorToken, right);
            }
        }

        // Nullable compound assignments read the target up to three times;
        // require a stable reference for them. Non-nullable compound
        // assignments can additionally expand to any target that is safe to
        // duplicate (property with a deterministic receiver, array/indexer
        // element with deterministic receiver and side-effect-free indices),
        // since reading such a target twice is observationally equivalent.
        var nullableTarget = IsNullableEnum(node.Left);
        if (nullableTarget)
        {
            if (isArithmeticCompound)
            {
                // 可空 enum 的 lifted 算术不在本 pass 承诺范围，保持原样。
                return node.Update(left, node.OperatorToken, right);
            }
            if (!IsStableReference(node.Left))
            {
                AddWarning($"Nullable enum compound assignment target is not a stable reference; skipping rewrite.");
                return node.Update(left, node.OperatorToken, right);
            }
        }
        else if (!IsSafelyExpandableTarget(node.Left))
        {
            AddWarning($"Compound assignment target is not a stable reference; skipping rewrite.");
            return node.Update(left, node.OperatorToken, right);
        }

        var (binKind, binToken) = node.Kind() switch
        {
            SyntaxKind.OrAssignmentExpression => (SyntaxKind.BitwiseOrExpression, SyntaxFactory.Token(SyntaxKind.BarToken)),
            SyntaxKind.AndAssignmentExpression => (SyntaxKind.BitwiseAndExpression, SyntaxFactory.Token(SyntaxKind.AmpersandToken)),
            SyntaxKind.ExclusiveOrAssignmentExpression => (SyntaxKind.ExclusiveOrExpression, SyntaxFactory.Token(SyntaxKind.CaretToken)),
            SyntaxKind.AddAssignmentExpression => (SyntaxKind.AddExpression, SyntaxFactory.Token(SyntaxKind.PlusToken)),
            SyntaxKind.SubtractAssignmentExpression => (SyntaxKind.SubtractExpression, SyntaxFactory.Token(SyntaxKind.MinusToken)),
            _ => throw new InvalidOperationException(),
        };

        // Handle nullable enum compound assignment
        if (nullableTarget)
        {
            var rightCasted = MaybeCastToUnderlying(right, node.Right, enumType, underlying);
            var leftValue = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                left,
                SyntaxFactory.IdentifierName("Value"));
            var leftCasted = CastToUnderlying(leftValue, underlying);

            var innerOp = SyntaxFactory.BinaryExpression(binKind, leftCasted, binToken, rightCasted);
            var enumName = EnumTypeSyntax(enumType, node.SpanStart);
            var nullableEnum = SyntaxFactory.NullableType(enumName);
            var trueExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                EnsureParenthesized(CastToEnum(innerOp, enumType, node)));
            var falseExpr = SyntaxFactory.CastExpression(
                nullableEnum,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

            var ternary = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    left,
                    SyntaxFactory.IdentifierName("HasValue")),
                trueExpr,
                falseExpr);

            _stats.CompoundAssignRewritten++;
            _stats.NullableLiftedRewritten++;
            IncrementRewrite();
            return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                left,
                SyntaxFactory.ParenthesizedExpression(ternary));
        }

        // Non-nullable: x |= R → x = (E)((U)x | R'); x is emitted twice,
        // which is equivalent for targets that passed IsSafelyExpandableTarget.
        var leftCasted2 = CastToUnderlying(left, underlying);
        var rightCasted2 = MaybeCastToUnderlying(right, node.Right, enumType, underlying);

        var binExpr = SyntaxFactory.BinaryExpression(binKind, leftCasted2, binToken, rightCasted2);
        var castedResult = MaybeUnchecked(
            CastToEnum(binExpr, enumType, node), enumType);

        _stats.CompoundAssignRewritten++;
        IncrementRewrite();
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            left,
            EnsureParenthesized(castedResult));
    }

    // ── plain 真 enum 的算术/关系站点 ──

    // ── HasFlag ──

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Process arguments first (bottom-up)
        var visitedArgs = (ArgumentListSyntax?)Visit(node.ArgumentList)
                          ?? node.ArgumentList;

        // Check if this is a HasFlag call: x.HasFlag(f)
        if (node.Expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.ValueText == "HasFlag"
            && node.ArgumentList.Arguments.Count == 1)
        {
            var originalArg = node.ArgumentList.Arguments[0].Expression;
            var receiverEnum = GetEnumType(ma.Expression);
            var argEnum = receiverEnum == null ? null : GetEnumType(originalArg);

            // Skip paths: the receiver chain was not visited yet; visit it so
            // enum ops nested inside the receiver are still rewritten.
            ExpressionSyntax Skipped()
            {
                var visitedSkipped = (ExpressionSyntax?)Visit(node.Expression);
                return visitedSkipped ?? node.Expression;
            }

            if (receiverEnum == null)
                return node.Update(Skipped(), visitedArgs);

            // Both must be the same enum type
            if (argEnum == null
                || !argEnum.Equals(receiverEnum, SymbolEqualityComparer.Default))
            {
                AddWarning($"HasFlag argument type does not match receiver enum type; skipping rewrite.");
                return node.Update(Skipped(), visitedArgs);
            }

            var underlying = GetUnderlyingType(receiverEnum);
            if (underlying == null)
            {
                AddWarning($"Cannot resolve underlying type for enum '{receiverEnum.Name}' in HasFlag; skipping.");
                return node.Update(Skipped(), visitedArgs);
            }

            // Handle nullable receiver
            if (IsNullableEnum(ma.Expression))
            {
                AddWarning($"Nullable receiver in HasFlag call; skipping rewrite.");
                return node.Update(Skipped(), visitedArgs);
            }

            // Rewrite: x.HasFlag(f) → ((U)x & (U)f) == (U)f
            var visitedReceiver = (ExpressionSyntax?)Visit(ma.Expression);
            var receiver = visitedReceiver ?? ma.Expression;
            var arg = visitedArgs.Arguments[0].Expression;

            var receiverCasted = MaybeCastToUnderlying(receiver, ma.Expression, receiverEnum, underlying);
            var argCasted = MaybeCastToUnderlying(arg, originalArg, receiverEnum, underlying);
            // Use the same casted arg for comparison
            var argCastedForCompare = MaybeCastToUnderlying(arg, originalArg, receiverEnum, underlying);

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

        // Non-HasFlag invocation: the receiver chain must also be visited —
        // enum ops nested in the receiver (e.g.
        // new Regex(pattern, options | compiled).IsMatch(input)) otherwise
        // survive the pass untouched and trip the residual gate.
        var visitedExpression = (ExpressionSyntax?)Visit(node.Expression);
        var expression = visitedExpression ?? node.Expression;
        return node.Update(expression, visitedArgs);
    }
}
