using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression.Utilities;

/// <summary>
/// C# 泛型方法调用（显式类型实参）在 Java 侧仍是泛型方法调用，类型实参不具体化；
/// 当表达式不在赋值/实参等可推断槽位时（STJ 的 `Deserialize&lt;T&gt;(json)` 后面
/// 直接跟成员/元素访问），Java 把类型变量推断成 Object，成员访问就落到 Object
/// 上（真实链：PropertyVisibilityTests 4 处「找不到符号 getMyString()」、
/// CollectionTests 1 处「需要数组，但找到 Object」）。这里把接收者表达式强转回
/// C# 结果类型的映射形态。
/// </summary>
internal static class GenericInvocationReceiverCoercion
{
    public static string CoerceOrSame(ExpressionSyntax receiver, string text, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return text;

        var inner = receiver;
        while (inner is ParenthesizedExpressionSyntax parenthesized)
            inner = parenthesized.Expression;

        if (inner is not InvocationExpressionSyntax invocation)
            return text;

        // 只有显式写了类型实参的泛型方法会丢类型：Java 侧没有类型见证，调用点又
        // 没有可推断的目标槽位。返回类型是方法自身类型参数时才会被推断成 Object；
        // 已构造符号的 ReturnType 已被替换，必须看原始定义。
        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { TypeArguments.Length: > 0 } method
            || method.ConstructedFrom.ReturnType is not ITypeParameterSymbol)
        {
            return text;
        }

        var type = context.GetTypeInfo(invocation).Type;
        if (type == null || type.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
            return text;

        var mapped = context.MapType(type);
        if (string.IsNullOrWhiteSpace(mapped)
            || mapped is "Object" or "var"
            || mapped is "int" or "long" or "short" or "byte" or "char"
                or "boolean" or "float" or "double")
        {
            return text;
        }

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith($"(({mapped})", StringComparison.Ordinal)
            || trimmed.StartsWith($"({mapped})", StringComparison.Ordinal))
        {
            return text;
        }

        context.AddImportsForTypePublic(type.ToDisplayString());
        return $"(({mapped}) {text})";
    }
}
