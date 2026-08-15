using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

internal sealed class UnsignedTypeSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly UnsignedTypeRewriterOptions _options;
    private readonly UnsignedTypeRewriterStatistics _stats;
    private readonly List<UnsignedTypeRewriterDiagnostic> _diagnostics;

    public bool UsedExtensions { get; private set; }

    private static readonly Dictionary<SyntaxKind, string> KeywordToFullName = new()
    {
        { SyntaxKind.ByteKeyword,   "System.Byte"   },
        { SyntaxKind.UShortKeyword, "System.UInt16" },
        { SyntaxKind.UIntKeyword,   "System.UInt32" },
        { SyntaxKind.ULongKeyword,  "System.UInt64" },
    };

    private static readonly Dictionary<SyntaxKind, string> BinaryOpToMethod = new()
    {
        { SyntaxKind.AddExpression,         "Add"       },
        { SyntaxKind.SubtractExpression,    "Subtract"  },
        { SyntaxKind.MultiplyExpression,    "Multiply"  },
        { SyntaxKind.DivideExpression,      "Divide"    },
        { SyntaxKind.ModuloExpression,      "Remainder" },
        { SyntaxKind.BitwiseAndExpression,  "And"       },
        { SyntaxKind.BitwiseOrExpression,   "Or"        },
        { SyntaxKind.ExclusiveOrExpression, "Xor"       },
        { SyntaxKind.LeftShiftExpression,   "LeftShift" },
        { SyntaxKind.RightShiftExpression,  "RightShift"},
    };

    private static readonly Dictionary<SyntaxKind, string> CompoundAssignmentToMethod = new()
    {
        { SyntaxKind.AddAssignmentExpression,         "Add"       },
        { SyntaxKind.SubtractAssignmentExpression,    "Subtract"  },
        { SyntaxKind.MultiplyAssignmentExpression,    "Multiply"  },
        { SyntaxKind.DivideAssignmentExpression,      "Divide"    },
        { SyntaxKind.ModuloAssignmentExpression,      "Remainder" },
        { SyntaxKind.AndAssignmentExpression,         "And"       },
        { SyntaxKind.OrAssignmentExpression,          "Or"        },
        { SyntaxKind.ExclusiveOrAssignmentExpression, "Xor"       },
        { SyntaxKind.LeftShiftAssignmentExpression,   "LeftShift" },
        { SyntaxKind.RightShiftAssignmentExpression,  "RightShift"},
    };

    public UnsignedTypeSyntaxRewriter(
        SemanticModel? semanticModel,
        UnsignedTypeRewriterOptions options,
        UnsignedTypeRewriterStatistics stats,
        List<UnsignedTypeRewriterDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantTokens().Any(t => KeywordToFullName.ContainsKey(t.Kind()));

    internal static bool ContainsUnsignedKeyword(SyntaxNode node)
        => node.DescendantTokens().Any(t => KeywordToFullName.ContainsKey(t.Kind()));

    private static bool IsUnsignedType(ITypeSymbol? type)
        => type?.SpecialType is SpecialType.System_Byte
            or SpecialType.System_UInt16
            or SpecialType.System_UInt32
            or SpecialType.System_UInt64;

    private bool IsUnsignedExpression(ExpressionSyntax expression)
        => _semanticModel != null && IsUnsignedType(_semanticModel.GetTypeInfo(expression).Type);

    /// <summary>
    /// True when the nearest enclosing checked/unchecked construct is checked.
    /// Rewriting arithmetic/increment/cast to unchecked extension calls inside a
    /// checked context would silently change overflow behavior, so those rewrites
    /// are suppressed there.
    /// </summary>
    private static bool IsInCheckedContext(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            if (current is CheckedExpressionSyntax checkedExpression)
            {
                return checkedExpression.IsKind(SyntaxKind.CheckedExpression);
            }

            if (current is CheckedStatementSyntax checkedStatement)
            {
                return checkedStatement.IsKind(SyntaxKind.CheckedStatement);
            }
        }

        return false;
    }

    /// <summary>
    /// True when the expression must be a compile-time constant (const field/local
    /// initializers, enum member values, attribute arguments, default parameter
    /// values, case labels, and auto-property initializers). Rewriting operators or
    /// casts there into extension method calls would produce CS0133-style errors,
    /// so those rewrites are suppressed; the native operators remain valid on the
    /// replaced System.* type names.
    /// </summary>
    private static bool IsInConstantExpressionContext(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case FieldDeclarationSyntax fieldDeclaration
                    when fieldDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword)
                         && fieldDeclaration.Declaration.Variables
                             .Any(variable => variable.Initializer?.Span.Contains(node.Span) == true):
                    return true;
                case LocalDeclarationStatementSyntax localDeclaration
                    when localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword)
                         && localDeclaration.Declaration.Variables
                             .Any(variable => variable.Initializer?.Span.Contains(node.Span) == true):
                    return true;
                case EnumMemberDeclarationSyntax enumMember
                    when enumMember.EqualsValue?.Span.Contains(node.Span) == true:
                    return true;
                case AttributeArgumentSyntax attributeArgument
                    when attributeArgument.Expression.Span.Contains(node.Span):
                    return true;
                case ParameterSyntax parameter
                    when parameter.Default?.Span.Contains(node.Span) == true:
                    return true;
                case CaseSwitchLabelSyntax caseLabel
                    when caseLabel.Value.Span.Contains(node.Span):
                    return true;
                case PropertyDeclarationSyntax propertyDeclaration
                    when propertyDeclaration.Initializer?.Span.Contains(node.Span) == true:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Only locals, parameters, and fields are safe to rewrite into
    /// `target = target.Add(...)`-style expressions. Properties and indexers must
    /// stay native because rewriting would evaluate their getter/receiver twice.
    /// </summary>
    private bool IsSafeAssignmentTarget(ExpressionSyntax target)
    {
        if (_semanticModel == null)
        {
            return false;
        }

        return target switch
        {
            IdentifierNameSyntax identifier =>
                _semanticModel.GetSymbolInfo(identifier).Symbol is ILocalSymbol
                    or IParameterSymbol
                    or IFieldSymbol,
            MemberAccessExpressionSyntax memberAccess =>
                _semanticModel.GetSymbolInfo(memberAccess).Symbol is IFieldSymbol
                && IsSideEffectFreeReceiver(memberAccess.Expression),
            _ => false,
        };
    }

    private static bool IsSideEffectFreeReceiver(ExpressionSyntax receiver) => receiver switch
    {
        ThisExpressionSyntax => true,
        BaseExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax memberAccess => IsSideEffectFreeReceiver(memberAccess.Expression),
        _ => false,
    };

    /// <summary>
    /// True when the node is evaluated more than once (loop condition/incrementor)
    /// or deferred (lambda/local function/query), where hoisting or in-place
    /// value-changing rewrites would break semantics.
    /// </summary>
    private static bool IsInRepeatedEvaluationContext(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case WhileStatementSyntax whileStatement when whileStatement.Condition.Span.Contains(node.Span):
                    return true;
                case DoStatementSyntax doStatement when doStatement.Condition.Span.Contains(node.Span):
                    return true;
                case ForStatementSyntax forStatement
                    when (forStatement.Condition?.Span.Contains(node.Span) == true
                          || forStatement.Incrementors.Any(i => i.Span.Contains(node.Span))):
                    return true;
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case QueryExpressionSyntax:
                    return true;
            }
        }

        return false;
    }

    private static bool IsBinaryOperatorRewriteKind(SyntaxKind kind)
        => BinaryOpToMethod.ContainsKey(kind)
           || kind is SyntaxKind.EqualsExpression
               or SyntaxKind.NotEqualsExpression
               or SyntaxKind.LessThanExpression
               or SyntaxKind.LessThanOrEqualExpression
               or SyntaxKind.GreaterThanExpression
               or SyntaxKind.GreaterThanOrEqualExpression;

    private static ExpressionSyntax Clone(ExpressionSyntax expression)
        => SyntaxFactory.ParseExpression(expression.ToString());

    private static InvocationExpressionSyntax Invoke(
        ExpressionSyntax receiver, string method, ExpressionSyntax? argument = null)
    {
        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            EnsureReceiver(receiver),
            SyntaxFactory.IdentifierName(method));

        var argumentList = argument == null
            ? SyntaxFactory.ArgumentList()
            : SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { SyntaxFactory.Argument(argument) }));

        return SyntaxFactory.InvocationExpression(memberAccess, argumentList);
    }

    private static ExpressionSyntax EnsureReceiver(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax
            or ParenthesizedExpressionSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or LiteralExpressionSyntax => receiver,
        _ => SyntaxFactory.ParenthesizedExpression(receiver),
    };

    private static ExpressionSyntax LiteralZero()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));

    private static ExpressionSyntax LiteralOne()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));

    private static SyntaxKind ComparisonKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.LessThanExpression => SyntaxKind.LessThanExpression,
        SyntaxKind.LessThanOrEqualExpression => SyntaxKind.LessThanOrEqualExpression,
        SyntaxKind.GreaterThanExpression => SyntaxKind.GreaterThanExpression,
        _ => SyntaxKind.GreaterThanOrEqualExpression,
    };

    public override SyntaxNode? VisitPredefinedType(PredefinedTypeSyntax node)
    {
        if (!KeywordToFullName.TryGetValue(node.Keyword.Kind(), out var fullName))
            return base.VisitPredefinedType(node);

        _stats.TypesReplaced++;
        return SyntaxFactory.ParseTypeName(fullName).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // `uint.MaxValue` / `uint.Parse(...)` parse with the keyword token as an
        // IdentifierName; replacing it produces System.UInt32.MaxValue.
        if (KeywordToFullName.TryGetValue(node.Identifier.Kind(), out var fullName))
        {
            _stats.TypesReplaced++;
            return SyntaxFactory.ParseTypeName(fullName).WithTriviaFrom(node);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitArrayType(ArrayTypeSyntax node)
    {
        if (ContainsUnsignedKeyword(node.ElementType))
            _stats.ArraysRewritten++;
        return base.VisitArrayType(node);
    }

    public override SyntaxNode? VisitTypeArgumentList(TypeArgumentListSyntax node)
    {
        if (node.Arguments.Any(ContainsUnsignedKeyword))
            _stats.GenericArgsReplaced++;
        return base.VisitTypeArgumentList(node);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var shouldRewrite = ShouldRewriteBinary(node);

        var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (!shouldRewrite)
            return rewritten;

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        return rewritten.Kind() switch
        {
            SyntaxKind.EqualsExpression => Invoke(rewritten.Left, "Equals", rewritten.Right),
            SyntaxKind.NotEqualsExpression => SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Invoke(rewritten.Left, "Equals", rewritten.Right)),
            SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression =>
                SyntaxFactory.BinaryExpression(
                    ComparisonKind(rewritten.Kind()),
                    Invoke(rewritten.Left, "CompareTo", rewritten.Right),
                    LiteralZero()),
            _ => Invoke(rewritten.Left, BinaryOpToMethod[rewritten.Kind()], rewritten.Right),
        };
    }

    private bool ShouldRewriteBinary(BinaryExpressionSyntax node)
    {
        if (_semanticModel == null
            || !_options.EnableOperatorRewrite
            || !IsBinaryOperatorRewriteKind(node.Kind())
            || IsInConstantExpressionContext(node))
        {
            return false;
        }

        // checked contexts must keep native overflow behavior.
        if (IsInCheckedContext(node))
        {
            return false;
        }

        var leftType = _semanticModel.GetTypeInfo(node.Left).Type;
        if (!IsUnsignedType(leftType))
        {
            return false;
        }

        // byte/ushort arithmetic and bitwise operators promote to int in C#.
        // Only equality/comparison (bool results) may be rewritten safely.
        var isEqualityOrComparison = node.Kind() is SyntaxKind.EqualsExpression
            or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression
            or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression
            or SyntaxKind.GreaterThanOrEqualExpression;
        if (leftType.SpecialType is SpecialType.System_Byte or SpecialType.System_UInt16
            && !isEqualityOrComparison)
        {
            return false;
        }

        // Shift extension methods take an int shift count and only uint/ulong
        // keep their result type; byte/ushort shifts promote to int, so they stay
        // native.
        if (node.Kind() is SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression)
        {
            return leftType.SpecialType is SpecialType.System_UInt32 or SpecialType.System_UInt64;
        }

        // The extension method takes the same unsigned type as the receiver.
        // A numeric literal is only fine when it converts to the receiver's type
        // (e.g. `a + 1`), not when it promotes the operation (e.g. `a + 1L`).
        var rightType = _semanticModel.GetTypeInfo(node.Right).Type;
        if (IsUnsignedType(rightType))
        {
            return rightType!.SpecialType == leftType!.SpecialType;
        }

        if (node.Right is LiteralExpressionSyntax)
        {
            return _semanticModel.GetTypeInfo(node.Right).ConvertedType?.SpecialType
                   == leftType!.SpecialType;
        }

        return false;
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var leftType = _semanticModel?.GetTypeInfo(node.Left).Type;
        var shouldRewrite = _options.EnableOperatorRewrite
            && CompoundAssignmentToMethod.ContainsKey(node.Kind())
            && !IsInCheckedContext(node)
            && IsUnsignedType(leftType)
            && IsSafeAssignmentTarget(node.Left)
            && IsCompatibleOperand(node.Right, leftType!);

        var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
        if (!shouldRewrite)
            return rewritten;

        UsedExtensions = true;
        _stats.OperatorsRewritten++;
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            rewritten.Left,
            Invoke(Clone(rewritten.Left), CompoundAssignmentToMethod[rewritten.Kind()], rewritten.Right));
    }

    private bool IsCompatibleOperand(ExpressionSyntax operand, ITypeSymbol leftType)
    {
        if (_semanticModel == null)
        {
            return false;
        }

        var operandType = _semanticModel.GetTypeInfo(operand).Type;
        if (IsUnsignedType(operandType))
        {
            return operandType!.SpecialType == leftType.SpecialType;
        }

        return operand is LiteralExpressionSyntax
               && _semanticModel.GetTypeInfo(operand).ConvertedType?.SpecialType == leftType.SpecialType;
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        var method = node.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => "Add",
            SyntaxKind.PostDecrementExpression => "Subtract",
            _ => null,
        };

        if (method != null
            && !IsInCheckedContext(node)
            && !IsInRepeatedEvaluationContext(node)
            && IsUnsignedExpression(node.Operand)
            && IsSafeAssignmentTarget(node.Operand))
        {
            return base.VisitPostfixUnaryExpression(
                node.WithAdditionalAnnotations(UnsignedPostfixAnnotations.Postfix));
        }

        return base.VisitPostfixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.BitwiseNotExpression))
        {
            var operandType = _semanticModel?.GetTypeInfo(node.Operand).Type;
            var shouldRewriteNot = _options.EnableOperatorRewrite
                && !IsInCheckedContext(node)
                && operandType?.SpecialType is SpecialType.System_UInt32 or SpecialType.System_UInt64;

            var notRewritten = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
            if (!shouldRewriteNot)
                return notRewritten;

            UsedExtensions = true;
            _stats.OperatorsRewritten++;
            return Invoke(notRewritten.Operand, "Not");
        }

        var method = node.Kind() switch
        {
            SyntaxKind.PreIncrementExpression => "Add",
            SyntaxKind.PreDecrementExpression => "Subtract",
            _ => null,
        };
        var shouldRewrite = method != null
            && !IsInCheckedContext(node)
            && IsUnsignedExpression(node.Operand)
            && IsSafeAssignmentTarget(node.Operand);

        var rewritten = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
        if (!shouldRewrite || method == null)
            return rewritten;

        UsedExtensions = true;
        _stats.OperatorsRewritten++;
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            rewritten.Operand,
            Invoke(Clone(rewritten.Operand), method, LiteralOne()));
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        string? castMethod = null;
        if (_options.EnableCastRewrite
            && _semanticModel != null
            && !IsInCheckedContext(node)
            && !IsInConstantExpressionContext(node))
        {
            var sourceType = _semanticModel.GetTypeInfo(node.Expression).Type;
            if (IsUnsignedType(sourceType))
                castMethod = GetSignedCastMethod(node.Type, sourceType!);
        }

        var rewritten = (CastExpressionSyntax)base.VisitCastExpression(node)!;
        if (castMethod == null)
        {
            // `(byte)-1` / `(byte)*p` are valid, but after replacing the keyword
            // with the qualified name, the parser can treat `-`, `+`, `*` or `&`
            // as a binary operator after the parenthesized type and raises
            // CS0075/CS0119. Parenthesize those prefix unary operands.
            if (rewritten.Type is not PredefinedTypeSyntax
                && rewritten.Expression is PrefixUnaryExpressionSyntax prefix
                && (prefix.IsKind(SyntaxKind.UnaryMinusExpression)
                    || prefix.IsKind(SyntaxKind.UnaryPlusExpression)
                    || prefix.IsKind(SyntaxKind.PointerIndirectionExpression)
                    || prefix.IsKind(SyntaxKind.AddressOfExpression)))
            {
                return rewritten.WithExpression(
                    SyntaxFactory.ParenthesizedExpression(rewritten.Expression));
            }

            return rewritten;
        }

        UsedExtensions = true;
        _stats.CastsRewritten++;
        return Invoke(rewritten.Expression, castMethod);
    }

    private static string? GetSignedCastMethod(TypeSyntax targetType, ITypeSymbol sourceType)
    {
        if (targetType is not PredefinedTypeSyntax predefined)
            return null;

        return predefined.Keyword.Kind() switch
        {
            SyntaxKind.SByteKeyword => "ToSByte",
            SyntaxKind.ShortKeyword => "ToInt16",
            SyntaxKind.IntKeyword => "ToInt32",
            SyntaxKind.LongKeyword =>
                sourceType.SpecialType == SpecialType.System_UInt32 ? "ToInt64Unsigned" : "ToInt64",
            _ => null,
        };
    }

}
