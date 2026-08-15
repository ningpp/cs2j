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

    private static readonly Dictionary<SyntaxKind, string> BinaryOpMethod = new()
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

    private static readonly Dictionary<SyntaxKind, string> CompoundAssignmentMethod = new()
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

    private static bool IsNullableValueType(ITypeSymbol? type)
        => type is INamedTypeSymbol named
           && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static bool IsEnumType(ITypeSymbol? type)
        => type?.TypeKind == TypeKind.Enum;

    private static bool IsIntegralSpecial(SpecialType type) => type is
        SpecialType.System_SByte
        or SpecialType.System_Byte
        or SpecialType.System_Int16
        or SpecialType.System_UInt16
        or SpecialType.System_Int32
        or SpecialType.System_UInt32
        or SpecialType.System_Int64
        or SpecialType.System_UInt64
        or SpecialType.System_Char;

    private bool IsUnsignedExpression(ExpressionSyntax expression)
        => _semanticModel != null && IsUnsignedType(GetEffectiveType(expression));

    private ITypeSymbol? GetEffectiveType(ExpressionSyntax expression)
    {
        if (_semanticModel == null)
        {
            return null;
        }

        var info = _semanticModel.GetTypeInfo(expression);
        return info.ConvertedType ?? info.Type;
    }

    private SpecialType? GetNaturalSpecial(ExpressionSyntax expression)
        => _semanticModel?.GetTypeInfo(expression).Type?.SpecialType;

    private static SpecialType? GetSpecial(ExpressionSyntax expression, ITypeSymbol? type)
        => type?.SpecialType;

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
                case ArrayCreationExpressionSyntax arrayCreation
                    when arrayCreation.Initializer != null
                         && arrayCreation.Type.RankSpecifiers
                             .SelectMany(rank => rank.Sizes)
                             .Any(size => size.Span.Contains(node.Span)):
                    // 带初始化器的数组创建要求长度是常量表达式。
                    return true;
            }
        }

        return false;
    }

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

    private static bool IsBinaryOperatorRewriteKind(SyntaxKind kind)
        => BinaryOpMethod.ContainsKey(kind)
           || kind is SyntaxKind.EqualsExpression
               or SyntaxKind.NotEqualsExpression
               or SyntaxKind.LessThanExpression
               or SyntaxKind.LessThanOrEqualExpression
               or SyntaxKind.GreaterThanExpression
               or SyntaxKind.GreaterThanOrEqualExpression;

    private static UnsignedDomain GetBinaryDomain(SpecialType? left, SpecialType? right)
    {
        if (left == null || right == null)
        {
            return UnsignedDomain.Native;
        }

        if (left == SpecialType.System_UInt64 || right == SpecialType.System_UInt64)
        {
            return UnsignedDomain.UInt64;
        }

        if (left == SpecialType.System_Int64 || right == SpecialType.System_Int64)
        {
            return UnsignedDomain.Int64;
        }

        if (left == SpecialType.System_UInt32 || right == SpecialType.System_UInt32)
        {
            if (left == SpecialType.System_Int32 || right == SpecialType.System_Int32
                || left == SpecialType.System_Int16 || right == SpecialType.System_Int16
                || left == SpecialType.System_SByte || right == SpecialType.System_SByte)
            {
                return UnsignedDomain.Int64;
            }

            return UnsignedDomain.UInt32;
        }

        return UnsignedDomain.Int32;
    }

    private static UnsignedDomain GetShiftDomain(SpecialType? left) => left switch
    {
        SpecialType.System_Byte or SpecialType.System_UInt16 => UnsignedDomain.Int32,
        SpecialType.System_UInt32 => UnsignedDomain.UInt32,
        SpecialType.System_UInt64 => UnsignedDomain.UInt64,
        _ => UnsignedDomain.Native,
    };

    private static UnsignedDomain GetUnaryDomain(SpecialType? operand) => operand switch
    {
        SpecialType.System_Byte or SpecialType.System_UInt16 => UnsignedDomain.Int32,
        SpecialType.System_UInt32 => UnsignedDomain.UInt32,
        SpecialType.System_UInt64 => UnsignedDomain.UInt64,
        _ => UnsignedDomain.Native,
    };

    private static UnsignedDomain GetResultDomain(SpecialType? result) => result switch
    {
        SpecialType.System_Int32 => UnsignedDomain.Int32,
        SpecialType.System_Int64 => UnsignedDomain.Int64,
        SpecialType.System_UInt32 => UnsignedDomain.UInt32,
        SpecialType.System_UInt64 => UnsignedDomain.UInt64,
        _ => UnsignedDomain.Native,
    };

    private static ExpressionSyntax WrapResult(
        ExpressionSyntax call,
        UnsignedDomain domain,
        SpecialType? contextSpecial) => domain switch
    {
        UnsignedDomain.UInt32 => contextSpecial switch
        {
            SpecialType.System_UInt32 => UnsignedMathSyntax.UncheckedCast(SyntaxKind.UIntKeyword, call),
            SpecialType.System_Int64 => UnsignedMathSyntax.StaticCall("ToInt64UInt32", call),
            SpecialType.System_Double => UnsignedMathSyntax.StaticCall("ToDoubleUInt32", call),
            SpecialType.System_Single => UnsignedMathSyntax.StaticCall("ToSingleUInt32", call),
            SpecialType.System_Decimal => UnsignedMathSyntax.StaticCall("ToDecimalUInt32", call),
            _ => call,
        },
        UnsignedDomain.UInt64 => contextSpecial switch
        {
            SpecialType.System_UInt64 => UnsignedMathSyntax.UncheckedCast(SyntaxKind.ULongKeyword, call),
            SpecialType.System_Double => UnsignedMathSyntax.StaticCall("ToDoubleUInt64", call),
            SpecialType.System_Single => UnsignedMathSyntax.StaticCall("ToSingleUInt64", call),
            SpecialType.System_Decimal => UnsignedMathSyntax.StaticCall("ToDecimalUInt64", call),
            _ => call,
        },
        _ => call,
    };

    private static ExpressionSyntax LiteralZero()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));

    private static ExpressionSyntax LiteralOne()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));

    public override SyntaxNode? VisitPredefinedType(PredefinedTypeSyntax node)
    {
        if (!KeywordToFullName.TryGetValue(node.Keyword.Kind(), out var fullName))
        {
            return base.VisitPredefinedType(node);
        }

        _stats.TypesReplaced++;
        return SyntaxFactory.ParseTypeName(fullName).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
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
        {
            _stats.ArraysRewritten++;
        }

        return base.VisitArrayType(node);
    }

    public override SyntaxNode? VisitTypeArgumentList(TypeArgumentListSyntax node)
    {
        if (node.Arguments.Any(ContainsUnsignedKeyword))
        {
            _stats.GenericArgsReplaced++;
        }

        return base.VisitTypeArgumentList(node);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var isShift = node.IsKind(SyntaxKind.LeftShiftExpression)
                      || node.IsKind(SyntaxKind.RightShiftExpression);
        var leftNaturalSpecial = GetNaturalSpecial(node.Left) ?? SpecialType.None;
        var rightNaturalSpecial = GetNaturalSpecial(node.Right) ?? SpecialType.None;
        var resultContextSpecial = GetSpecial(node, GetEffectiveType(node));
        var domain = ShouldRewriteBinary(node, isShift, leftNaturalSpecial, rightNaturalSpecial);

        var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (domain == UnsignedDomain.Native)
        {
            return rewritten;
        }

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        if (isShift)
        {
            var method = BinaryOpMethod[rewritten.Kind()] + UnsignedMathSyntax.Suffix(domain);
            return WrapResult(
                UnsignedMathSyntax.StaticCall(
                    method,
                    UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
                    rewritten.Right),
                domain,
                resultContextSpecial);
        }

        if (rewritten.IsKind(SyntaxKind.EqualsExpression)
            || rewritten.IsKind(SyntaxKind.NotEqualsExpression)
            || rewritten.IsKind(SyntaxKind.LessThanExpression)
            || rewritten.IsKind(SyntaxKind.LessThanOrEqualExpression)
            || rewritten.IsKind(SyntaxKind.GreaterThanExpression)
            || rewritten.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
        {
            var compare = UnsignedMathSyntax.StaticCall(
                "Compare" + UnsignedMathSyntax.Suffix(domain),
                UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
                UnsignedMathSyntax.Normalize(rewritten.Right, domain, rightNaturalSpecial));
            var comparisonKind = rewritten.Kind() switch
            {
                SyntaxKind.EqualsExpression => SyntaxKind.EqualsExpression,
                SyntaxKind.NotEqualsExpression => SyntaxKind.NotEqualsExpression,
                SyntaxKind.LessThanExpression => SyntaxKind.LessThanExpression,
                SyntaxKind.LessThanOrEqualExpression => SyntaxKind.LessThanOrEqualExpression,
                SyntaxKind.GreaterThanExpression => SyntaxKind.GreaterThanExpression,
                _ => SyntaxKind.GreaterThanOrEqualExpression,
            };
            return SyntaxFactory.BinaryExpression(
                comparisonKind, compare, LiteralZero());
        }

        var binaryMethod = BinaryOpMethod[rewritten.Kind()] + UnsignedMathSyntax.Suffix(domain);
        return WrapResult(
            UnsignedMathSyntax.StaticCall(
                binaryMethod,
                UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
                UnsignedMathSyntax.Normalize(rewritten.Right, domain, rightNaturalSpecial)),
            domain,
            resultContextSpecial);
    }

    private UnsignedDomain ShouldRewriteBinary(
        BinaryExpressionSyntax node,
        bool isShift,
        SpecialType leftNaturalSpecial,
        SpecialType rightNaturalSpecial)
    {
        if (_semanticModel == null
            || !_options.EnableOperatorRewrite
            || !IsBinaryOperatorRewriteKind(node.Kind())
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node))
        {
            return UnsignedDomain.Native;
        }

        if (IsNullableValueType(GetEffectiveType(node.Left))
            || IsNullableValueType(GetEffectiveType(node.Right)))
        {
            return UnsignedDomain.Native;
        }

        if (IsEnumType(GetEffectiveType(node.Left))
            || IsEnumType(GetEffectiveType(node.Right)))
        {
            return UnsignedDomain.Native;
        }

        if (isShift)
        {
            // 移位只由左操作数（提升后）类型决定；int/long 左操作数不重写。
            return GetShiftDomain(leftNaturalSpecial);
        }

        if (!IsUnsignedSpecial(leftNaturalSpecial)
            && !IsUnsignedSpecial(rightNaturalSpecial))
        {
            return UnsignedDomain.Native;
        }

        if (node.IsKind(SyntaxKind.EqualsExpression)
            || node.IsKind(SyntaxKind.NotEqualsExpression)
            || node.IsKind(SyntaxKind.LessThanExpression)
            || node.IsKind(SyntaxKind.LessThanOrEqualExpression)
            || node.IsKind(SyntaxKind.GreaterThanExpression)
            || node.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
        {
            // 比较的结果是 bool，按操作数自然类型的提升规则归类。
            return GetBinaryDomain(leftNaturalSpecial, rightNaturalSpecial);
        }

        // 算术/位运算按 C# 实际结果类型归类（结果类型已经体现常量转换与提升）。
        return GetResultDomain(_semanticModel.GetTypeInfo(node).Type?.SpecialType);
    }

    private static bool IsUnsignedSpecial(SpecialType special)
        => special is SpecialType.System_Byte
            or SpecialType.System_UInt16
            or SpecialType.System_UInt32
            or SpecialType.System_UInt64;

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;

        if (_semanticModel == null
            || !_options.EnableOperatorRewrite
            || !CompoundAssignmentMethod.ContainsKey(node.Kind())
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node)
            || !IsSafeAssignmentTarget(node.Left))
        {
            return rewritten;
        }

        var leftType = GetEffectiveType(node.Left);
        if (!IsUnsignedType(leftType)
            || IsNullableValueType(leftType)
            || IsEnumType(leftType))
        {
            return rewritten;
        }

        var isShift = node.IsKind(SyntaxKind.LeftShiftAssignmentExpression)
                      || node.IsKind(SyntaxKind.RightShiftAssignmentExpression);
        var leftSpecial = GetSpecial(node.Left, leftType);
        var rightEffectiveSpecial = GetSpecial(node.Right, GetEffectiveType(node.Right));
        var leftNaturalSpecial = GetNaturalSpecial(node.Left) ?? SpecialType.None;
        var rightNaturalSpecial = GetNaturalSpecial(node.Right) ?? SpecialType.None;
        var domain = isShift
            ? GetShiftDomain(leftSpecial)
            : GetBinaryDomain(leftSpecial, rightEffectiveSpecial);
        if (domain == UnsignedDomain.Native)
        {
            return rewritten;
        }

        var method = CompoundAssignmentMethod[node.Kind()] + UnsignedMathSyntax.Suffix(domain);
        var call = UnsignedMathSyntax.StaticCall(
            method,
            UnsignedMathSyntax.Normalize(rewritten.Left, domain, leftNaturalSpecial),
            UnsignedMathSyntax.Normalize(rewritten.Right, domain, rightNaturalSpecial));

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            rewritten.Left,
            UnsignedMathSyntax.WrapTarget(leftType!.SpecialType, call));
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
            && !IsNullableValueType(GetEffectiveType(node.Operand))
            && !IsEnumType(GetEffectiveType(node.Operand))
            && IsUnsignedSpecial(GetNaturalSpecial(node.Operand) ?? SpecialType.None)
            && IsSafeAssignmentTarget(node.Operand))
        {
            var target = GetNaturalSpecial(node.Operand) ?? SpecialType.None;
            return base.VisitPostfixUnaryExpression(
                node.WithAdditionalAnnotations(
                    UnsignedPostfixAnnotations.For(target)));
        }

        return base.VisitPostfixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.BitwiseNotExpression))
        {
            var notRewritten = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
            if (_semanticModel == null
                || !_options.EnableOperatorRewrite
                || IsInCheckedContext(node)
                || IsInConstantExpressionContext(node))
            {
                return notRewritten;
            }

            var notSpecial = GetNaturalSpecial(node.Operand) ?? SpecialType.None;
            var domain = GetUnaryDomain(notSpecial);
            if (domain == UnsignedDomain.Native)
            {
                return notRewritten;
            }

            UsedExtensions = true;
            _stats.OperatorsRewritten++;
            return WrapResult(
                UnsignedMathSyntax.StaticCall(
                    "Not" + UnsignedMathSyntax.Suffix(domain),
                    UnsignedMathSyntax.Normalize(
                        notRewritten.Operand,
                        domain,
                        notSpecial)),
                domain,
                GetSpecial(node, GetEffectiveType(node)));
        }

        var method = node.Kind() switch
        {
            SyntaxKind.PreIncrementExpression => "Add",
            SyntaxKind.PreDecrementExpression => "Subtract",
            _ => null,
        };

        var rewritten = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
        if (method == null
            || _semanticModel == null
            || !_options.EnableOperatorRewrite
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node)
            || IsNullableValueType(GetEffectiveType(node.Operand))
            || IsEnumType(GetEffectiveType(node.Operand))
            || !IsUnsignedSpecial(GetNaturalSpecial(node.Operand) ?? SpecialType.None)
            || !IsSafeAssignmentTarget(node.Operand))
        {
            return rewritten;
        }

        var operandSpecial = GetNaturalSpecial(node.Operand) ?? SpecialType.None;
        var prefixDomain = GetUnaryDomain(operandSpecial);
        if (prefixDomain == UnsignedDomain.Native)
        {
            return rewritten;
        }

        var call = UnsignedMathSyntax.StaticCall(
            method + UnsignedMathSyntax.Suffix(prefixDomain),
            UnsignedMathSyntax.Normalize(
                rewritten.Operand,
                prefixDomain,
                operandSpecial),
            LiteralOne());

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            rewritten.Operand,
            UnsignedMathSyntax.WrapTarget(operandSpecial, call));
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var rewritten = (CastExpressionSyntax)base.VisitCastExpression(node)!;
        if (IsInConstantExpressionContext(node))
        {
            return FixQualifiedCastParens(rewritten);
        }

        if (_semanticModel == null || !_options.EnableCastRewrite || IsInCheckedContext(node))
        {
            return FixQualifiedCastParens(rewritten);
        }

        var sourceType = _semanticModel.GetTypeInfo(node.Expression).Type;
        var targetType = _semanticModel.GetTypeInfo(node).Type;
        if (sourceType == null || targetType == null)
        {
            return FixQualifiedCastParens(rewritten);
        }
        if (IsNullableValueType(sourceType) || IsNullableValueType(targetType))
        {
            return FixQualifiedCastParens(rewritten);
        }
        if (IsEnumType(sourceType) || IsEnumType(targetType))
        {
            return FixQualifiedCastParens(rewritten);
        }
        if (!IsIntegralSpecial(sourceType.SpecialType))
        {
            // object/字符串等非整型源（如拆箱 (byte)objectValue）保持原生转换。
            return FixQualifiedCastParens(rewritten);
        }
        var replacement = GetCastReplacement(targetType, sourceType, rewritten);
        if (replacement == null)
        {
            return FixQualifiedCastParens(rewritten);
        }

        UsedExtensions = true;
        _stats.CastsRewritten++;
        return replacement;
    }

    private static ExpressionSyntax FixQualifiedCastParens(CastExpressionSyntax rewritten)
    {
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

    private ExpressionSyntax? GetCastReplacement(
        ITypeSymbol? targetType,
        ITypeSymbol? sourceType,
        CastExpressionSyntax rewritten)
    {
        if (targetType == null || sourceType == null)
        {
            return null;
        }

        var target = targetType.SpecialType;
        var source = sourceType.SpecialType;
        var operand = rewritten.Expression;

        if (target is SpecialType.System_Byte or SpecialType.System_UInt16)
        {
            if (source is SpecialType.System_Byte or SpecialType.System_UInt16)
            {
                return null;
            }

            var method = target == SpecialType.System_Byte
                ? (source == SpecialType.System_Int64 || source == SpecialType.System_UInt64
                    ? "ToByteInt64" : "ToByteInt32")
                : (source == SpecialType.System_Int64 || source == SpecialType.System_UInt64
                    ? "ToUInt16Int64" : "ToUInt16Int32");
            var normalized = source == SpecialType.System_UInt64
                ? UnsignedMathSyntax.UncheckedCast(SyntaxKind.LongKeyword, operand)
                : source == SpecialType.System_UInt32
                    ? UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand)
                    : operand;
            return UnsignedMathSyntax.StaticCall(method, normalized);
        }

        if (target == SpecialType.System_Int64 && source == SpecialType.System_UInt32)
        {
            return UnsignedMathSyntax.StaticCall(
                "ToInt64UInt32",
                UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand));
        }

        if (target == SpecialType.System_UInt64 && source == SpecialType.System_UInt32)
        {
            return UnsignedMathSyntax.StaticCall(
                "ToUInt64UInt32",
                UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand));
        }

        if (target == SpecialType.System_Int32 && source == SpecialType.System_UInt64)
        {
            return UnsignedMathSyntax.StaticCall(
                "ToInt32UInt64",
                UnsignedMathSyntax.UncheckedCast(SyntaxKind.LongKeyword, operand));
        }

        if (target is SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal)
        {
            if (source == SpecialType.System_UInt32)
            {
                var name = target switch
                {
                    SpecialType.System_Double => "ToDoubleUInt32",
                    SpecialType.System_Single => "ToSingleUInt32",
                    _ => "ToDecimalUInt32",
                };
                return UnsignedMathSyntax.StaticCall(
                    name,
                    UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand));
            }

            if (source == SpecialType.System_UInt64)
            {
                var name = target switch
                {
                    SpecialType.System_Double => "ToDoubleUInt64",
                    SpecialType.System_Single => "ToSingleUInt64",
                    _ => "ToDecimalUInt64",
                };
                return UnsignedMathSyntax.StaticCall(
                    name,
                    UnsignedMathSyntax.UncheckedCast(SyntaxKind.LongKeyword, operand));
            }
        }

        return null;
    }
}
