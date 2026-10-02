using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Covariant;

/// <summary>
/// C46 CR 协变返回 plan 构建器（设计 §4.3-2/§4.4 CR-1..CR-4，决策 10/11/12 + 裁决 4）：
/// 逐协变链产出 plan。CR-1 记 Retained（零改写）；CR-3 值类型/不可判 → CS2J5195 原子不动；
/// CR-2 擦除冲突 → 合成基槽签名 bridge + 原体改名 <c>M_cs2jCovariant</c>，并把编译内全部派生
/// override（sub-chain）整组改同一名以保全虚分派；派生 override 自身仍为协变（非 Retained）时
/// 桥接不可证 → CS2J5196（裁决 4 保守优先）。
/// </summary>
internal static class CrPlanBuilder
{
    private sealed class Draft
    {
        public required CovariantChain Chain { get; init; }
        public CrVerdict Verdict { get; set; } = CrVerdict.Retained;
        public bool HasDescendants { get; set; }
        public bool Sealed { get; set; }
        public string? RenamedName { get; set; }
        public string? BridgeName { get; set; }
        public string? BaseReturnTypeText { get; set; }
        public string? BridgeKey { get; set; }
        public HashSet<string> PrimaryKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Renames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Originals { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MatchKeys { get; } = new(StringComparer.Ordinal);
        public List<string> MemberKeys { get; } = new();
        public MemberDesugarOutcome Outcome { get; set; } = MemberDesugarOutcome.Planned;
        public MemberDesugarDiagnosticCode? FailCode { get; set; }
        public string? FailReason { get; set; }
        public SortedSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static void Build(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options.Signatures;
        var chains = context.SignatureAnalysis!.Covariant.Chains;
        var verdictByMember = new Dictionary<IMethodSymbol, CovariantVerdict>(SymbolEqualityComparer.Default);
        foreach (var chain in chains) verdictByMember[chain.Member] = chain.Verdict;

        var overrideIndex = BuildOverrideIndex(context);
        var reservations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var claimed = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var drafts = new List<Draft>();

        // 基链先行（浅深度优先）：桥接组改名先认领其派生 sub-chain，深链后处理时不再重复。
        var ordered = chains
            .OrderBy(chain => Depth(chain.Member))
            .ThenBy(chain => chain.Member.ContainingType?.ToDisplayString(), StringComparer.Ordinal)
            .ThenBy(chain => chain.Member.Name, StringComparer.Ordinal)
            .ThenBy(chain => Span(chain.Member))
            .ToList();

        foreach (var chain in ordered)
        {
            var draft = new Draft { Chain = chain };
            draft.Files.Add(chain.DeclaringFile);
            draft.MemberKeys.Add(CrPlan.SiteKey(chain.Member));

            if (options.IsSkipped(chain.Member))
            {
                draft.Outcome = MemberDesugarOutcome.UserSkipped;
                claimed.Add(chain.Member);
                drafts.Add(draft);
                continue;
            }

            switch (chain.Verdict)
            {
                case CovariantVerdict.Retained:
                    // CR-1：直映保留，仅审计（含 C27 Cs2jWithClone 合成对）。
                    draft.Verdict = CrVerdict.Retained;
                    claimed.Add(chain.Member);
                    drafts.Add(draft);
                    continue;

                case CovariantVerdict.ValueTypeSkipped:
                    // CR-3：值类型卷入或不可判 → CS2J5195 整 plan 原子不动。
                    draft.Verdict = CrVerdict.Skipped;
                    draft.Outcome = MemberDesugarOutcome.Infeasible;
                    draft.FailCode = MemberDesugarDiagnosticCode.SignatureValueTypeCovariant;
                    draft.FailReason = SignatureDiagnostics.ValueTypeCovariant(
                        Fqn(chain.Member), chain.Member.Name,
                        chain.BaseReturnType.ToDisplayString(),
                        chain.DerivedReturnType.ToDisplayString());
                    claimed.Add(chain.Member);
                    drafts.Add(draft);
                    continue;

                case CovariantVerdict.Bridged:
                    draft.Verdict = CrVerdict.Bridged;
                    if (claimed.Contains(chain.Member))
                    {
                        // 已作为更上层桥接组的派生成员整组改名，不再单独桥接。
                        continue;
                    }
                    BuildBridge(context, chain, draft, verdictByMember, overrideIndex, reservations, claimed);
                    drafts.Add(draft);
                    continue;
            }
        }

        ClassifySites(context, drafts);
        foreach (var draft in drafts) builder.Add(ToPlan(draft));
    }

    // ---- CR-2 桥接 + 派生 sub-chain 整组改名 ----

    private static void BuildBridge(
        MemberDesugarAnalysisContext context, CovariantChain chain, Draft draft,
        Dictionary<IMethodSymbol, CovariantVerdict> verdictByMember,
        Dictionary<string, List<IMethodSymbol>> overrideIndex,
        Dictionary<string, HashSet<string>> reservations,
        HashSet<ISymbol> claimed)
    {
        var bridge = chain.Member;
        var descendants = CollectDescendants(bridge, overrideIndex);

        // 派生 override 自身仍是擦除冲突协变（非 Retained）→ 整组改名无法保全桥接虚分派语义，
        // 桥接不可证 → CS2J5196（裁决 4：保守优先，仅在无法桥接时跳过）。
        foreach (var descendant in descendants)
        {
            if (verdictByMember.TryGetValue(descendant, out var verdict)
                && verdict != CovariantVerdict.Retained)
            {
                draft.Outcome = MemberDesugarOutcome.Infeasible;
                draft.FailCode = MemberDesugarDiagnosticCode.SignatureGenericBridgeInfeasible;
                draft.FailReason = SignatureDiagnostics.GenericBridgeInfeasible(Fqn(bridge), bridge.Name);
                claimed.Add(bridge);
                return;
            }
        }

        draft.Sealed = bridge.IsSealed;
        draft.HasDescendants = descendants.Count > 0;
        draft.BridgeName = bridge.Name;
        draft.BridgeKey = CrPlan.SiteKey(bridge);
        draft.RenamedName = ReserveName(bridge.ContainingType, bridge.Name, reservations);
        draft.BaseReturnTypeText = BaseReturnTypeText(context, bridge, chain.BaseReturnType);

        AddRename(draft, bridge, bridge.Name);
        foreach (var descendant in descendants)
        {
            AddRename(draft, descendant, bridge.Name);
            draft.MemberKeys.Add(CrPlan.SiteKey(descendant));
            claimed.Add(descendant);
        }
        claimed.Add(bridge);
    }

    private static void AddRename(Draft draft, IMethodSymbol member, string originalName)
    {
        var key = CrPlan.SiteKey(member);
        draft.PrimaryKeys.Add(key);
        draft.Renames[key] = draft.RenamedName!;
        draft.Originals[key] = originalName;
        draft.MatchKeys.Add(key);
        foreach (var loc in member.Locations.Where(l => l.IsInSource))
            draft.Files.Add(loc.SourceTree!.FilePath);
    }

    // ---- override 派生索引（编译内）----

    private static Dictionary<string, List<IMethodSymbol>> BuildOverrideIndex(
        MemberDesugarAnalysisContext context)
    {
        var index = new Dictionary<string, List<IMethodSymbol>>(StringComparer.Ordinal);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var model in context.Models)
        {
            foreach (var decl in model.SyntaxTree.GetRoot()
                         .DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not IMethodSymbol method) continue;
                if (method.MethodKind != MethodKind.Ordinary || !method.IsOverride) continue;
                if (method.OverriddenMethod is not { } overridden) continue;
                if (!seen.Add(method)) continue;
                var key = CrPlan.SiteKey(overridden);
                if (!index.TryGetValue(key, out var list))
                    index[key] = list = new List<IMethodSymbol>();
                list.Add(method);
            }
        }
        return index;
    }

    private static List<IMethodSymbol> CollectDescendants(
        IMethodSymbol root, Dictionary<string, List<IMethodSymbol>> index)
    {
        var result = new List<IMethodSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { CrPlan.SiteKey(root) };
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!index.TryGetValue(CrPlan.SiteKey(current), out var children)) continue;
            foreach (var child in children)
            {
                if (!seen.Add(CrPlan.SiteKey(child))) continue;
                result.Add(child);
                queue.Enqueue(child);
            }
        }
        return result;
    }

    // ---- 站点分类（CR-2 改名点 + 5194/5193 闸门）----

    private static void ClassifySites(MemberDesugarAnalysisContext context, List<Draft> drafts)
    {
        var matchByKey = new Dictionary<string, List<Draft>>(StringComparer.Ordinal);
        var byOriginalName = new Dictionary<string, List<Draft>>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (draft.Outcome != MemberDesugarOutcome.Planned) continue;
            if (draft.Verdict != CrVerdict.Bridged) continue;
            foreach (var key in draft.MatchKeys)
            {
                if (!matchByKey.TryGetValue(key, out var list))
                    matchByKey[key] = list = new List<Draft>();
                list.Add(draft);
            }
            if (draft.BridgeName != null)
                byOriginalName[draft.BridgeName] = new List<Draft> { draft };
        }
        if (matchByKey.Count == 0) return;

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
            {
                if (node is InvocationExpressionSyntax invocation
                    && SignatureSyntaxFacts.IsReflectionContractSite(invocation, model))
                {
                    foreach (var arg in invocation.ArgumentList.Arguments)
                    {
                        if (arg.Expression is not LiteralExpressionSyntax { Token.Value: string text })
                            continue;
                        foreach (var (name, plans) in byOriginalName)
                            if (MatchesReflectionName(text, name))
                                foreach (var plan in plans) plan.Files.Add(filePath);
                    }
                    continue;
                }

                ExpressionSyntax? expr = node switch
                {
                    MemberAccessExpressionSyntax ma => ma,
                    IdentifierNameSyntax id when id.Parent is not (MemberAccessExpressionSyntax
                        or MemberBindingExpressionSyntax) => id,
                    _ => null,
                };
                if (expr == null) continue;

                var bound = SignatureSyntaxFacts.BoundSymbol(model, expr);
                if (bound == null) continue;
                if (!matchByKey.TryGetValue(CrPlan.SiteKey(bound), out var matched)) continue;
                foreach (var plan in matched)
                {
                    plan.Files.Add(filePath);
                    if (SignatureSyntaxFacts.ClassifyCallSite(expr, model)
                        == SignatureCallSiteClass.Undecidable)
                        Gate(plan, bound.Name, SignatureSyntaxFacts.IsDynamic(expr, model));
                }
            }
        }
    }

    private static void Gate(Draft draft, string member, bool dynamic)
    {
        if (draft.Outcome != MemberDesugarOutcome.Planned) return;
        draft.Outcome = MemberDesugarOutcome.Infeasible;
        draft.FailCode = MemberDesugarDiagnosticCode.SignatureExpressionTreeOrDynamic;
        draft.FailReason = SignatureDiagnostics.ExpressionTreeOrDynamic(member, dynamic);
    }

    // ---- 小工具 ----

    private static CrPlan ToPlan(Draft draft) => new()
    {
        Id = CrPlan.BuildId(draft.Chain.Member),
        Kind = MemberDesugarKind.Signature,
        Symbol = draft.Chain.Member,
        Outcome = draft.Outcome,
        TouchedFiles = draft.Files.ToList(),
        FailCode = draft.FailCode,
        FailReason = draft.FailReason,
        Member = draft.Chain.Member,
        Verdict = draft.Verdict,
        BaseReturnType = draft.Chain.BaseReturnType,
        DerivedReturnType = draft.Chain.DerivedReturnType,
        RenamedName = draft.RenamedName,
        BridgeName = draft.BridgeName,
        BaseReturnTypeText = draft.BaseReturnTypeText,
        Sealed = draft.Sealed,
        HasDescendants = draft.HasDescendants,
        BridgeKey = draft.BridgeKey,
        RenameByKey = draft.Renames,
        OriginalNameByKey = draft.Originals,
        PrimaryKeys = draft.PrimaryKeys,
        MemberKeys = draft.MemberKeys,
    };

    private static int Depth(IMethodSymbol member)
    {
        var depth = 0;
        for (var ov = member.OverriddenMethod; ov != null; ov = ov.OverriddenMethod) depth++;
        return depth;
    }

    private static int Span(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;

    private static string Fqn(ISymbol symbol)
        => symbol.ContainingType?.ToDisplayString() ?? symbol.Name;

    private static string BaseReturnTypeText(
        MemberDesugarAnalysisContext context, IMethodSymbol member, ITypeSymbol type)
    {
        var loc = member.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc?.SourceTree is { } tree)
        {
            try
            {
                var model = context.ModelFor(tree);
                return type.ToMinimalDisplayString(model, loc.SourceSpan.Start);
            }
            catch
            {
                // 位置不可绑定（部分重解析）→ 退化为完全限定显示。
            }
        }
        return type.ToDisplayString();
    }

    /// <summary>确定性防撞命名：<c>{Name}_cs2jCovariant</c>；占用则追加 <c>_2</c>/<c>_3</c>…（设计裁决 1）。</summary>
    private static string ReserveName(
        INamedTypeSymbol? type, string memberName,
        Dictionary<string, HashSet<string>> reservations)
    {
        var typeKey = type?.ToDisplayString() ?? memberName;
        if (!reservations.TryGetValue(typeKey, out var reserved))
            reservations[typeKey] = reserved = new HashSet<string>(StringComparer.Ordinal);
        var baseName = memberName + "_cs2jCovariant";
        var name = baseName;
        var suffix = 2;
        while ((type != null && type.GetMembers(name).Any(m => !m.IsImplicitlyDeclared))
               || reserved.Contains(name))
        {
            name = $"{baseName}_{suffix}";
            suffix++;
        }
        reserved.Add(name);
        return name;
    }

    private static bool MatchesReflectionName(string text, string memberName)
        => string.Equals(text, memberName, StringComparison.Ordinal)
           || text.EndsWith("." + memberName, StringComparison.Ordinal);
}