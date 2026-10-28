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

    /// <summary>
    /// MSTest 框架契约属性：测试运行器按「属性名 TestContext」反射注入
    /// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestContext</c> 实例。
    /// 展开成 get_TestContext/set_TestContext 方法后属性消失，反射注入永远为 null，
    /// 所有使用点（TestContext.WriteLine / TestContext.DeploymentDirectory）全部 NRE。
    /// 故按语义符号判定并整体保留（实例/静态、自动属性/显式访问器同判）。
    /// </summary>
    public static bool IsTestFrameworkInjectionProperty(
        IPropertySymbol prop, SemanticModel model, TypeSyntax typeSyntax)
    {
        if (prop.Name != MstestTestContextName) return false;
        if (prop.Type is not INamedTypeSymbol { Name: MstestTestContextName } type) return false;

        // 正常情形（编译带 MSTest 引用）：按类型符号的完全限定名判定。
        if (type.TypeKind != TypeKind.Error)
            return type.ContainingNamespace?.ToDisplayString() == MstestTestContextNamespace;

        // member-desugar 预处理编译只挂 BCL 引用（ProjectUnsignedTypePreprocessor
        // .GetBasicReferences），NuGet 里的 MSTest 程序集不参与绑定 → 属性类型退化为
        // IErrorTypeSymbol（Name 仍为 TestContext，命名空间为空）。此时按 C# 名称解析
        // 规则还原类型全名：声明处作用域的 using（含项目级 <Using> 生成的 global using）
        // 必须导入 MSTest 命名空间。判定输入仍是属性/类型符号名，不做声明文本匹配。
        return DeclaredNameResolvesToMstestTestContext(typeSyntax, model);
    }

    private const string MstestTestContextNamespace = "Microsoft.VisualStudio.TestTools.UnitTesting";
    private const string MstestTestContextName = "TestContext";
    private const string MstestTestContextFullName =
        MstestTestContextNamespace + "." + MstestTestContextName;

    private static bool DeclaredNameResolvesToMstestTestContext(
        TypeSyntax typeSyntax, SemanticModel model)
    {
        // 全限定写法：Foo.Bar.TestContext 直接比对全名。
        if (typeSyntax is QualifiedNameSyntax or AliasQualifiedNameSyntax)
        {
            return string.Equals(
                typeSyntax.ToString().Replace("global::", string.Empty, StringComparison.Ordinal),
                MstestTestContextFullName,
                StringComparison.Ordinal);
        }

        if (typeSyntax is not IdentifierNameSyntax { Identifier.ValueText: MstestTestContextName })
            return false;

        // 文件/命名空间作用域内的 using。
        foreach (var ancestor in typeSyntax.AncestorsAndSelf())
        {
            var usings = ancestor switch
            {
                NamespaceDeclarationSyntax ns => ns.Usings,
                FileScopedNamespaceDeclarationSyntax fileScoped => fileScoped.Usings,
                CompilationUnitSyntax unit => unit.Usings,
                _ => default,
            };
            if (ImportsMstestNamespace(usings, requireGlobal: false)) return true;
        }

        // 项目级 <Using Include="..."/> 由预处理链编译成合成 global using 树。
        foreach (var tree in model.Compilation.SyntaxTrees)
        {
            if (tree.GetRoot() is CompilationUnitSyntax root
                && ImportsMstestNamespace(root.Usings, requireGlobal: true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImportsMstestNamespace(
        SyntaxList<UsingDirectiveSyntax> usings, bool requireGlobal)
    {
        foreach (var directive in usings)
        {
            if (directive.Alias != null || directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                continue;
            if (requireGlobal && !directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                continue;
            var name = directive.Name?.ToString();
            if (name == null) continue;
            // 合成 global using 树用「global::Ns」形态（ImplicitGlobalUsings）。
            if (name.StartsWith("global::", StringComparison.Ordinal))
                name = name["global::".Length..];
            if (name == MstestTestContextNamespace) return true;
        }

        return false;
    }
}
