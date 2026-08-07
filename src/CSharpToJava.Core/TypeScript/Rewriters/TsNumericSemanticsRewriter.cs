using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 数值语义 rewriter（Task 19，M2）：C# 语法树级改写，仅在 TS 发射 pass 内运行
/// （SingleFileTsEmitPass 装配，Java 路径不进入）。
/// 背景：IR 方法体为 raw 文本（JavaRawStatement），IR 层无类型信息；本 rewriter 在
/// C# 语法树 + Roslyn 语义模型上完成类型判定，把产物改写为合法 TS 语义形态，再经
/// 共享 visitor 流入 raw 文本（Math.trunc/BigInt/Number 调用在 Java 文本与 TS 中
/// 同形，天然直译）：
/// 1. 整数除法：两操作数均为 C# 整型（sbyte..uint，非 long/ulong）→ Math.trunc(l / r)；
///    long/ulong（bigint）除法 JS 天然截断不包（spec §2.5）；float/double 不包。
/// 2. 窄化/提升转换：(sbyte)e → ((e << 24) >> 24)、(short)e → << 16 >> 16、
///    (byte)e 沿用 Java 路径 & 0xFF（long/object 源补 Number 包装）、
///    (ushort)e → & 0xFFFF、(uint)e → 模 2^32 截断、(int)e 浮点源 → Math.trunc、
///    long 源 → Number、整型源 → 剥除、(long)/(ulong)e → BigInt（浮点源先 Math.trunc）。
/// 3. bigint/number 混算：+ - * / % 与比较、位运算、移位、复合赋值中一侧 bigint 一侧
///    number 时按 C# 数值提升插入 BigInt(...)/Number(...)；移位以值侧类型为准。
/// 4. List.Clear() 语句 → x.length = 0（Task 2 排除项；接收者 C# 类型为
///    List/IList/ICollection/ArrayList 家族，TS 数组语义）。
/// 5. 类型无法判定（错误类型/类型参数）且与已确认 bigint 混算 → 诊断；char/decimal/
///    enum 源保守不动（char 算术与字面量发射为既有 gap，见注记；枚举转换由
///    Task 17 rewriter 在 IR 层处理，语法层不剥除枚举源强转）。
/// </summary>
public sealed class TsNumericSemanticsRewriter : CSharpSyntaxRewriter
{
    private readonly ConversionContext _context;
    private readonly SemanticModel _semanticModel;
    private readonly HashSet<string> _emittedDiagnostics = new(StringComparer.Ordinal);

    public TsNumericSemanticsRewriter(ConversionContext context)
    {
        _context = context;
        _semanticModel = context.SemanticModel!;
    }

    /// <summary>数值类别：仅任务范围内的目标类型参与改写。</summary>
    private enum NumericKind
    {
        None,       // 已知非目标类型（bool/string/decimal/enum/char/object/引用类型）
        Integral,   // sbyte/byte/short/ushort/int/uint（TS number 整型）
        BigInt,     // long/ulong（TS bigint）
        Floating,   // float/double（TS number 浮点）
        String,     // string（仅 + 拼接与 bigint 混算时转 Number）
        Unknown,    // 错误类型/类型参数/无法判定
    }

    // ─── 遍历覆盖 ───────────────────────────────────────────────────

    /// <summary>按节点所在树解析语义模型（Task 38：项目模式注入 Compilation/
    /// SemanticModel，多文件各树模型不同）；单文件模式回退到构造时捕获的模型。</summary>
    private SemanticModel? SemanticModelFor(SyntaxNode node)
    {
        if (node.SyntaxTree == null)
        {
            return _semanticModel;
        }
        return _context.GetSemanticModelForNode(node) ?? _semanticModel;
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        // 先用原始子树做语义判定（base.Visit 后原始节点仍保持原样，可安全查询），
        // 再递归改写子树，最后按判定套壳（Math.trunc/BigInt/Number）。
        var kind = node.Kind();
        var isDivision = kind == SyntaxKind.DivideExpression;
        var isArithmetic = IsArithmeticOp(kind);
        var isComparison = IsComparisonOp(kind);
        var isShift = kind is SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression
            or SyntaxKind.UnsignedRightShiftExpression;
        var isBitwise = kind is SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression
            or SyntaxKind.ExclusiveOrExpression;
        if (!isDivision && !isArithmetic && !isComparison && !isShift && !isBitwise)
        {
            return base.VisitBinaryExpression(node);
        }

        var leftKind = Classify(node.Left);
        var rightKind = Classify(node.Right);
        var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;

        if (isDivision)
        {
            return RewriteDivision(rewritten, leftKind, rightKind);
        }
        return RewriteMixedOperands(rewritten, leftKind, rightKind, isShift, kind);
    }
    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var target = ClassifyTarget(node.Type);
        var source = Classify(node.Expression);
        var rewritten = (CastExpressionSyntax)base.VisitCastExpression(node)!;
        return RewriteCast(rewritten, target, source);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var kind = node.Kind();
        var isCompound = kind is SyntaxKind.AddAssignmentExpression
            or SyntaxKind.SubtractAssignmentExpression
            or SyntaxKind.MultiplyAssignmentExpression
            or SyntaxKind.DivideAssignmentExpression
            or SyntaxKind.ModuloAssignmentExpression
            or SyntaxKind.LeftShiftAssignmentExpression
            or SyntaxKind.RightShiftAssignmentExpression
            or SyntaxKind.UnsignedRightShiftAssignmentExpression
            or SyntaxKind.AndAssignmentExpression
            or SyntaxKind.OrAssignmentExpression
            or SyntaxKind.ExclusiveOrAssignmentExpression;
        if (!isCompound)
        {
            return base.VisitAssignmentExpression(node);
        }

        var leftKind = Classify(node.Left);
        var rightKind = Classify(node.Right);
        var isShift = kind is SyntaxKind.LeftShiftAssignmentExpression
            or SyntaxKind.RightShiftAssignmentExpression
            or SyntaxKind.UnsignedRightShiftAssignmentExpression;
        var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
        if (isShift)
        {
            // 复合移位：值侧为左操作数
            var newRight = ConvertShiftCount(rewritten.Right, rightKind, leftKind);
            return newRight == rewritten.Right ? rewritten : rewritten.WithRight(newRight);
        }
        var newRight2 = ConvertOperand(rewritten.Right, rightKind, leftKind);
        return newRight2 == rewritten.Right ? rewritten : rewritten.WithRight(newRight2);
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var rewritten = (ExpressionStatementSyntax)base.VisitExpressionStatement(node)!;
        if (rewritten.Expression is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name.Identifier.Text == "Clear"
            && invocation.ArgumentList.Arguments.Count == 0
            && node.Expression is InvocationExpressionSyntax originalInvocation
            && originalInvocation.Expression is MemberAccessExpressionSyntax originalMemberAccess
            && IsListLikeReceiver(originalMemberAccess.Expression))
        {
            // x.Clear() → x.length = 0（TS 数组语义；接收者为 List 家族，Task 2 排除项）
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        memberAccess.Expression,
                        SyntaxFactory.IdentifierName("length")),
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(0))));
        }
        return rewritten;
    }

    // ─── 除法 ───────────────────────────────────────────────────────

    private SyntaxNode RewriteDivision(BinaryExpressionSyntax rewritten, NumericKind left, NumericKind right)
    {
        // 混算先转（结果类型 = 提升类型；两侧同型后不再套 Math.trunc）：
        // long/int → BigInt(int)；long/double → Number(long)。
        var newLeft = ConvertOperand(rewritten.Left, left, right);
        var newRight = ConvertOperand(rewritten.Right, right, left);
        if (newLeft != rewritten.Left || newRight != rewritten.Right)
        {
            return rewritten.WithLeft(newLeft).WithRight(newRight);
        }

        // 整型除法（两操作数均为 number 整型，非 bigint）→ Math.trunc(l / r)
        if (left == NumericKind.Integral && right == NumericKind.Integral)
        {
            return Invoke("Math.trunc", rewritten);
        }
        return rewritten;
    }

    // ─── 混算操作数转换 ─────────────────────────────────────────────

    private SyntaxNode RewriteMixedOperands(
        BinaryExpressionSyntax rewritten, NumericKind left, NumericKind right, bool isShift, SyntaxKind kind)
    {
        // 移位：结果类型 = 值侧类型（C# int << long 结果仍为 int），转换作用于计数侧
        if (isShift)
        {
            var newRight = ConvertShiftCount(rewritten.Right, right, left);
            return newRight == rewritten.Right ? rewritten : rewritten.WithRight(newRight);
        }

        var newLeft = ConvertOperand(rewritten.Left, left, right);
        var newRight2 = ConvertOperand(rewritten.Right, right, left);
        if (newLeft != rewritten.Left || newRight2 != rewritten.Right)
        {
            return rewritten.WithLeft(newLeft).WithRight(newRight2);
        }

        // 字符串拼接 + bigint：C# long + string → string，JS string + bigint 抛 TypeError
        if (kind == SyntaxKind.AddExpression)
        {
            if (left == NumericKind.String && right == NumericKind.BigInt)
            {
                return rewritten.WithRight(Wrap(rewritten.Right, "Number"));
            }
            if (left == NumericKind.BigInt && right == NumericKind.String)
            {
                return rewritten.WithLeft(Wrap(rewritten.Left, "Number"));
            }
        }
        return rewritten;
    }

    /// <summary>
    /// 数值提升转换：bigint 侧 + number 整型侧 → 整型侧包 BigInt（C# int→long 提升）；
    /// bigint 侧 + 浮点侧 → bigint 侧包 Number（C# long→double 提升）。
    /// </summary>
    private ExpressionSyntax ConvertOperand(ExpressionSyntax operand, NumericKind operandKind, NumericKind otherKind)
    {
        if (operandKind == NumericKind.Integral && otherKind == NumericKind.BigInt)
        {
            return Wrap(operand, "BigInt");
        }
        if (operandKind == NumericKind.BigInt && otherKind == NumericKind.Floating)
        {
            return Wrap(operand, "Number");
        }
        if (operandKind == NumericKind.BigInt && otherKind == NumericKind.Unknown)
        {
            WarnOnce("Mixed bigint/number arithmetic with an unresolved operand type cannot be converted safely; generated TypeScript may fail at runtime.");
        }
        return operand;
    }

    /// <summary>移位计数转换：值侧 bigint → 计数包 BigInt；值侧 number → 计数包 Number。</summary>
    private ExpressionSyntax ConvertShiftCount(ExpressionSyntax count, NumericKind countKind, NumericKind valueKind)
    {
        if (countKind == NumericKind.Integral && valueKind == NumericKind.BigInt)
        {
            return Wrap(count, "BigInt");
        }
        if (countKind == NumericKind.BigInt && valueKind is NumericKind.Integral or NumericKind.Floating)
        {
            return Wrap(count, "Number");
        }
        return count;
    }
    // ─── 转换表达式 ─────────────────────────────────────────────────

    private SyntaxNode RewriteCast(CastExpressionSyntax rewritten, NumericKind target, NumericKind source)
    {
        // 未知/十进制源：保留原样（十进制 Java 路径产 dec.intValue() 等合法形态；
        // 类型参数/错误类型保守不动）
        if (source == NumericKind.Unknown || IsDecimalSource(rewritten.Expression))
        {
            return rewritten;
        }

        var inner = rewritten.Expression;
        switch (target)
        {
            case NumericKind.Integral when IsByteTarget(rewritten.Type):
                // (byte)e：Java 路径已产 (e & 0xFF) raw（合法 TS）；仅 bigint/object 源需
                // Number 包装（bigint & number 抛 TypeError；object 源 Java 包装非法）
                if (source == NumericKind.BigInt || IsObjectSource(rewritten.Expression))
                {
                    return rewritten.WithExpression(Wrap(inner, "Number"));
                }
                return rewritten;
            case NumericKind.Integral when IsSByteTarget(rewritten.Type):
                // (sbyte)e → ((e << 24) >> 24)：JS 位运算为 32 位带符号，与 C# sbyte 环绕一致
                return SignExtend(inner, 24, source);
            case NumericKind.Integral when IsShortTarget(rewritten.Type):
                return SignExtend(inner, 16, source);
            case NumericKind.Integral when IsUShortTarget(rewritten.Type):
                // (ushort)e → (e & 0xFFFF)（替代 Java 路径非法 TS 形态 ((int)(e)) & 0xFFFF）
                return Mask(inner, "0xFFFF", source);
            case NumericKind.Integral when IsUIntTarget(rewritten.Type):
                // (uint)e → 模 2^32 截断（JS 无 >>> 的 C# 语法形态；Math.trunc 处理浮点源）
                return UIntTruncate(inner);
            case NumericKind.Integral when IsIntTarget(rewritten.Type):
                return IntCast(rewritten, inner, source);
            case NumericKind.BigInt:
                return LongCast(rewritten, inner, source);
            default:
                return rewritten;
        }
    }

    private SyntaxNode SignExtend(ExpressionSyntax inner, int bits, NumericKind source)
    {
        var operand = source == NumericKind.BigInt || IsObjectSource(inner) ? Wrap(inner, "Number") : inner;
        var left = SyntaxFactory.BinaryExpression(
            SyntaxKind.LeftShiftExpression, operand, IntLiteral(bits));
        return SyntaxFactory.BinaryExpression(
            SyntaxKind.RightShiftExpression, left, IntLiteral(bits));
    }

    private SyntaxNode Mask(ExpressionSyntax inner, string maskText, NumericKind source)
    {
        var operand = source == NumericKind.BigInt || IsObjectSource(inner) ? Wrap(inner, "Number") : inner;
        return SyntaxFactory.BinaryExpression(
            SyntaxKind.BitwiseAndExpression,
            operand,
            SyntaxFactory.ParseExpression(maskText));
    }

    private SyntaxNode UIntTruncate(ExpressionSyntax inner)
    {
        // ((Math.trunc(Number(e)) % 0x100000000) + 0x100000000) % 0x100000000；0x100000000 = 2^32，
        // 十六进制字面量避免 literal transformer 产出 long 后缀 L（后缀在 TS 中非法）
        var mod = SyntaxFactory.BinaryExpression(
            SyntaxKind.ModuloExpression,
            Invoke("Math.trunc", Invoke("Number", inner)),
            SyntaxFactory.ParseExpression("0x100000000"));
        var add = SyntaxFactory.BinaryExpression(
            SyntaxKind.AddExpression, mod, SyntaxFactory.ParseExpression("0x100000000"));
        return SyntaxFactory.BinaryExpression(
            SyntaxKind.ModuloExpression, add, SyntaxFactory.ParseExpression("0x100000000"));
    }

    private SyntaxNode IntCast(CastExpressionSyntax cast, ExpressionSyntax inner, NumericKind source)
    {
        // (int)e：浮点源 → Math.trunc；bigint 源 → Number；整型源 → 剥除（TS number 无窄化）；
        // object 源 → Math.trunc(Number(...))；char/enum → 保留原样（枚举转换归 Task 17）
        if (source == NumericKind.None && IsObjectSource(cast.Expression))
        {
            return Invoke("Math.trunc", Invoke("Number", inner));
        }
        return source switch
        {
            NumericKind.Floating => Invoke("Math.trunc", inner),
            NumericKind.BigInt => Invoke("Number", inner),
            NumericKind.Integral => inner,
            _ => cast,
        };
    }

    private SyntaxNode LongCast(CastExpressionSyntax cast, ExpressionSyntax inner, NumericKind source)
    {
        // (long)/(ulong)e：整型源 → BigInt；浮点源 → BigInt(Math.trunc)；bigint 源 → 剥除；
        // object 源 → BigInt；char/enum → 保留原样
        if (source == NumericKind.None && IsObjectSource(cast.Expression))
        {
            return Invoke("BigInt", inner);
        }
        return source switch
        {
            NumericKind.Integral => Invoke("BigInt", inner),
            NumericKind.Floating => Invoke("BigInt", Invoke("Math.trunc", inner)),
            NumericKind.BigInt => inner,
            _ => cast,
        };
    }
    // ─── 语义辅助 ───────────────────────────────────────────────────

    private NumericKind Classify(ExpressionSyntax expr)
    {
        var type = UnwrapNullable(GetType(expr));
        if (type == null || type.TypeKind == TypeKind.Error || type.TypeKind == TypeKind.TypeParameter)
        {
            return NumericKind.Unknown;
        }
        return type.SpecialType switch
        {
            SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                => NumericKind.Integral,
            SpecialType.System_Int64 or SpecialType.System_UInt64 => NumericKind.BigInt,
            SpecialType.System_Single or SpecialType.System_Double => NumericKind.Floating,
            SpecialType.System_String => NumericKind.String,
            _ => NumericKind.None,
        };
    }

    private NumericKind ClassifyTarget(TypeSyntax type)
    {
        var symbol = GetType(type);
        return symbol?.SpecialType switch
        {
            SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                => NumericKind.Integral,
            SpecialType.System_Int64 or SpecialType.System_UInt64 => NumericKind.BigInt,
            _ => NumericKind.None,
        };
    }

    private static bool IsByteTarget(TypeSyntax t) => t is PredefinedTypeSyntax { Keyword.Text: "byte" };
    private static bool IsSByteTarget(TypeSyntax t) => t is PredefinedTypeSyntax { Keyword.Text: "sbyte" };
    private static bool IsShortTarget(TypeSyntax t) => t is PredefinedTypeSyntax { Keyword.Text: "short" };
    private static bool IsUShortTarget(TypeSyntax t) => t is PredefinedTypeSyntax { Keyword.Text: "ushort" };
    private static bool IsUIntTarget(TypeSyntax t) => t is PredefinedTypeSyntax { Keyword.Text: "uint" };
    private static bool IsIntTarget(TypeSyntax t) => t is PredefinedTypeSyntax { Keyword.Text: "int" };

    private bool IsObjectSource(ExpressionSyntax expr)
        => UnwrapNullable(GetType(expr))?.SpecialType == SpecialType.System_Object;

    private bool IsDecimalSource(ExpressionSyntax expr)
        => UnwrapNullable(GetType(expr))?.SpecialType == SpecialType.System_Decimal;

    private ITypeSymbol? GetType(ExpressionSyntax expr)
    {
        try
        {
            var model = SemanticModelFor(expr);
            if (model == null)
            {
                return null;
            }
            var info = model.GetTypeInfo(expr);
            return info.Type ?? info.ConvertedType;
        }
        catch
        {
            return null;
        }
    }

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments.Length == 1)
        {
            return nullable.TypeArguments[0];
        }
        return type;
    }

    private bool IsListLikeReceiver(ExpressionSyntax expr)
    {
        var type = UnwrapNullable(GetType(expr)) as INamedTypeSymbol;
        if (type == null || type.TypeKind != TypeKind.Struct && type.TypeKind != TypeKind.Class
            && type.TypeKind != TypeKind.Interface)
        {
            return false;
        }
        var def = type.OriginalDefinition;
        var ns = def.ContainingNamespace?.ToDisplayString() ?? "";
        return def.Name switch
        {
            "List" or "LinkedList" or "Collection" => ns == "System.Collections.Generic" || ns == "System.Collections.ObjectModel",
            "ArrayList" => ns == "System.Collections",
            "IList" or "ICollection" => ns is "System.Collections" or "System.Collections.Generic",
            _ => false,
        };
    }

    // ─── 语法构造辅助 ───────────────────────────────────────────────

    private static ExpressionSyntax Invoke(string name, ExpressionSyntax arg)
    {
        ExpressionSyntax target = name.Contains('.')
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(name[..name.IndexOf('.')]),
                SyntaxFactory.IdentifierName(name[(name.IndexOf('.') + 1)..]))
            : SyntaxFactory.IdentifierName(name);
        return SyntaxFactory.InvocationExpression(
            target,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(arg))));
    }

    private static ExpressionSyntax Wrap(ExpressionSyntax expr, string name) => Invoke(name, expr);

    private static LiteralExpressionSyntax IntLiteral(int value)
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));

    private static bool IsArithmeticOp(SyntaxKind kind)
        => kind is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
            or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression
            or SyntaxKind.ModuloExpression;

    private static bool IsComparisonOp(SyntaxKind kind)
        => kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;

    private void WarnOnce(string message)
    {
        if (_emittedDiagnostics.Add(message))
        {
            _context.Diagnostics.Warning(message);
        }
    }
}
