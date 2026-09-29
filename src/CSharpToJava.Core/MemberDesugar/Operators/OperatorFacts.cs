using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>谓词层（设计 §4.1 OperatorFacts）：一切以绑定符号为准。</summary>
internal static class OperatorFacts
{
    public static bool IsIncrementOrDecrement(string metadataName)
        => metadataName is "op_Increment" or "op_Decrement";

    public static bool IsTrueOrFalse(string metadataName)
        => metadataName is "op_True" or "op_False";

    public static bool IsGateAndOr(string metadataName)
        => metadataName is "op_BitwiseAnd" or "op_BitwiseOr";

    /// <summary>声明了 op_True/op_False 的门控类型（设计决策 7④：其 &amp;/| 保留给 JK）。</summary>
    public static bool IsGateType(INamedTypeSymbol type)
        => type.GetMembers().OfType<IMethodSymbol>().Any(m => IsTrueOrFalse(m.Name));

    public static bool HasSourceDeclaration(IMethodSymbol m)
        => m.Locations.Any(l => l.IsInSource);

    /// <summary>接口 static abstract/virtual 运算符（C# 11 generic math，CS2J5175）。</summary>
    public static bool IsInterfaceStaticAbstract(IMethodSymbol m)
        => m.ContainingType is { TypeKind: TypeKind.Interface } && m.IsStatic
           && (m.IsAbstract || m.IsVirtual);

    public static bool IsUserDefinedOperatorKind(IMethodSymbol m)
        => m.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion;

    /// <summary>提升展开可安全引用两次的运算元形态（设计 §4.3 SB-Lifted 边界）：
    /// 局部/参数标识符，或 this/标识符接收者上的字段访问。</summary>
    public static bool IsSimpleLiftedOperand(ExpressionSyntax operand, SemanticModel model)
    {
        if (operand is IdentifierNameSyntax
            && model.GetSymbolInfo(operand).Symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol)
            return true;
        if (operand is MemberAccessExpressionSyntax ma
            && ma.Expression is ThisExpressionSyntax or IdentifierNameSyntax
            && model.GetSymbolInfo(ma).Symbol is IFieldSymbol)
            return true;
        return false;
    }

    /// <summary>复合赋值可安全内联（左值求值两次无副作用）的左值形态（决策 2b）：
    /// 标识符 / this / base，或以它们为根的字段访问链（镜像 C11 IsSimpleReceiver 纪律）。
    /// 其余接收者形态须在语句位提升 `__cs2j_recv`，否则分析期整 plan 跳过（CS2J5179）。</summary>
    public static bool IsSimpleLvalue(ExpressionSyntax left) => left switch
    {
        IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax => true,
        MemberAccessExpressionSyntax ma => IsSimpleLvalue(ma.Expression),
        ParenthesizedExpressionSyntax p => IsSimpleLvalue(p.Expression),
        _ => false,
    };
}
