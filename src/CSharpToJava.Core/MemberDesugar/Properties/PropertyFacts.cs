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

    /// <summary>访问器体使用 C#13 field 关键字（CS2J5122；FieldExpression + 标识符文本双判）。</summary>
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

    /// <summary>接口内声明（含 DIM）。</summary>
    public static bool IsInterfaceMember(IPropertySymbol prop)
        => prop.ContainingType is { TypeKind: TypeKind.Interface };
}
