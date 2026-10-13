using System.Globalization;
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

    // 必须用 global:: 限定:源码若处于形如 dotnet.System.Text 的命名空间内,
    // 裸 System.Byte 会相对解析到不存在的 dotnet.System.Byte(cs-text 回归)。
    private static readonly Dictionary<SyntaxKind, string> KeywordToFullName = new()
    {
        { SyntaxKind.ByteKeyword,   "global::System.Byte"   },
        { SyntaxKind.UShortKeyword, "global::System.UInt16" },
        { SyntaxKind.UIntKeyword,   "global::System.UInt32" },
        { SyntaxKind.ULongKeyword,  "global::System.UInt64" },
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

    internal static bool IsDirty(CompilationUnitSyntax root, UnsignedTypeRewriterOptions options)
        => root.DescendantTokens().Any(t => KeywordToFullName.ContainsKey(t.Kind())
            || (options.EnableLiteralRewrite
                && t.IsKind(SyntaxKind.NumericLiteralToken)
                && UnsignedLiteralSyntax.NeedsRewrite(t.Text)));

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
                // VisitVariableDeclarator passes the declarator NODE itself,
                // whose span COVERS the initializer (so Initializer.Span.Contains
                // is false for it); inner expression visitors pass nodes inside
                // the initializer. Accept both — a const declarator value must
                // stay a constant expression (CS0133 on a ToInt64UInt32 call).
                case FieldDeclarationSyntax fieldDeclaration
                    when fieldDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword)
                         && fieldDeclaration.Declaration.Variables
                             .Any(variable => ReferenceEquals(variable, node)
                                 || variable.Initializer?.Span.Contains(node.Span) == true):
                    return true;
                case LocalDeclarationStatementSyntax localDeclaration
                    when localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword)
                         && localDeclaration.Declaration.Variables
                             .Any(variable => ReferenceEquals(variable, node)
                                 || variable.Initializer?.Span.Contains(node.Span) == true):
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

    private static bool IsDirectByteArrayInitializerElement(ExpressionSyntax node)
    {
        if (node.Parent is not InitializerExpressionSyntax initializer)
        {
            return false;
        }

        string? elementType = initializer.Parent switch
        {
            ArrayCreationExpressionSyntax arrayCreation => arrayCreation.Type.ElementType.ToString(),
            EqualsValueClauseSyntax equalsValue
                when equalsValue.Parent is VariableDeclaratorSyntax declarator
                     && declarator.Parent is VariableDeclarationSyntax declaration
                     && declaration.Type is ArrayTypeSyntax arrayType => arrayType.ElementType.ToString(),
            _ => null,
        };

        return elementType is "byte"
            or "System.Byte"
            or "global::System.Byte";
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

        // nint/nuint 擦除为 Java long,按 64 位有/无符号域处理(nuint 是无符号)。
        if (left == SpecialType.System_UIntPtr || right == SpecialType.System_UIntPtr)
        {
            return UnsignedDomain.UInt64;
        }

        if (left == SpecialType.System_IntPtr || right == SpecialType.System_IntPtr)
        {
            return UnsignedDomain.Int64;
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
            // 仅当 uint 一侧遇上带符号整型时 C# 才提升到 long;byte/ushort 一侧
            // 仍按 int 计算(它们提升后就是 int,Java int 算术逐位一致)。
            var other = left == SpecialType.System_UInt32 ? right.Value : left.Value;
            if (other is SpecialType.System_Int32
                or SpecialType.System_Int16
                or SpecialType.System_SByte)
            {
                return UnsignedDomain.Int64;
            }

            return UnsignedDomain.UInt32;
        }

        // byte/ushort(以及与 int)的提升结果:int,与 Java int 完全一致,无需重写。
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
            // uint 结果喂给 nuint 上下文:还原成 uint(uint→nuint 隐式)。
            SpecialType.System_UIntPtr => UnsignedMathSyntax.UncheckedCast(SyntaxKind.UIntKeyword, call),
            SpecialType.System_Int64 => UnsignedMathSyntax.StaticCall("ToInt64UInt32", call),
            SpecialType.System_Double => UnsignedMathSyntax.StaticCall("ToDoubleUInt32", call),
            SpecialType.System_Single => UnsignedMathSyntax.StaticCall("ToSingleUInt32", call),
            SpecialType.System_Decimal => UnsignedMathSyntax.StaticCall("ToDecimalUInt32", call),
            _ => call,
        },
        UnsignedDomain.UInt64 => contextSpecial switch
        {
            SpecialType.System_UInt64 => UnsignedMathSyntax.UncheckedCast(SyntaxKind.ULongKeyword, call),
            // 64 位结果喂给 nuint 上下文:显式转回 nuint(long→nuint 需要强转)。
            SpecialType.System_UIntPtr => UnsignedMathSyntax.UncheckedNUIntCast(call),
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

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        var rewritten = (LiteralExpressionSyntax)base.VisitLiteralExpression(node)!;
        if (!_options.EnableLiteralRewrite || !node.IsKind(SyntaxKind.NumericLiteralExpression))
        {
            return rewritten;
        }

        ExpressionSyntax result = rewritten;
        if (UnsignedLiteralSyntax.TryRewrite(node.Token.Text, out var rewrite))
        {
            // 只换 token 文本,trivia 原样保留:类型与值由后缀+值决定,与上下文无关,
            // 因此重载决议、常量折叠、const/enum/attribute/case 等常量上下文全部不变。
            _stats.LiteralsRewritten++;
            var token = SyntaxFactory.Literal(
                node.Token.LeadingTrivia,
                rewrite.Text,
                rewrite.Value,
                node.Token.TrailingTrivia);
            result = rewritten.WithToken(token);
        }

        // C# byte(无符号)≠ Java byte(有符号):byte[] 擦除后仍是 Java byte[],
        // 元素常量 ≥ 128 必须 (byte) 收窄 javac 才接受;标量 byte 擦除为 int,
        // 数值原样正确,不包装。
        if (IsByteArrayInitializerElement(node)
            && _semanticModel.GetConstantValue(node).HasValue
            && Convert.ToUInt64(_semanticModel.GetConstantValue(node).Value!, CultureInfo.InvariantCulture) is >= 128 and <= 255
            && result is LiteralExpressionSyntax element)
        {
            _stats.LiteralsRewritten++;
            var cast = SyntaxFactory.CastExpression(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword)),
                element.WithoutTrivia());
            return UnsignedRewriteAnnotations.Mark(cast.WithTriviaFrom(node));
        }

        return ReferenceEquals(result, rewritten)
            ? rewritten
            : UnsignedRewriteAnnotations.Mark(result);
    }

    /// <summary>
    /// 字面量是 byte 数组/集合初始化器的直接元素(含多维内层初始化器与 C# 12
    /// 集合表达式的 ExpressionElement)。用语义模型的 ConvertedType 判定元素
    /// 类型,标量 byte 上下文(赋值/实参/返回)的父节点不是初始化器,天然排除。
    /// </summary>
    private bool IsByteArrayInitializerElement(LiteralExpressionSyntax node)
        => _semanticModel != null
           && node.Parent is InitializerExpressionSyntax
               or CollectionExpressionSyntax
               or ExpressionElementSyntax
           && _semanticModel.GetTypeInfo(node).ConvertedType?.SpecialType == SpecialType.System_Byte;

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
        var floatingWrapSide = GetFloatingWrapSide(node, leftNaturalSpecial, rightNaturalSpecial);

        var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;

        // uint/ulong 与浮点/decimal 混算:只把无符号操作数换成无符号浮点值,
        // 运算本身保持原生(Java 的 double 算术与 C# 一致,符号扩展才是错的)。
        if (floatingWrapSide != 0)
        {
            var left = WithoutLeading(rewritten.Left, out var leftLead);
            var right = WithoutLeading(rewritten.Right, out var rightLead);
            ExpressionSyntax result;
            if (floatingWrapSide == 1)
            {
                var target = leftNaturalSpecial == SpecialType.System_UInt64
                    ? SpecialType.System_UInt64 : SpecialType.System_UInt32;
                result = SyntaxFactory.BinaryExpression(
                    rewritten.Kind(),
                    UnsignedMathSyntax.ToFloating(
                        target, rightNaturalSpecial, left),
                    right);
            }
            else
            {
                var target = rightNaturalSpecial == SpecialType.System_UInt64
                    ? SpecialType.System_UInt64 : SpecialType.System_UInt32;
                result = SyntaxFactory.BinaryExpression(
                    rewritten.Kind(),
                    left,
                    UnsignedMathSyntax.ToFloating(
                        target, leftNaturalSpecial, right));
            }

            UsedExtensions = true;
            _stats.OperatorsRewritten++;
            return UnsignedRewriteAnnotations.Mark(
                WithHoistedLeading(result, leftLead, rightLead));
        }

        // Int32 域 = byte/ushort 提升算术,与 Java int 逐位一致,保持原生。
        if (domain == UnsignedDomain.Native || domain == UnsignedDomain.Int32)
        {
            return rewritten;
        }

        var rewrittenLeft = WithoutLeading(rewritten.Left, out var hoistedLeft);
        var rewrittenRight = WithoutLeading(rewritten.Right, out var hoistedRight);

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        if (isShift)
        {
            // 左移逐位一致直接内联;右移内联为 C# 11 的 >>>(转换器映射为 Java >>>)。
            var shiftKind = rewritten.IsKind(SyntaxKind.LeftShiftExpression)
                ? SyntaxKind.LeftShiftExpression
                : SyntaxKind.UnsignedRightShiftExpression;
            var shift = SyntaxFactory.BinaryExpression(
                shiftKind,
                UnsignedMathSyntax.Normalize(rewrittenLeft, domain, leftNaturalSpecial),
                rewrittenRight);
            return UnsignedRewriteAnnotations.Mark(
                WithHoistedLeading(
                    WrapResult(shift, domain, resultContextSpecial),
                    hoistedLeft, hoistedRight));
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
                UnsignedMathSyntax.Normalize(rewrittenLeft, domain, leftNaturalSpecial),
                UnsignedMathSyntax.Normalize(rewrittenRight, domain, rightNaturalSpecial));
            var comparisonKind = rewritten.Kind() switch
            {
                SyntaxKind.EqualsExpression => SyntaxKind.EqualsExpression,
                SyntaxKind.NotEqualsExpression => SyntaxKind.NotEqualsExpression,
                SyntaxKind.LessThanExpression => SyntaxKind.LessThanExpression,
                SyntaxKind.LessThanOrEqualExpression => SyntaxKind.LessThanOrEqualExpression,
                SyntaxKind.GreaterThanExpression => SyntaxKind.GreaterThanExpression,
                _ => SyntaxKind.GreaterThanOrEqualExpression,
            };
            return UnsignedRewriteAnnotations.Mark(
                WithHoistedLeading(
                    SyntaxFactory.BinaryExpression(comparisonKind, compare, LiteralZero()),
                    hoistedLeft, hoistedRight));
        }

        var binaryMethod = BinaryOpMethod[rewritten.Kind()] + UnsignedMathSyntax.Suffix(domain);
        return UnsignedRewriteAnnotations.Mark(
            WithHoistedLeading(
                WrapResult(
                    UnsignedMathSyntax.StaticCall(
                        binaryMethod,
                        UnsignedMathSyntax.Normalize(rewrittenLeft, domain, leftNaturalSpecial),
                        UnsignedMathSyntax.Normalize(rewrittenRight, domain, rightNaturalSpecial)),
                    domain,
                    resultContextSpecial),
                hoistedLeft, hoistedRight));
    }

    /// <summary>
    /// 一侧是浮点/decimal、另一侧是 uint/ulong 时返回需要包 helper 的一侧
    /// (1=左侧,2=右侧),否则 0。C# 会把无符号一侧按无符号值提升到浮点,
    /// Java 直接 widening 会按符号扩展。
    /// </summary>
    private int GetFloatingWrapSide(
        BinaryExpressionSyntax node,
        SpecialType leftNaturalSpecial,
        SpecialType rightNaturalSpecial)
    {
        if (_semanticModel == null
            || !_options.EnableOperatorRewrite
            || !IsBinaryOperatorRewriteKind(node.Kind())
            || node.IsKind(SyntaxKind.LeftShiftExpression)
            || node.IsKind(SyntaxKind.RightShiftExpression)
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node))
        {
            return 0;
        }

        bool leftIsFloat = IsFloatingSpecial(leftNaturalSpecial);
        bool rightIsFloat = IsFloatingSpecial(rightNaturalSpecial);
        if (leftIsFloat == rightIsFloat)
        {
            return 0;
        }

        var unsignedSide = leftIsFloat ? 2 : 1;
        var unsignedSpecial = leftIsFloat ? rightNaturalSpecial : leftNaturalSpecial;
        return IsUnsignedWideSpecial(unsignedSpecial) ? unsignedSide : 0;
    }

    private static bool IsUnsignedWideSpecial(SpecialType special)
        => special is SpecialType.System_UInt32 or SpecialType.System_UInt64;

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
            // 移位只由左操作数(提升后)类型决定;int/long 左操作数不重写;
            // byte/ushort 提升为 int 后与 Java int 移位一致,同样不重写。
            var shiftDomain = GetShiftDomain(leftNaturalSpecial);
            return shiftDomain == UnsignedDomain.Int32 ? UnsignedDomain.Native : shiftDomain;
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
            // 比较的结果是 bool,按操作数自然类型的提升规则归类;
            // Int32 说明只有 byte/ushort 参与,Java int 比较一致,保持原生。
            var compareDomain = GetBinaryDomain(leftNaturalSpecial, rightNaturalSpecial);
            return compareDomain == UnsignedDomain.Int32 ? UnsignedDomain.Native : compareDomain;
        }

        // 算术/位运算按 C# 实际结果类型归类(结果类型已经体现常量转换与提升);
        // Int32 结果意味着提升后是 int,Java int 算术逐位一致,保持原生。
        var arithmeticDomain = GetResultDomain(_semanticModel.GetTypeInfo(node).Type?.SpecialType);
        if (arithmeticDomain == UnsignedDomain.Int32)
        {
            return UnsignedDomain.Native;
        }

        // 结果为 nint/nuint 时(如 nuint + uint 的常量提升),GetResultDomain 归为
        // Native;但 Java 擦除下 uint 一侧会符号扩展,必须按 64 位无符号域重写。
        if (arithmeticDomain == UnsignedDomain.Native
            && (IsNativeIntSpecial(leftNaturalSpecial) || IsNativeIntSpecial(rightNaturalSpecial)))
        {
            return GetBinaryDomain(leftNaturalSpecial, rightNaturalSpecial);
        }

        return arithmeticDomain;
    }

    private static bool IsNativeIntSpecial(SpecialType special)
        => special is SpecialType.System_IntPtr or SpecialType.System_UIntPtr;

    private static bool IsUnsignedSpecial(SpecialType special)
        => special is SpecialType.System_Byte
            or SpecialType.System_UInt16
            or SpecialType.System_UInt32
            or SpecialType.System_UInt64;

    private static bool IsFloatingSpecial(SpecialType special)
        => special is SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Decimal;

    /// <summary>拆下表达式的前导 trivia(可能包含 #if/#endif 指令与禁用文本)。</summary>
    private static ExpressionSyntax WithoutLeading(ExpressionSyntax expression, out SyntaxTriviaList leading)
    {
        leading = expression.GetLeadingTrivia();
        return leading.Count == 0 ? expression : expression.WithoutLeadingTrivia();
    }

    /// <summary>
    /// 把拆下的前导 trivia 重新挂到替换节点的最外层。被改写的表达式若位于语句开头,
    /// 其首 token 的前导 trivia 携带条件编译指令时,绝不能跟着内层 token 埋进
    /// 参数列表中间,否则指令会拆散语句(Latin1Utility 的 #if DEBUG 回归)。
    /// </summary>
    private static ExpressionSyntax WithHoistedLeading(
        ExpressionSyntax replacement, params SyntaxTriviaList[] leads)
    {
        var total = default(SyntaxTriviaList);
        foreach (var lead in leads)
        {
            total = total.AddRange(lead);
        }

        return total.Count == 0 ? replacement : replacement.WithLeadingTrivia(total);
    }

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
            return RewriteSimpleAssignmentConversion(node, rewritten);
        }

        var leftType = GetEffectiveType(node.Left);
        if (!IsUnsignedType(leftType)
            || IsNullableValueType(leftType)
            || IsEnumType(leftType))
        {
            return RewriteSimpleAssignmentConversion(node, rewritten);
        }

        var isShift = node.IsKind(SyntaxKind.LeftShiftAssignmentExpression)
                      || node.IsKind(SyntaxKind.RightShiftAssignmentExpression);
        var leftSpecial = GetSpecial(node.Left, leftType)!.Value;
        var rightEffectiveSpecial = GetSpecial(node.Right, GetEffectiveType(node.Right));
        var leftNaturalSpecial = GetNaturalSpecial(node.Left) ?? SpecialType.None;
        var rightNaturalSpecial = GetNaturalSpecial(node.Right) ?? SpecialType.None;

        // 目标是 byte/ushort 时 C# 先按 int 运算再截断:运算部分内联(Java int
        // 逐位一致),只保留截断 helper。uint/ulong 目标才走无符号 helper。
        var domain = isShift
            ? GetShiftDomain(leftSpecial)
            : leftSpecial is SpecialType.System_Byte or SpecialType.System_UInt16
                ? UnsignedDomain.Int32
                : GetBinaryDomain(leftSpecial, rightEffectiveSpecial);
        if (domain == UnsignedDomain.Native)
        {
            return rewritten;
        }

        var target = WithoutLeading(rewritten.Left, out var targetLead);
        var value = WithoutLeading(rewritten.Right, out var valueLead);

        ExpressionSyntax computed;
        if (isShift)
        {
            var shiftKind = node.IsKind(SyntaxKind.LeftShiftAssignmentExpression)
                ? SyntaxKind.LeftShiftExpression
                : SyntaxKind.UnsignedRightShiftExpression;
            var normalizedLeft = domain == UnsignedDomain.Int32
                ? target
                : UnsignedMathSyntax.Normalize(target, domain, leftNaturalSpecial);
            computed = SyntaxFactory.BinaryExpression(shiftKind, normalizedLeft, value);
        }
        else if (domain == UnsignedDomain.Int32)
        {
            computed = SyntaxFactory.BinaryExpression(
                CompoundToBinaryKind(node.Kind()), target, value);
        }
        else
        {
            var method = CompoundAssignmentMethod[node.Kind()] + UnsignedMathSyntax.Suffix(domain);
            computed = UnsignedMathSyntax.StaticCall(
                method,
                UnsignedMathSyntax.Normalize(target, domain, leftNaturalSpecial),
                UnsignedMathSyntax.Normalize(value, domain, rightNaturalSpecial));
        }

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        return UnsignedRewriteAnnotations.Mark(
            WithHoistedLeading(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    target,
                    UnsignedMathSyntax.WrapTarget(leftSpecial, computed)),
                targetLead, valueLead));
    }

    private static SyntaxKind CompoundToBinaryKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
        SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
        _ => SyntaxKind.AddExpression,
    };

    /// <summary>
    /// 简单赋值的隐式无符号拓宽:double d = u / long l = u / ulong ul = u。
    /// C# 按无符号值提升,Java 擦除后的 widening 是符号扩展,必须换成 helper。
    /// 二元/强转右侧已由各自路径按结果上下文转换,这里不再重复包装。
    /// </summary>
    private ExpressionSyntax? RewriteSimpleAssignmentConversion(
        AssignmentExpressionSyntax node, AssignmentExpressionSyntax rewritten)
    {
        if (_semanticModel == null
            || !_options.EnableCastRewrite
            || !node.IsKind(SyntaxKind.SimpleAssignmentExpression)
            || node.Right is BinaryExpressionSyntax or CastExpressionSyntax
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node))
        {
            return rewritten;
        }

        var valueNatural = GetNaturalSpecial(node.Right);
        if (!IsUnsignedWideSpecial(valueNatural ?? SpecialType.None))
        {
            return rewritten;
        }

        var targetSpecial = GetSpecial(node.Left, GetEffectiveType(node.Left));
        if (targetSpecial == null || targetSpecial == valueNatural)
        {
            return rewritten;
        }

        ExpressionSyntax converted;
        if (IsFloatingSpecial(targetSpecial.Value))
        {
            converted = UnsignedMathSyntax.ToFloating(valueNatural!.Value, targetSpecial.Value, rewritten.Right);
        }
        else if (targetSpecial is SpecialType.System_Int64 or SpecialType.System_UInt64
                 && valueNatural == SpecialType.System_UInt32)
        {
            converted = UnsignedMathSyntax.ToWide(valueNatural.Value, targetSpecial.Value, rewritten.Right);
        }
        else
        {
            return rewritten;
        }

        UsedExtensions = true;
        _stats.CastsRewritten++;
        return UnsignedRewriteAnnotations.Mark(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                rewritten.Left,
                converted));
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
            // Int32 域 = byte/ushort 的 ~:提升为 int 后与 Java ~ 逐位一致。
            if (domain == UnsignedDomain.Native || domain == UnsignedDomain.Int32)
            {
                return notRewritten;
            }

            var operand = WithoutLeading(notRewritten.Operand, out var operandLead);
            UsedExtensions = true;
            _stats.OperatorsRewritten++;
            return UnsignedRewriteAnnotations.Mark(
                WithHoistedLeading(
                    WrapResult(
                        UnsignedMathSyntax.StaticCall(
                            "Not" + UnsignedMathSyntax.Suffix(domain),
                            UnsignedMathSyntax.Normalize(operand, domain, notSpecial)),
                        domain,
                        GetSpecial(node, GetEffectiveType(node))),
                    operandLead));
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

        var prefixOperand = WithoutLeading(rewritten.Operand, out var prefixLead);
        ExpressionSyntax value = prefixDomain == UnsignedDomain.Int32
            ? SyntaxFactory.BinaryExpression(
                method == "Add" ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                prefixOperand,
                LiteralOne())
            : UnsignedMathSyntax.StaticCall(
                method + UnsignedMathSyntax.Suffix(prefixDomain),
                UnsignedMathSyntax.Normalize(prefixOperand, prefixDomain, operandSpecial),
                LiteralOne());

        UsedExtensions = true;
        _stats.OperatorsRewritten++;

        // ++x/--x lowers from a prefix-unary expression (high precedence) to an
        // assignment expression (lowest precedence). In a value sub-expression
        // slot (`if (++x < y)`, an argument, an operand…) the unparenthesized
        // print lets the enclosing operator bind as the assignment's RHS —
        // `x = F() < y` — which is only a semantic error (CS0029 bool→target),
        // not a parse error. Group the assignment everywhere except the C#
        // statement-expression slots (expression statement, for initializer /
        // incrementor), where a parenthesized assignment is itself rejected
        // (CS0201) and the bare assignment is the expected statement form.
        ExpressionSyntax lowered = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            prefixOperand,
            UnsignedMathSyntax.WrapTarget(operandSpecial, value));
        if (!IsStatementExpressionSite(node))
        {
            lowered = SyntaxFactory.ParenthesizedExpression(lowered);
        }

        return UnsignedRewriteAnnotations.Mark(
            WithHoistedLeading(lowered, prefixLead));
    }

    /// <summary>
    /// True when <paramref name="node"/> occupies one of C#'s
    /// statement-expression slots — an expression statement, or a for-loop
    /// initializer/incrementor (source parentheses around the node are
    /// transparent): a lowered assignment needs no grouping parens there, and
    /// wrapping it would even be rejected with CS0201.
    /// </summary>
    private static bool IsStatementExpressionSite(ExpressionSyntax node)
    {
        SyntaxNode current = node;
        while (current.Parent is ParenthesizedExpressionSyntax)
        {
            current = current.Parent;
        }

        switch (current.Parent)
        {
            case ExpressionStatementSyntax statement:
                return statement.Expression == current;
            case ForStatementSyntax forStatement:
                return forStatement.Initializers.Contains((ExpressionSyntax)current)
                       || forStatement.Incrementors.Contains((ExpressionSyntax)current);
            default:
                return false;
        }
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var isByteArrayInitializerElement = IsDirectByteArrayInitializerElement(node);
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
            // object/字符串等非整型源(如拆箱 (byte)objectValue)保持原生转换。
            return FixQualifiedCastParens(rewritten);
        }

        var operand = WithoutLeading(rewritten.Expression, out var operandLead);
        var replacement = GetCastReplacement(targetType, sourceType, operand, isByteArrayInitializerElement);
        if (replacement == null)
        {
            return FixQualifiedCastParens(rewritten);
        }

        UsedExtensions = true;
        _stats.CastsRewritten++;
        return UnsignedRewriteAnnotations.Mark(WithHoistedLeading(replacement, operandLead));
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
        ExpressionSyntax operand,
        bool isByteArrayInitializerElement)
    {
        if (targetType == null || sourceType == null)
        {
            return null;
        }

        var target = targetType.SpecialType;
        var source = sourceType.SpecialType;

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
            var call = UnsignedMathSyntax.StaticCall(method, normalized);

            // byte[] stays Java byte[] while scalar byte maps to Java int. In a
            // byte[] initializer the helper's int result must be narrowed back
            // to byte or javac reports a lossy-conversion error.
            if (target == SpecialType.System_Byte
                && isByteArrayInitializerElement)
            {
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword)),
                    call);
            }

            return call;
        }

        if (target == SpecialType.System_UIntPtr && source == SpecialType.System_UInt32)
        {
            // Java 擦除下 uint→long 是符号扩展;先零扩展再转 nuint。
            return UnsignedMathSyntax.UncheckedNUIntCast(
                UnsignedMathSyntax.StaticCall(
                    "ToUInt64UInt32",
                    UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, operand)));
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

    /// <summary>
    /// 局部/字段声明初始化器的隐式无符号拓宽:double d = u; long l = u;
    /// ulong ul = u;。二元/强转初始化器已由各自路径处理,这里跳过。
    /// </summary>
    public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
        var rewritten = (VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!;
        // 语义信息只能查原始树上的节点;构建结果才用 rewritten。
        if (_semanticModel == null
            || !_options.EnableCastRewrite
            || node.Initializer?.Value is not ExpressionSyntax originalValue
            || originalValue is BinaryExpressionSyntax or CastExpressionSyntax
            || IsInCheckedContext(node)
            || IsInConstantExpressionContext(node)
            || IsInsideLambdaOrLocalFunction(node))
        {
            return rewritten;
        }

        var valueNatural = GetNaturalSpecial(originalValue);
        if (!IsUnsignedWideSpecial(valueNatural ?? SpecialType.None))
        {
            return rewritten;
        }

        var declaredType = _semanticModel.GetDeclaredSymbol(node) switch
        {
            ILocalSymbol local => local.Type,
            IFieldSymbol field => field.Type,
            _ => null,
        };
        var declaredSpecial = declaredType?.SpecialType;
        if (declaredSpecial == null || declaredSpecial == valueNatural)
        {
            return rewritten;
        }

        ExpressionSyntax converted;
        if (IsFloatingSpecial(declaredSpecial.Value))
        {
            converted = UnsignedMathSyntax.ToFloating(valueNatural!.Value, declaredSpecial.Value, rewritten.Initializer!.Value);
        }
        else if (declaredSpecial is SpecialType.System_Int64 or SpecialType.System_UInt64
                 && valueNatural == SpecialType.System_UInt32)
        {
            converted = UnsignedMathSyntax.ToWide(valueNatural.Value, declaredSpecial.Value, rewritten.Initializer!.Value);
        }
        else if (declaredSpecial == SpecialType.System_UIntPtr
                 && valueNatural == SpecialType.System_UInt32)
        {
            // nuint x = u; Java 擦除下 int→long 符号扩展,必须零扩展。
            converted = UnsignedMathSyntax.UncheckedNUIntCast(
                UnsignedMathSyntax.StaticCall(
                    "ToUInt64UInt32",
                    UnsignedMathSyntax.UncheckedCast(SyntaxKind.IntKeyword, rewritten.Initializer!.Value)));
        }
        else
        {
            return rewritten;
        }

        UsedExtensions = true;
        _stats.CastsRewritten++;
        return UnsignedRewriteAnnotations.Mark(
            rewritten.WithInitializer(
                SyntaxFactory.EqualsValueClause(converted)));
    }

    private static bool IsInsideLambdaOrLocalFunction(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current != null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return true;
            }
        }

        return false;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (_semanticModel != null && _options.EnableOperatorRewrite
            && !IsInCheckedContext(node)
            && !IsInConstantExpressionContext(node))
        {
            // Detect static XmlConvert.ToString(uint/ulong value) calls.
            // After erasure, XmlConvert.ToString(Int64) and ToString(UInt64) merge into
            // the same toString(long) which uses signed formatting. Rewrite the UInt64/UInt32
            // call sites to UnsignedMath helpers so they produce unsigned strings.
            if (node.Expression is MemberAccessExpressionSyntax staticMae
                && staticMae.Name.Identifier.ValueText == "ToString"
                && node.ArgumentList.Arguments.Count == 1)
            {
                var argType = GetNaturalSpecial(node.ArgumentList.Arguments[0].Expression);
                if (argType is SpecialType.System_UInt32 or SpecialType.System_UInt64)
                {
                    var domain = argType == SpecialType.System_UInt64
                        ? UnsignedDomain.UInt64 : UnsignedDomain.UInt32;
                    var visitedArg = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[0].Expression)!;
                    var strippedArg = WithoutLeading(visitedArg, out var argLead);
                    var normalizedArg = UnsignedMathSyntax.Normalize(
                        strippedArg, domain, argType.Value);
                    var method = "ToString" + UnsignedMathSyntax.Suffix(domain);
                    UsedExtensions = true;
                    _stats.OperatorsRewritten++;
                    return UnsignedRewriteAnnotations.Mark(
                        WithHoistedLeading(
                            UnsignedMathSyntax.StaticCall(method, normalizedArg), argLead));
                }
            }

            // Detect uint/ulong.ToString() instance calls and rewrite to UnsignedMath helpers.
            // C# uint.ToString() / ulong.ToString() produce unsigned representations
            // (e.g. (-1u).ToString() == "4294967295"), but after erasure to Java
            // int/long, String.valueOf produces "-1". Rewrite to UnsignedMath helpers.
            if (node.Expression is MemberAccessExpressionSyntax mae
                && mae.Name.Identifier.ValueText == "ToString")
            {
                var receiverType = GetNaturalSpecial(mae.Expression);
                if (receiverType is SpecialType.System_UInt32 or SpecialType.System_UInt64)
                {
                    var domain = receiverType == SpecialType.System_UInt64
                        ? UnsignedDomain.UInt64 : UnsignedDomain.UInt32;
                    var visitedReceiver = (ExpressionSyntax)Visit(mae.Expression)!;
                    var strippedReceiver = WithoutLeading(visitedReceiver, out var receiverLead);
                    var normalizedReceiver = UnsignedMathSyntax.Normalize(
                        strippedReceiver, domain, receiverType.Value);
                    var method = "ToString" + UnsignedMathSyntax.Suffix(domain);
                    var visitedArgs = node.ArgumentList.Arguments
                        .Select(a => (ExpressionSyntax)Visit(a.Expression)!);
                    var allArgs = new[] { normalizedReceiver }.Concat(visitedArgs).ToArray();
                    UsedExtensions = true;
                    _stats.OperatorsRewritten++;
                    return UnsignedRewriteAnnotations.Mark(
                        WithHoistedLeading(
                            UnsignedMathSyntax.StaticCall(method, allArgs), receiverLead));
                }
            }
        }

        return base.VisitInvocationExpression(node);
    }
}
