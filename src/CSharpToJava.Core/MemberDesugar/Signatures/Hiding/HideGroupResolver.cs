using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Hiding;

/// <summary>隐藏形态（设计决策 9/14）：new 令牌/隐式（CS0108）/new virtual-abstract（新虚槽）/静态。</summary>
internal enum HideFlavor
{
    New,
    Implicit,
    NewSlot,
    Static,
}

/// <summary>一个被隐藏成员（非 override 的派生成员，基类链上存在同名同槽成员）。</summary>
internal sealed class HiddenMember
{
    public required ISymbol Member { get; init; }
    public required HideFlavor Flavor { get; init; }
    /// <summary>体为纯 base 委托（NH-3 白名单候选，保留同名桥接）。</summary>
    public required bool IsPureBaseDelegation { get; init; }
    public required string DeclaringFile { get; init; }
    /// <summary>newslot 链组键（NH-2）；非 newslot 为成员自身键。</summary>
    public required string GroupId { get; init; }
}

/// <summary>newslot 虚槽链组（NH-2）：组内= new virtual/abstract 声明 + 全部派生 override 其槽的方法。</summary>
internal sealed class HideGroup
{
    public required string GroupId { get; init; }
    public required IReadOnlyList<ISymbol> Members { get; init; }
}

/// <summary>
/// 隐藏组索引/解析器（设计 §4.2-3）：IsHideByName 语义（含隐式隐藏 CS0108 与显式 new 令牌）、
/// new virtual/abstract newslot 链成组、纯 base 委托白名单判定（镜像
/// <c>PropertyTransformer.IsPureBaseDelegation</c>，泛化到方法/索引器/事件）。
/// </summary>
internal sealed class HideGroupResolver
{
    public required IReadOnlyList<HiddenMember> HiddenMembers { get; init; }
    public required IReadOnlyList<HideGroup> Groups { get; init; }

    public static HideGroupResolver Build(MemberDesugarAnalysisContext context)
    {
        var hidden = new List<HiddenMember>();
        var allMethods = new List<IMethodSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = model.SyntaxTree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                var symbol = ResolveDeclaredSymbol(node, model);
                if (symbol is null) continue;

                if (symbol is IMethodSymbol method
                    && method.MethodKind == MethodKind.Ordinary && method.IsOverride)
                    allMethods.Add(method);

                if (!seen.Add(symbol)) continue;
                if (symbol.IsOverride) continue; // override 不是隐藏
                if (IsExcludedKind(symbol)) continue;

                var baseMember = FindHiddenBase(symbol);
                if (baseMember is null) continue;

                var modifiers = GetModifiers(node);
                var hasNew = modifiers.Any(m => m.IsKind(SyntaxKind.NewKeyword));
                var isVirtual = modifiers.Any(m =>
                    m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.AbstractKeyword));

                var flavor = symbol.IsStatic
                    ? HideFlavor.Static
                    : hasNew && isVirtual ? HideFlavor.NewSlot
                    : hasNew ? HideFlavor.New
                    : HideFlavor.Implicit;

                hidden.Add(new HiddenMember
                {
                    Member = symbol,
                    Flavor = flavor,
                    IsPureBaseDelegation = IsPureBaseDelegation(symbol, node),
                    DeclaringFile = SignatureExplicitizationAnalyzer.FirstSourceFile(symbol),
                    GroupId = GroupIdFor(symbol),
                });
            }
        }

        return new HideGroupResolver
        {
            HiddenMembers = hidden,
            Groups = BuildNewSlotGroups(hidden, allMethods),
        };
    }

    // ---- new virtual/abstract newslot 链成组（NH-2）----

    private static IReadOnlyList<HideGroup> BuildNewSlotGroups(
        IReadOnlyList<HiddenMember> hidden, IReadOnlyList<IMethodSymbol> allMethods)
    {
        var newSlots = hidden.Where(h => h.Flavor == HideFlavor.NewSlot).ToList();
        if (newSlots.Count == 0) return Array.Empty<HideGroup>();

        var byMember = newSlots.ToDictionary(
            h => h.Member, h => h.GroupId, (IEqualityComparer<ISymbol>)SymbolEqualityComparer.Default);
        var members = new Dictionary<string, List<ISymbol>>(StringComparer.Ordinal);
        foreach (var h in newSlots)
            members[h.GroupId] = new List<ISymbol> { h.Member };

        foreach (var method in allMethods)
        {
            for (var ov = method.OverriddenMethod; ov != null; ov = ov.OverriddenMethod)
            {
                // newslot 声明的符号与原定义比较（构造/开放定义差异靠 OriginalDefinition 归一）。
                var key = ov.OriginalDefinition;
                var match = byMember.Keys.FirstOrDefault(k =>
                    SymbolEqualityComparer.Default.Equals(
                        ((ISymbol)k).OriginalDefinition, key));
                if (match == null) continue;
                var groupId = byMember[match];
                if (!members[groupId].Contains(method, SymbolEqualityComparer.Default))
                    members[groupId].Add(method);
                break;
            }
        }

        return members.Select(kv => new HideGroup
        {
            GroupId = kv.Key,
            Members = kv.Value,
        }).ToList();
    }

    // ---- IsHideByName 语义 ----

    private static ISymbol? FindHiddenBase(ISymbol member)
    {
        for (var baseType = member.ContainingType?.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            foreach (var candidate in baseType.GetMembers(member.Name))
            {
                if (candidate.DeclaredAccessibility == Accessibility.Private) continue;
                if (candidate.IsStatic != member.IsStatic) continue;
                if (!SignatureMatches(member, candidate)) continue;
                return candidate;
            }
        }
        return null;
    }

    private static bool SignatureMatches(ISymbol member, ISymbol candidate)
    {
        if (member is IMethodSymbol ma && candidate is IMethodSymbol mb)
            return ParametersMatch(ma.Parameters, mb.Parameters);
        if (member is IPropertySymbol { IsIndexer: true } pa
            && candidate is IPropertySymbol { IsIndexer: true } pb)
            return ParametersMatch(pa.Parameters, pb.Parameters);
        if (member is IPropertySymbol && candidate is IPropertySymbol) return true;
        if (member is IEventSymbol && candidate is IEventSymbol) return true;
        return false;
    }

    private static bool ParametersMatch(
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> a,
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!SymbolEqualityComparer.Default.Equals(a[i].Type, b[i].Type)) return false;
        return true;
    }

    // ---- 纯 base 委托白名单（NH-3，镜像 PropertyTransformer.IsPureBaseDelegation）----

    internal static bool IsPureBaseDelegation(ISymbol member, SyntaxNode node)
    {
        var name = member.Name;
        switch (node)
        {
            case MethodDeclarationSyntax m:
                return SingleStatementDelegatesToBase(
                    m.Body, m.ExpressionBody?.Expression, name);
            case PropertyDeclarationSyntax p:
                return AccessorsDelegateToBase(p.AccessorList, name);
            case IndexerDeclarationSyntax idx:
                return AccessorsDelegateToBase(idx.AccessorList, name);
            case EventDeclarationSyntax e:
                return AccessorsDelegateToBase(e.AccessorList, name);
            default:
                return false;
        }
    }

    private static bool AccessorsDelegateToBase(AccessorListSyntax? list, string name)
    {
        if (list is null || list.Accessors.Count == 0) return false;
        return list.Accessors.All(a =>
            SingleStatementDelegatesToBase(a.Body, a.ExpressionBody?.Expression, name));
    }

    private static bool SingleStatementDelegatesToBase(
        BlockSyntax? body, ExpressionSyntax? expressionBody, string name)
    {
        ExpressionSyntax? expr = expressionBody;
        if (expr is null && body is { Statements.Count: 1 })
        {
            expr = body.Statements[0] switch
            {
                ReturnStatementSyntax r => r.Expression,
                ExpressionStatementSyntax e => e.Expression,
                _ => null,
            };
        }
        if (expr is null) return false;

        // base.Name(...) / base.Name / base.Name = value / base.Name op= value
        return expr switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inv } => BaseName(inv, name),
            MemberAccessExpressionSyntax ma => BaseName(ma, name),
            AssignmentExpressionSyntax assign => IsBaseTarget(assign.Left, name),
            _ => false,
        };
    }

    private static bool IsBaseTarget(ExpressionSyntax left, string name)
        => left is MemberAccessExpressionSyntax ma && BaseName(ma, name);

    private static bool BaseName(MemberAccessExpressionSyntax ma, string name)
        => ma.Expression is BaseExpressionSyntax
           && string.Equals(ma.Name.Identifier.ValueText, name, StringComparison.Ordinal);

    // ---- 小工具 ----

    private static bool IsExcludedKind(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.MethodKind is not (MethodKind.Ordinary
            or MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove),
        _ => false,
    };

    private static ISymbol? ResolveDeclaredSymbol(SyntaxNode node, SemanticModel model) => node switch
    {
        MethodDeclarationSyntax m => model.GetDeclaredSymbol(m),
        PropertyDeclarationSyntax p => model.GetDeclaredSymbol(p),
        IndexerDeclarationSyntax i => model.GetDeclaredSymbol(i),
        EventDeclarationSyntax e => model.GetDeclaredSymbol(e),
        EventFieldDeclarationSyntax ef when ef.Declaration.Variables.Count > 0
            => model.GetDeclaredSymbol(ef.Declaration.Variables[0]),
        _ => null,
    };

    private static SyntaxTokenList GetModifiers(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Modifiers,
        PropertyDeclarationSyntax p => p.Modifiers,
        IndexerDeclarationSyntax i => i.Modifiers,
        EventDeclarationSyntax e => e.Modifiers,
        EventFieldDeclarationSyntax ef => ef.Modifiers,
        _ => default,
    };

    internal static string GroupIdFor(ISymbol member)
        => $"{member.ContainingType?.ToDisplayString() ?? "?"}::{member.Name}";
}