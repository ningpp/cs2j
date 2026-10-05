using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// dynamic 站点共享判定（C25 设计 §4.1/§4.3），与 <see cref="ExpressionTreeFacts"/> 并列，
/// 供 C25 自身及 C23/C24/C35/C36/C14 等表达式族在 dynamic 子树内跳过各自改写。
/// 方法面是 MemberDesugar 私有 <c>MemberDesugarSyntaxFacts.IsDynamic</c>（仅 TypeKind
/// 接收者/节点两面）的超集：增加 ConvertedType、CandidateReason 晚绑定信号、祖先边界
/// 传播、var 隐式 dynamic 声明与元数据 <c>[Dynamic]</c> 识别。
/// </summary>
public static class DynamicSiteFacts
{
    /// <summary>BCL DynamicAttribute 全名（D12）；同名自定义特性不认。</summary>
    public const string DynamicAttributeFullName = "System.Runtime.CompilerServices.DynamicAttribute";

    // DynamicAttribute 在不同 .NET 世代的宿主程序集名；仅用于排除同名自定义特性（D12）。
    private static readonly HashSet<string> BclHostAssemblies = new(StringComparer.Ordinal)
    {
        "System.Linq.Expressions",
        "System.Runtime",
        "System.ObjectModel",
        "System.Core",
        "mscorlib",
        "netstandard",
    };

    /// <summary>
    /// 表达式是否参与动态绑定：TypeInfo.Type/ConvertedType 为 dynamic，或符号查询为
    /// 晚绑定候选。注意 Roslyn 的实际信号是 <see cref="CandidateReason.LateBound"/>
    /// （不存在 CandidateReason.Dynamic 成员，C25 设计文档用语以本实况为准）。
    /// </summary>
    public static bool IsDynamicallyBoundNode(SemanticModel model, SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);

        var typeInfo = model.GetTypeInfo(node);
        if (typeInfo.Type?.TypeKind == TypeKind.Dynamic
            || typeInfo.ConvertedType?.TypeKind == TypeKind.Dynamic)
        {
            return true;
        }

        var symbolInfo = model.GetSymbolInfo(node);
        return symbolInfo.CandidateReason == CandidateReason.LateBound;
    }

    /// <summary>
    /// 祖先传播（设计 §4.3）：以最近的 invocation/元素访问/二元/赋值/一元/条件/await
    /// 边界为准——dynamic 实参使整个 invocation 动态，但不穿过语句边界波及兄弟语句。
    /// 最近边界非动态即停止传播。
    /// </summary>
    public static bool IsDynamicExpression(SemanticModel model, SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);

        if (IsDynamicallyBoundNode(model, node))
        {
            return true;
        }

        for (var current = node.Parent; current != null && current is not StatementSyntax; current = current.Parent)
        {
            var isBoundary = current is InvocationExpressionSyntax
                or ElementAccessExpressionSyntax
                or BinaryExpressionSyntax
                or AssignmentExpressionSyntax
                or PostfixUnaryExpressionSyntax
                or PrefixUnaryExpressionSyntax
                or ConditionalExpressionSyntax
                or AwaitExpressionSyntax;
            if (!isBoundary)
            {
                continue;
            }

            // 最近边界决定传播结果：动态则真，非动态不继续向外扩散。
            return IsDynamicallyBoundNode(model, current);
        }

        return false;
    }

    /// <summary>
    /// 显式 <c>dynamic</c> 局部/字段声明 与 <c>var</c> 推断为 dynamic 的隐式局部等价（D6）。
    /// <c>object</c> 局部/字段是真实静态类型，返回 false；object+[Dynamic] 走
    /// <see cref="HasDynamicAttribute"/>（DS019），不在本判定。
    /// </summary>
    public static bool IsDynamicDeclaration(SemanticModel model, VariableDeclaratorSyntax declarator)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(declarator);

        var symbol = model.GetDeclaredSymbol(declarator);
        var type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol evt => evt.Type,
            _ => null,
        };

        if (type?.TypeKind == TypeKind.Dynamic)
        {
            return true;
        }

        // 语义模型异常时的词法兜底：声明类型语法直接是 dynamic 关键字。
        if (declarator.Parent is VariableDeclarationSyntax { Type: IdentifierNameSyntax id }
            && id.Identifier.Text == "dynamic")
        {
            return type == null || type.TypeKind == TypeKind.Dynamic;
        }

        return false;
    }

    /// <summary>
    /// 符号是否直接带 BCL <c>System.Runtime.CompilerServices.DynamicAttribute</c>（D12）。
    /// 方法返回类型上的特性在 <see cref="IMethodSymbol.GetReturnTypeAttributes"/>，
    /// 由调用方配合 <see cref="IsBclDynamicAttribute"/> 判定。
    /// </summary>
    public static bool HasDynamicAttribute(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.GetAttributes().Any(IsBclDynamicAttribute);
    }

    /// <summary>特性是否为 BCL DynamicAttribute（全名 + 宿主程序集双重确认，排除同名自定义特性）。</summary>
    public static bool IsBclDynamicAttribute(AttributeData attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var attributeClass = attribute.AttributeClass;
        if (attributeClass == null
            || attributeClass.ToDisplayString() != DynamicAttributeFullName)
        {
            return false;
        }

        var assemblyName = attributeClass.ContainingAssembly?.Name;
        return assemblyName == null || BclHostAssemblies.Contains(assemblyName);
    }

    /// <summary>
    /// 类型（含数组/指针/泛型实参，如 EventHandler&lt;dynamic&gt;）是否含 dynamic 分量。
    /// </summary>
    public static bool TypeContainsDynamic(ITypeSymbol? type)
    {
        switch (type)
        {
            case null:
                return false;
            case { TypeKind: TypeKind.Dynamic }:
                return true;
            case IArrayTypeSymbol array:
                return TypeContainsDynamic(array.ElementType);
            case IPointerTypeSymbol pointer:
                return TypeContainsDynamic(pointer.PointedAtType);
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments)
                {
                    if (TypeContainsDynamic(argument))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// 节点是否位于表达式树 lambda 内（CS1963 共同禁区）：祖先找最近 lambda 再调
    /// <see cref="ExpressionTreeFacts.IsExpressionTreeLambda"/>（JK/GD 先例）。
    /// </summary>
    public static bool IsInsideExpressionTree(SemanticModel model, SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);

        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is LambdaExpressionSyntax lambda)
            {
                return ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda);
            }
        }

        return false;
    }
}
