using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>访问器 → 方法声明的共享骨架（设计 §4.5）：命名与修饰符平移规则。
/// 属性面规则负责归类；语法构造在 PropertyDeclarationExpander（C12/C13 复用本类命名与修饰符规则）。</summary>
internal static class AccessorsToMethods
{
    /// <summary>get 访问器方法名（C# 规范访问器名，设计决策 1：天然零碰撞）。</summary>
    public static string GetterName(string propertyName) => "get_" + propertyName;

    /// <summary>set 访问器方法名（init 访问器同样映射到 set_，设计决策 4）。</summary>
    public static string SetterName(string propertyName) => "set_" + propertyName;

    // 复合可访问性（protected internal / private protected）在语法上是两个
    // 关键字 token 的组合，按 token 集合整体平移即可。
    private static readonly SyntaxKind[] AccessibilityKinds =
    {
        SyntaxKind.PublicKeyword,
        SyntaxKind.PrivateKeyword,
        SyntaxKind.ProtectedKeyword,
        SyntaxKind.InternalKeyword,
    };

    public static bool IsAccessibility(SyntaxToken token)
        => AccessibilityKinds.Contains(token.Kind());

    /// <summary>
    /// 方法修饰符：非可访问性修饰符（static/virtual/override/sealed/new/abstract）自属性平移；
    /// 可访问性 = 访问器自带（private set 等）否则属性的；required/partial/readonly 不传递
    /// （设计决策 5/6）。接口成员与显式接口实现不带任何修饰符（static 接口成员保留 static）。
    /// </summary>
    public static SyntaxTokenList BuildMethodModifiers(
        PropertyDeclarationSyntax property,
        AccessorDeclarationSyntax accessor,
        bool inInterface,
        bool isExplicitInterfaceImplementation)
    {
        if (isExplicitInterfaceImplementation)
            return SyntaxFactory.TokenList();

        if (inInterface)
            return property.Modifiers.Any(SyntaxKind.StaticKeyword)
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                : SyntaxFactory.TokenList<SyntaxToken>();

        var mods = new List<SyntaxToken>();
        var accessorAccessibility = accessor.Modifiers.FirstOrDefault(IsAccessibility);
        var chosen = accessorAccessibility != default
            ? accessorAccessibility
            : property.Modifiers.FirstOrDefault(IsAccessibility);
        if (chosen != default)
            mods.Add(chosen);

        foreach (var m in property.Modifiers)
        {
            if (IsAccessibility(m)) continue;
            if (m.IsKind(SyntaxKind.RequiredKeyword)
                || m.IsKind(SyntaxKind.PartialKeyword)
                || m.IsKind(SyntaxKind.ReadOnlyKeyword))
                continue;
            mods.Add(m);
        }

        return SyntaxFactory.TokenList(mods);
    }
}
