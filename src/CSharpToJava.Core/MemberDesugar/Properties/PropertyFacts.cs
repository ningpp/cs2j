using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>属性分类谓词（设计 §4.4 PD-1/PD-2/PD-3/PD-11）。</summary>
internal static class PropertyFacts
{
    /// <summary>自动属性：全部访问器体均为分号（语法判），符号非 abstract/extern。</summary>
    public static bool IsAutoProperty(PropertyDeclarationSyntax decl, IPropertySymbol prop)
        => decl.AccessorList != null
           && decl.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null)
           && !prop.IsAbstract && !prop.IsExtern;

    /// <summary>需要后备字段：自动属性，或带初始化器的 get-only 属性（编译器同款降级）。</summary>
    public static bool NeedsBackingField(PropertyDeclarationSyntax decl, IPropertySymbol prop)
        => IsAutoProperty(decl, prop)
           || (prop.GetMethod != null && prop.SetMethod == null
               && decl.Initializer != null && !prop.IsAbstract);

    /// <summary>init 访问器（放宽为普通 set，设计决策 4）。</summary>
    public static bool HasInitAccessor(PropertyDeclarationSyntax decl)
        => decl.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

    /// <summary>required 修饰符（编译期约束解除，设计决策 5）。</summary>
    public static bool IsRequired(PropertyDeclarationSyntax decl)
        => decl.Modifiers.Any(SyntaxKind.RequiredKeyword);

    /// <summary>ref/ref-readonly 返回（CS2J5115）。</summary>
    public static bool ReturnsByRef(IPropertySymbol prop)
        => prop.RefKind != RefKind.None || prop.ReturnsByRef;

    /// <summary>partial 属性（CS2J5114；C#13 语法）。</summary>
    public static bool IsPartial(PropertyDeclarationSyntax decl)
        => decl.Modifiers.Any(SyntaxKind.PartialKeyword);

    /// <summary>接口 static abstract/virtual（CS2J5116）。</summary>
    public static bool IsStaticAbstractInterfaceMember(IPropertySymbol prop)
        => prop.IsStatic && (prop.IsAbstract || prop.IsVirtual)
           && prop.ContainingType is { TypeKind: TypeKind.Interface };

    /// <summary>访问器体使用 C# 14 field 关键字。C50 子集开关关闭时走声明级跳过
    /// （CS2J5122）；开关启用时改由 <see cref="FieldKeywordResolver"/> 语义解析，
    /// 本判定仅作关闭路径与 FK-9 residual 兜底（FieldExpression + 标识符文本双判）。</summary>
    public static bool UsesFieldKeyword(PropertyDeclarationSyntax decl)
        => decl.AccessorList?.Accessors.Any(UsesFieldKeyword) == true;

    private static bool UsesFieldKeyword(AccessorDeclarationSyntax accessor)
        => accessor.DescendantNodes().Any(IsFieldKeywordNode);

    private static bool IsFieldKeywordNode(SyntaxNode node)
        => node.IsKind(SyntaxKind.FieldExpression)
           || (node is IdentifierNameSyntax id && id.Identifier.ValueText == "field");

    /// <summary>位置 record 参数属性（CS2J5123）：位置落在 record 参数上（无属性声明节点）。</summary>
    public static bool IsPositionalRecordProperty(IPropertySymbol prop)
        => prop.ContainingType is { IsRecord: true }
           && prop.Locations.Any(l => l.IsInSource
               && l.SourceTree!.GetRoot().FindNode(l.SourceSpan)
                   .AncestorsAndSelf().OfType<ParameterSyntax>().Any());

    /// <summary>显式接口实现。</summary>
    public static bool IsExplicitInterfaceImplementation(IPropertySymbol prop)
        => prop.ExplicitInterfaceImplementations.Length > 0;

    /// <summary>
    /// 本属性以显式或隐式方式实现的接口属性中，是否有落在元数据上的（BCL/外部程序集/
    /// 不参与本次编译的工程：无声明语法）。这类接口本体不经过本 pass 降形，属性展开成
    /// get_X/set_X 方法后类不再以属性实现该接口槽（CS0535/CS0470；显式实现则是 CS0683），
    /// 因此必须保留原属性。例：迭代器降级产物用 <c>public T Current</c> 隐式实现
    /// <c>IEnumerator&lt;T&gt;.Current</c>。
    /// </summary>
    public static bool ImplementsMetadataInterfaceProperty(IPropertySymbol prop)
    {
        if (prop.ExplicitInterfaceImplementations.Any(
                implemented => implemented.DeclaringSyntaxReferences.Length == 0))
        {
            return true;
        }

        var type = prop.ContainingType;
        if (type == null || type.AllInterfaces.Length == 0) return false;

        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(prop.Name).OfType<IPropertySymbol>())
            {
                if (member.DeclaringSyntaxReferences.Length == 0
                    && SymbolEqualityComparer.Default.Equals(
                        type.FindImplementationForInterfaceMember(member), prop))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>接口内声明（含 DIM）。</summary>
    public static bool IsInterfaceMember(IPropertySymbol prop)
        => prop.ContainingType is { TypeKind: TypeKind.Interface };
}
