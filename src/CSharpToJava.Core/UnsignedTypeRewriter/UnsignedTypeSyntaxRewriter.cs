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
            receiver,
            SyntaxFactory.IdentifierName(method));

        var argumentList = argument == null
            ? SyntaxFactory.ArgumentList()
            : SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { SyntaxFactory.Argument(argument) }));

        return SyntaxFactory.InvocationExpression(memberAccess, argumentList);
    }

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
        var shouldRewrite = _options.EnableOperatorRewrite
            && _semanticModel != null
            && IsBinaryOperatorRewriteKind(node.Kind())
            && (IsUnsignedExpression(node.Left) || IsUnsignedExpression(node.Right));

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

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var shouldRewrite = _options.EnableOperatorRewrite
            && CompoundAssignmentToMethod.ContainsKey(node.Kind())
            && IsUnsignedExpression(node.Left);

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

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        var method = node.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => "Add",
            SyntaxKind.PostDecrementExpression => "Subtract",
            _ => null,
        };
        var shouldRewrite = method != null && IsUnsignedExpression(node.Operand);

        var rewritten = (PostfixUnaryExpressionSyntax)base.VisitPostfixUnaryExpression(node)!;
        if (!shouldRewrite || method == null)
            return rewritten;

        UsedExtensions = true;
        _stats.OperatorsRewritten++;
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            rewritten.Operand,
            Invoke(Clone(rewritten.Operand), method, LiteralOne()));
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.BitwiseNotExpression))
        {
            var shouldRewriteNot = _options.EnableOperatorRewrite
                && IsUnsignedExpression(node.Operand);

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
        var shouldRewrite = method != null && IsUnsignedExpression(node.Operand);

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
        if (_options.EnableCastRewrite && _semanticModel != null)
        {
            var sourceType = _semanticModel.GetTypeInfo(node.Expression).Type;
            if (IsUnsignedType(sourceType))
                castMethod = GetSignedCastMethod(node.Type, sourceType!);
        }

        var rewritten = (CastExpressionSyntax)base.VisitCastExpression(node)!;
        if (castMethod == null)
            return rewritten;

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
