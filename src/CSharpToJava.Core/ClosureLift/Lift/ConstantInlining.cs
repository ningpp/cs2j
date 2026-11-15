using System.Globalization;
using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45: 外层 <c>const</c> 局部是编译期常量，C# 闭包不捕获它
/// （<see cref="CaptureClassification.CollectCapturedSymbols"/> 有意跳过
/// HasConstantValue）。lambda 体迁入 display class 的 <c>Lambda_k</c> 方法或
/// 宿主级 <c>private static</c> 命名方法后，原作用域的常量名字不再可见
/// （CS0103），必须按值内联成字面量。渲染失败时跳过该常量（保持原样，交给
/// 上游已有的 residual 机制），绝不产出坏码。
/// </summary>
internal static class ConstantInlining
{
    private static readonly Func<SyntaxNode, bool> DescendNotIntoLocalFunctions
        = static d => d is not LocalFunctionStatementSyntax;

    /// <summary>
    /// 采集 <paramref name="lambda"/> 体内引用的外层 const 局部，渲染为
    /// 名字 → 字面量文本的映射（升序无关；同名不可能既是捕获又是常量）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> CollectByName(
        SemanticModel model, LambdaExpressionSyntax lambda)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => (SyntaxNode?)p.Block ?? p.ExpressionBody,
            SimpleLambdaExpressionSyntax s => (SyntaxNode?)s.Block ?? s.ExpressionBody,
            _ => null,
        };
        if (body == null)
            return result;

        var lambdaSymbol = model.GetSymbolInfo(lambda).Symbol as IMethodSymbol;
        foreach (var info in CaptureClassification.CollectReferencedConstants(
                     model, body, lambdaSymbol, DescendNotIntoLocalFunctions))
        {
            if (Render(info) is { } literal)
                result[info.Name] = literal;
        }

        return result;
    }

    /// <summary>
    /// 变量访问位置上的标识符命中常量映射时，返回替换用的字面量表达式。
    /// nameof、成员名、类型上下文等位置不改写（那里不是变量访问）。
    /// </summary>
    public static bool TryInline(
        IdentifierNameSyntax node,
        IReadOnlyDictionary<string, string> constantByName,
        out ExpressionSyntax replacement)
    {
        replacement = null!;
        if (!IsVariableAccessPosition(node)
            || !constantByName.TryGetValue(node.Identifier.ValueText, out var literal))
        {
            return false;
        }

        replacement = SyntaxFactory.ParseExpression(literal).WithTriviaFrom(node);
        return true;
    }

    /// <summary>Positions that are ordinary variable accesses (read or write).</summary>
    internal static bool IsVariableAccessPosition(IdentifierNameSyntax node)
    {
        if (node.Parent is NameColonSyntax || CaptureClassification.IsInsideNameOf(node))
            return false;
        if (node.Parent is MemberAccessExpressionSyntax member && member.Name == node)
            return false;
        if (node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node)
            return false;
        if (node.Parent is NameEqualsSyntax)
            return false;

        // Type contexts: typeof(T), sizeof(T), default(T), new T(), (T)x,
        // x as T, x is T u, T[] — a name in these positions denotes a type.
        if (node.Parent is TypeOfExpressionSyntax
            or SizeOfExpressionSyntax
            or DefaultExpressionSyntax
            or ArrayTypeSyntax
            or GenericNameSyntax
            or QualifiedNameSyntax
            or TypeArgumentListSyntax
            or DeclarationPatternSyntax)
        {
            return false;
        }

        if (node.Parent is CastExpressionSyntax cast && cast.Type == node)
            return false;
        if (node.Parent is ObjectCreationExpressionSyntax creation && creation.Type == node)
            return false;
        if (node.Parent is BinaryExpressionSyntax binary
            && (binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression))
            && binary.Right == node)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 常量值 → C# 字面量文本。枚举常量写成 <c>(EnumType)(underlying)</c>：
    /// 裸整数字面量对枚举不成立。类型无法解析（ErrorType）时返回 null。
    /// </summary>
    private static string? Render(ReferencedConstantInfo info)
    {
        if (info.Type.TypeKind == TypeKind.Enum)
        {
            if (info.ConstantValue == null || info.Type is not INamedTypeSymbol)
                return null;

            var inner = RenderPrimitiveLiteral(info.ConstantValue);
            if (inner == null)
                return null;

            var typeDisplay = info.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"({typeDisplay})({inner})";
        }

        if (info.Type.TypeKind == TypeKind.Error)
            return null;

        return RenderPrimitiveLiteral(info.ConstantValue);
    }

    private static string? RenderPrimitiveLiteral(object? value)
    {
        if (value is null)
            return "null";

        return value switch
        {
            bool b => b ? "true" : "false",
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            int i => i.ToString(CultureInfo.InvariantCulture),
            uint u => u.ToString(CultureInfo.InvariantCulture) + "U",
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
            short sh => "(short)" + sh.ToString(CultureInfo.InvariantCulture),
            ushort ush => "(ushort)" + ush.ToString(CultureInfo.InvariantCulture),
            byte by => "(byte)" + by.ToString(CultureInfo.InvariantCulture),
            sbyte sb => "(sbyte)" + sb.ToString(CultureInfo.InvariantCulture),
            double d => RenderDoubleLiteral(d),
            float f => RenderFloatLiteral(f),
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "M",
            _ => null,
        };
    }

    private static string RenderDoubleLiteral(double value)
    {
        if (double.IsNaN(value))
            return "double.NaN";
        if (double.IsPositiveInfinity(value))
            return "double.PositiveInfinity";
        if (double.IsNegativeInfinity(value))
            return "double.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "D";
    }

    private static string RenderFloatLiteral(float value)
    {
        if (float.IsNaN(value))
            return "float.NaN";
        if (float.IsPositiveInfinity(value))
            return "float.PositiveInfinity";
        if (float.IsNegativeInfinity(value))
            return "float.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "F";
    }
}
