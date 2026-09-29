using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Events;

/// <summary>事件规则纯判定：命名派生（D2）、修饰符平移（D12/D14）、特性路由（D16）、
/// 词法含入类型判定（D8/D9）。修饰符规则镜像 C11 AccessorsToMethods.BuildMethodModifiers。</summary>
internal static class EventFacts
{
    public static string AdderName(string eventName) => "add_" + eventName;

    public static string RemoverName(string eventName) => "remove_" + eventName;

    public static bool IsPartial(MemberDeclarationSyntax evt)
        => evt.Modifiers.Any(SyntaxKind.PartialKeyword);

    /// <summary>partial 事件判定（D20）：声明节点带 partial，或任一声明语法部件带 partial
    /// （C# 13 的实现部件不带 partial 关键字，故按符号的声明语法引用全量检查）。</summary>
    public static bool IsPartialEvent(IEventSymbol evt, MemberDeclarationSyntax decl)
    {
        if (IsPartial(decl)) return true;
        foreach (var reference in evt.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is MemberDeclarationSyntax d && IsPartial(d))
                return true;
        return false;
    }

    /// <summary>字段修饰符：固定 private（+ static 随事件）。</summary>
    public static SyntaxTokenList BuildFieldModifiers(MemberDeclarationSyntax evt)
    {
        var mods = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (evt.Modifiers.Any(SyntaxKind.StaticKeyword))
            mods.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        return SyntaxFactory.TokenList(mods);
    }

    /// <summary>
    /// 方法修饰符：事件修饰符（static/virtual/override/sealed/abstract/new）平移；
    /// 可访问性 = 访问器自带否则事件的；readonly/partial 不传递；接口成员不带修饰符
    /// （static 接口成员保留 static）；显式接口实现不带任何修饰符。
    /// </summary>
    public static SyntaxTokenList BuildMethodModifiers(
        MemberDeclarationSyntax evt,
        AccessorDeclarationSyntax? accessor,
        bool inInterface,
        bool isExplicitInterfaceImplementation)
    {
        if (isExplicitInterfaceImplementation)
            return SyntaxFactory.TokenList();

        if (inInterface)
            return evt.Modifiers.Any(SyntaxKind.StaticKeyword)
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword))
                : SyntaxFactory.TokenList();

        var mods = new List<SyntaxToken>();
        var accessorAccessibility = accessor?.Modifiers.FirstOrDefault(IsAccessibility) ?? default;
        var chosen = accessorAccessibility != default
            ? accessorAccessibility
            : evt.Modifiers.FirstOrDefault(IsAccessibility);
        if (chosen != default)
            mods.Add(chosen);

        foreach (var m in evt.Modifiers)
        {
            if (IsAccessibility(m)) continue;
            if (m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.PartialKeyword)) continue;
            mods.Add(m);
        }

        return SyntaxFactory.TokenList(mods);
    }

    // 复合可访问性（protected internal / private protected）在语法上是两个
    // 关键字 token 的组合，按 token 集合整体平移即可。
    private static readonly SyntaxKind[] AccessibilityKinds =
    {
        SyntaxKind.PublicKeyword,
        SyntaxKind.PrivateKeyword,
        SyntaxKind.ProtectedKeyword,
        SyntaxKind.InternalKeyword,
    };

    private static bool IsAccessibility(SyntaxToken token)
        => AccessibilityKinds.Contains(token.Kind());

    public enum EventRoute
    {
        Field,
        Methods,
        Unroutable,
    }

    /// <summary>D16：显式 target 直接路由（field/method；event target 无落点 → 不可路由）；
    /// 无 target 按 AttributeUsage 目标感知路由（全 Field → 字段，任一 Method → 两方法，
    /// 仅 Event 合法 → 不可路由 CS2J5156）。</summary>
    public static EventRoute RouteAttributeList(
        AttributeListSyntax list, SemanticModel model, bool fieldGenerated)
    {
        var target = list.Target?.Identifier.ValueText;
        return target switch
        {
            "field" => fieldGenerated ? EventRoute.Field : EventRoute.Unroutable,
            "method" => EventRoute.Methods,
            "event" => EventRoute.Unroutable,
            _ => RouteByUsage(list, model, fieldGenerated),
        };
    }

    private static EventRoute RouteByUsage(AttributeListSyntax list, SemanticModel model, bool allowField)
    {
        var allField = list.Attributes.Count > 0 && allowField;
        var anyMethod = false;
        foreach (var attr in list.Attributes)
        {
            var usage = GetValidOn(model, attr);
            if (!usage.HasFlag(AttributeTargets.Field)) allField = false;
            if (usage.HasFlag(AttributeTargets.Method)) anyMethod = true;
        }
        if (allField) return EventRoute.Field;
        if (anyMethod) return EventRoute.Methods;
        return EventRoute.Unroutable;
    }

    /// <summary>复用 C11 AttributeRouting.GetValidOn 的用法位提取逻辑（事件侧自持副本）。</summary>
    public static AttributeTargets GetValidOn(SemanticModel model, AttributeSyntax attr)
    {
        var attrClass = (model.GetSymbolInfo(attr).Symbol as IMethodSymbol)?.ContainingType;
        if (attrClass == null) return AttributeTargets.All;
        var usage = attrClass.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass is { Name: "AttributeUsageAttribute" }
            && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "System");
        if (usage != null && usage.ConstructorArguments.Length > 0
            && usage.ConstructorArguments[0].Value is int v)
            return (AttributeTargets)v;
        return AttributeTargets.All;
    }

    /// <summary>站点词法含入类型（D8/D9）：站点在 lambda/局部函数内时 ContainingType 仍是宿主类型。</summary>
    public static INamedTypeSymbol? EnclosingType(SemanticModel model, SyntaxNode node)
        => model.GetEnclosingSymbol(node.SpanStart)?.ContainingType;
}
