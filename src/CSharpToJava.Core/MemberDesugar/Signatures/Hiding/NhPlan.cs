using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Hiding;

/// <summary>
/// C46 NH 隐藏 plan（设计 §4.3-2/§4.4 NH-1..NH-5）：一个隐藏成员一 plan（NH-2 为一 newslot 链一 plan）。
/// <list type="bullet">
/// <item>NH-1：实例化石隐藏（显式 <c>new</c> 或隐式 CS0108）→ 改名 <c>{Name}_cs2jNew</c>、去 <c>new</c> 令牌；
/// 属性/索引器/事件经 C11–C13 方法化为访问器方法对，按 <c>get_/set_</c>（<c>add_/remove_</c>）配对改名。</item>
/// <item>NH-2：<c>new virtual/abstract</c> 及隐式虚隐藏新虚槽 → 与全部派生 override 整组改名。</item>
/// <item>NH-3：纯 base 委托白名单内保留同名 public 桥接（<see cref="Bridged"/>，不改名/不改站点）。</item>
/// <item>NH-4：显式 <c>new</c> 的字段/静态方法改名。</item>
/// <item>NH-5：无 plan 认领的隐藏 <c>new</c> → <see cref="MemberDesugarOutcome.Infeasible"/> + CS2J5199（residual 对账）。</item>
/// </list>
/// </summary>
internal sealed class NhPlan : MemberDesugarPlan
{
    public required ISymbol Hidden { get; init; }
    public required HideFlavor Flavor { get; init; }

    /// <summary>NH-3 白名单内保留同名桥接（不改名、不改站点）。</summary>
    public bool Bridged { get; init; }

    /// <summary>各组件的声明主键集合（计数去重；每个被改名的声明只计一次）。</summary>
    public IReadOnlySet<string> PrimaryKeys { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>稳定键（声明/调用点）→ 新名。NH-1/NH-2 含访问器方法键；NH-4 为字段/静态方法键。</summary>
    public IReadOnlyDictionary<string, string> RenameByKey { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>稳定键 → 原名（<c>nameof</c> 字面量化目标，决策 15）。</summary>
    public IReadOnlyDictionary<string, string> OriginalNameByKey { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Hidden.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return memberName != null
               && (string.Equals(memberName, Hidden.Name, StringComparison.Ordinal)
                   || RenameByKey.Values.Contains(memberName));
    }

    public static string SiteKey(ISymbol symbol) => ExplicitInterface.SiPlan.SiteKey(symbol);

    public static string? KeyMemberName(string siteKey)
        => ExplicitInterface.SiPlan.KeyMemberName(siteKey);

    public static string BuildId(ISymbol hidden)
    {
        var span = hidden.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD-hide:{hidden.ContainingType?.ToDisplayString()}::{hidden.Name}@{span}";
    }
}

/// <summary>
/// C46 NH plan 构建器（设计 §4.4 NH-1..NH-5，裁决 5/7）。
/// <list type="bullet">
/// <item>NH-3：纯 base 委托白名单桥接 / 白名单外 CS2J5192。</item>
/// <item>NH-4：显式 <c>new</c> 字段/静态方法改名。</item>
/// <item>NH-1：实例非虚隐藏（显式/隐式）改名；属性/索引器/事件按访问器方法对配对改名。</item>
/// <item>NH-2：<c>new virtual/abstract</c> 与隐式虚隐藏的 newslot 链整组改名。</item>
/// <item>NH-5：以上均未认领且带 <c>new</c> 令牌的隐藏成员 → CS2J5199（residual 对账）。</item>
/// </list>
/// 一切判定以符号为准，零接收者文本推测；站点按 <c>SemanticModel</c> 绑定符号纳入 TouchedFiles 并过闸门。
/// </summary>
internal static class NhPlanBuilder
{
    private sealed class Draft
    {
        public required HiddenMember Hidden { get; init; }
        public required string ContainingTypeFqn { get; init; }
        public HashSet<string> PrimaryKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Renames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> OriginalNames { get; } = new(StringComparer.Ordinal);
        /// <summary>站点分类匹配键（声明键 + 所属属性/事件符号键，分析期站点仍绑到后者）。</summary>
        public HashSet<string> MatchKeys { get; } = new(StringComparer.Ordinal);
        public MemberDesugarOutcome Outcome { get; set; } = MemberDesugarOutcome.Planned;
        public bool Bridged { get; set; }
        public MemberDesugarDiagnosticCode? FailCode { get; set; }
        public string? FailReason { get; set; }
        public SortedSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static void Build(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options.Signatures;
        var hiding = context.SignatureAnalysis!.Hiding;
        var reservations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var drafts = new List<Draft>();
        var claimed = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var groupsById = hiding.Groups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);

        var ordered = hiding.HiddenMembers
            .OrderBy(h => h.Member.ContainingType?.ToDisplayString(), StringComparer.Ordinal)
            .ThenBy(h => h.Member.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var hidden in ordered)
        {
            var draft = new Draft
            {
                Hidden = hidden,
                ContainingTypeFqn = hidden.Member.ContainingType?.ToDisplayString() ?? hidden.Member.Name,
            };
            draft.Files.Add(hidden.DeclaringFile);

            if (options.IsSkipped(hidden.Member))
            {
                draft.Outcome = MemberDesugarOutcome.UserSkipped;
                claimed.Add(hidden.Member);
                drafts.Add(draft);
                continue;
            }

            // NH-3：纯 base 委托（桥接例外）。
            if (hidden.IsPureBaseDelegation)
            {
                if (IsBridgeWhitelisted(hidden))
                {
                    draft.Bridged = true; // 保留同名 public 桥接：不改名、不改站点。
                }
                else
                {
                    draft.Outcome = MemberDesugarOutcome.Infeasible;
                    draft.FailCode = MemberDesugarDiagnosticCode.SignatureNameReservationConflict;
                    draft.FailReason = SignatureDiagnostics.HidingBridgeNotWhitelisted(
                        draft.ContainingTypeFqn, hidden.Member.Name);
                }
                claimed.Add(hidden.Member);
                drafts.Add(draft);
                continue;
            }

            // NH-4：显式 new 的字段 / 静态方法（静态属性等不在 NH-4 范围，落到 NH-5 对账）。
            if (hidden.Member.IsStatic)
            {
                if (hidden.HasNewToken
                    && hidden.Member is IFieldSymbol or IMethodSymbol
                    && (hidden.IsField || hidden.Member.IsStatic))
                {
                    var decl = hidden.Member;
                    AddRename(draft, decl, decl.Name, reservations);
                    claimed.Add(hidden.Member);
                    drafts.Add(draft);
                }
                continue;
            }

            // NH-2：newslot 链整组改名（newslot 声明 + 全部派生 override）。
            if (hidden.Flavor == HideFlavor.NewSlot)
            {
                var members = groupsById.TryGetValue(hidden.GroupId, out var group)
                    ? group.Members : new[] { hidden.Member };
                foreach (var member in members)
                {
                    foreach (var (decl, original) in DeclarationUnits(member))
                    {
                        AddRename(draft, decl, original, reservations);
                        foreach (var loc in decl.Locations.Where(l => l.IsInSource))
                            draft.Files.Add(loc.SourceTree!.FilePath);
                    }
                    draft.MatchKeys.Add(NhPlan.SiteKey(member));
                }
                claimed.Add(hidden.Member);
                drafts.Add(draft);
                continue;
            }

            // NH-1：实例化石隐藏改名（显式 new / 隐式 CS0108）。
            foreach (var (accessor, original) in DeclarationUnits(hidden.Member))
                AddRename(draft, accessor, original, reservations);
            draft.MatchKeys.Add(NhPlan.SiteKey(hidden.Member));
            claimed.Add(hidden.Member);
            drafts.Add(draft);
        }

        // NH-5：residual 对账——带 new 令牌但无任何 plan 认领的隐藏成员 → CS2J5199（原子不动）。
        foreach (var hidden in ordered)
        {
            if (!hidden.HasNewToken || claimed.Contains(hidden.Member)) continue;
            drafts.Add(new Draft
            {
                Hidden = hidden,
                ContainingTypeFqn = hidden.Member.ContainingType?.ToDisplayString() ?? hidden.Member.Name,
                Outcome = MemberDesugarOutcome.Infeasible,
                FailCode = MemberDesugarDiagnosticCode.SignatureResidual,
                FailReason = SignatureDiagnostics.Residual(hidden.DeclaringFile, 1),
                Files = { hidden.DeclaringFile },
            });
        }

        ClassifySites(context, drafts);

        foreach (var draft in drafts)
            builder.Add(ToPlan(draft));
    }

    // ---- 改名单元（成员 → 访问器方法对展开）----

    /// <summary>把一个成员展开为「将被 C11–C13 方法化」的声明单元：普通方法/字段为其自身；
    /// 属性/索引器为 get/set 方法对，事件为 add/remove 方法对。第二项为 nameof 可见原名。</summary>
    private static IEnumerable<(ISymbol Decl, string OriginalName)> DeclarationUnits(ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol m:
                yield return (m, m.Name);
                break;
            case IFieldSymbol f:
                yield return (f, f.Name);
                break;
            case IPropertySymbol p:
                if (p.GetMethod != null) yield return (p.GetMethod, p.Name);
                if (p.SetMethod != null) yield return (p.SetMethod, p.Name);
                break;
            case IEventSymbol e:
                if (e.AddMethod != null) yield return (e.AddMethod, e.Name);
                if (e.RemoveMethod != null) yield return (e.RemoveMethod, e.Name);
                break;
        }
    }

    private static void AddRename(
        Draft draft, ISymbol decl, string originalName,
        Dictionary<string, HashSet<string>> reservations)
    {
        var type = decl.ContainingType;
        if (type == null) return;
        var newName = ReserveName(type, decl.Name, reservations);
        var key = NhPlan.SiteKey(decl);
        draft.PrimaryKeys.Add(key);
        draft.Renames[key] = newName;
        draft.OriginalNames[key] = originalName;
        draft.MatchKeys.Add(key);
        foreach (var loc in decl.Locations.Where(l => l.IsInSource))
            draft.Files.Add(loc.SourceTree!.FilePath);
    }

    // ---- 站点分类（NH-1/NH-2/NH-5 全站点按符号绑定改名 + 5194 闸门 + 5193 反射）----

    private static void ClassifySites(MemberDesugarAnalysisContext context, List<Draft> drafts)
    {
        var matchByKey = new Dictionary<string, List<Draft>>(StringComparer.Ordinal);
        var byOriginalName = new Dictionary<string, List<Draft>>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (draft.Outcome != MemberDesugarOutcome.Planned) continue;
            foreach (var key in draft.MatchKeys)
            {
                if (!matchByKey.TryGetValue(key, out var list))
                    matchByKey[key] = list = new List<Draft>();
                list.Add(draft);
            }
            foreach (var original in draft.OriginalNames.Values)
            {
                if (!byOriginalName.TryGetValue(original, out var list))
                    byOriginalName[original] = list = new List<Draft>();
                if (!list.Contains(draft)) list.Add(draft);
            }
        }
        if (matchByKey.Count == 0) return;

        // 性能：名字快筛（同 SiPlanBuilder）：命中键的成员名 == 站点绑定符号 MetadataName
        // == 源码标识符文本；先按文本过滤可省掉绝大多数 GetSymbolInfo。
        var siteNames = new HashSet<string>(StringComparer.Ordinal);
        var filterUsable = true;
        foreach (var key in matchByKey.Keys)
        {
            var keyName = NhPlan.KeyMemberName(key);
            if (keyName == null) { filterUsable = false; break; }
            siteNames.Add(keyName);
        }

        // 逐树并行采集命中事件，随后按树序回放（Gate 首现优先，顺序必须与串行一致）。
        var models = context.Models;
        var partMatches = new List<(Draft Draft, string FilePath, string Name, bool Gate, bool Dynamic)>[
            models.Count];
        System.Threading.Tasks.Parallel.For(0, models.Count, index =>
        {
            var model = models[index];
            var filePath = model.SyntaxTree.FilePath;
            var matches = new List<(Draft, string, string, bool, bool)>();
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
                                foreach (var plan in plans)
                                    matches.Add((plan, filePath, string.Empty, false, false));
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

                var sourceName = expr switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    _ => null,
                };
                if (filterUsable && sourceName != null && !siteNames.Contains(sourceName)) continue;

                var bound = SignatureSyntaxFacts.BoundSymbol(model, expr);
                if (bound != null)
                {
                    if (!matchByKey.TryGetValue(NhPlan.SiteKey(bound), out var plans)) continue;
                    var undecidable = plans.Count > 0
                        && SignatureSyntaxFacts.ClassifyCallSite(expr, model)
                            == SignatureCallSiteClass.Undecidable;
                    var dynamic = undecidable && SignatureSyntaxFacts.IsDynamic(expr, model);
                    foreach (var plan in plans)
                        matches.Add((plan, filePath, bound.Name, undecidable, dynamic));
                    continue;
                }

                // dynamic 接收者无绑定符号：按成员名保守过闸门（不可判定 → CS2J5194 原子跳过）。
                if (expr is MemberAccessExpressionSyntax dynExpr
                    && SignatureSyntaxFacts.IsDynamic(dynExpr, model)
                    && byOriginalName.TryGetValue(dynExpr.Name.Identifier.ValueText, out var dynPlans))
                {
                    foreach (var plan in dynPlans)
                        matches.Add((plan, filePath, dynExpr.Name.Identifier.ValueText, true, true));
                }
            }

            partMatches[index] = matches;
        });

        foreach (var matches in partMatches)
        {
            foreach (var (draft, filePath, name, gate, dynamic) in matches)
            {
                draft.Files.Add(filePath);
                if (gate && draft.Outcome == MemberDesugarOutcome.Planned)
                    Gate(draft, name, dynamic);
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

    // ---- NH-3 白名单（裁决 5）：基类型在编译内 且（槽位 sealed 或基链无覆写）----

    private static bool IsBridgeWhitelisted(HiddenMember hidden)
    {
        if (hidden.BaseMember == null) return false;
        var baseType = hidden.BaseMember.ContainingType;
        if (baseType == null || !baseType.Locations.Any(l => l.IsInSource)) return false;
        if (IsSealed(hidden.BaseMember)) return true;
        if (IsOverride(hidden.BaseMember)) return false;

        for (var t = hidden.Member.ContainingType?.BaseType; t != null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, baseType)) break;
            foreach (var m in t.GetMembers(hidden.BaseMember.Name))
                if (IsOverride(m)) return false;
        }
        return true;
    }

    private static bool IsSealed(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.IsSealed,
        IPropertySymbol p => p.IsSealed,
        IEventSymbol e => e.IsSealed,
        _ => false,
    };

    private static bool IsOverride(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.IsOverride,
        IPropertySymbol p => p.IsOverride,
        IEventSymbol e => e.IsOverride,
        _ => false,
    };

    // ---- 小工具 ----

    private static NhPlan ToPlan(Draft draft) => new()
    {
        Id = NhPlan.BuildId(draft.Hidden.Member),
        Kind = MemberDesugarKind.Signature,
        Symbol = draft.Hidden.Member,
        Outcome = draft.Outcome,
        TouchedFiles = draft.Files.ToList(),
        FailCode = draft.FailCode,
        FailReason = draft.FailReason,
        Hidden = draft.Hidden.Member,
        Flavor = draft.Hidden.Flavor,
        Bridged = draft.Bridged,
        PrimaryKeys = draft.PrimaryKeys,
        RenameByKey = draft.Renames,
        OriginalNameByKey = draft.OriginalNames,
    };

    /// <summary>确定性防撞命名：<c>{Name}_cs2jNew</c>；占用则追加 <c>_2</c>/<c>_3</c>…（设计裁决 1）。</summary>
    private static string ReserveName(
        INamedTypeSymbol type, string memberName, Dictionary<string, HashSet<string>> reservations)
    {
        var typeKey = type.ToDisplayString();
        if (!reservations.TryGetValue(typeKey, out var reserved))
            reservations[typeKey] = reserved = new HashSet<string>(StringComparer.Ordinal);
        var baseName = memberName + "_cs2jNew";
        var name = baseName;
        var suffix = 2;
        while (type.GetMembers(name).Any(m => !m.IsImplicitlyDeclared) || reserved.Contains(name))
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