using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Hiding;

/// <summary>
/// C46 NH 隐藏 plan（设计 §4.3-2/§4.4 NH-3/NH-4）：一个隐藏成员一 plan。
/// <list type="bullet">
/// <item>NH-3：纯 base 委托且落在白名单内 → 保留同名 public 桥接（<see cref="Bridged"/>，不改名/不改站点）；
/// 白名单外 → <see cref="MemberDesugarOutcome.Infeasible"/> + CS2J5192（成员原样保留）。</item>
/// <item>NH-4：显式 <c>new</c> 的字段/静态方法 → 改名 <c>{Name}_cs2jNew</c>、去 <c>new</c> 令牌，
/// 全站点按符号绑定改名。</item>
/// </list>
/// </summary>
internal sealed class NhPlan : MemberDesugarPlan
{
    public required ISymbol Hidden { get; init; }
    public required HideFlavor Flavor { get; init; }

    /// <summary>NH-3 白名单内保留同名桥接（不改名、不改站点）。</summary>
    public bool Bridged { get; init; }

    /// <summary>NH-4 声明主键（计数去重；同 plan 多声明只计一次）。</summary>
    public string? PrimaryKey { get; init; }

    /// <summary>稳定键 → 新名（方法声明/字段声明/站点改名查表）。</summary>
    public IReadOnlyDictionary<string, string> RenameByKey { get; init; }
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

    public static string BuildId(ISymbol hidden)
    {
        var span = hidden.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD-hide:{hidden.ContainingType?.ToDisplayString()}::{hidden.Name}@{span}";
    }
}

/// <summary>
/// C46 NH plan 构建器（设计 §4.4 NH-3/NH-4，裁决 5）：从 M1 <see cref="HideGroupResolver"/> 取隐藏成员，
/// 只处理 M3 范围内的形态——纯 base 委托白名单桥接（NH-3）与 <c>new</c> 字段/静态方法改名（NH-4）。
/// 实例 <c>new</c> 方法（NH-1）与 <c>new virtual/abstract</c>（NH-2）留待 M4，不在此产 plan。
/// 一切判定以符号为准，零接收者文本推测。
/// </summary>
internal static class NhPlanBuilder
{
    private sealed class Draft
    {
        public required HiddenMember Hidden { get; init; }
        public required string ContainingTypeFqn { get; init; }
        public string? PrimaryKey { get; set; }
        public Dictionary<string, string> Renames { get; } = new(StringComparer.Ordinal);
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

        foreach (var hidden in hiding.HiddenMembers
                     .OrderBy(h => h.Member.ContainingType?.ToDisplayString(), StringComparer.Ordinal)
                     .ThenBy(h => h.Member.Name, StringComparer.Ordinal))
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
                drafts.Add(draft);
                continue;
            }

            // NH-3：纯 base 委托。
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
                drafts.Add(draft);
                continue;
            }

            // NH-4：显式 new 的字段 / 静态方法（实例 new 方法 NH-1、new virtual NH-2 归 M4）。
            if (!hidden.HasNewToken) continue;
            if (hidden.Member is not (IFieldSymbol or IMethodSymbol)) continue;
            if (!hidden.IsField && !hidden.Member.IsStatic) continue;

            var type = hidden.Member.ContainingType;
            if (type == null) continue;
            var newName = ReserveName(type, hidden.Member.Name, reservations);
            var key = NhPlan.SiteKey(hidden.Member);
            draft.PrimaryKey = key;
            draft.Renames[key] = newName;
            drafts.Add(draft);
        }

        ClassifySites(context, drafts);

        foreach (var draft in drafts)
            builder.Add(ToPlan(draft));
    }

    // ---- 站点分类（NH-4 全站点按符号绑定改名）----

    private static void ClassifySites(MemberDesugarAnalysisContext context, List<Draft> drafts)
    {
        var byKey = new Dictionary<string, List<Draft>>(StringComparer.Ordinal);
        foreach (var draft in drafts)
            foreach (var key in draft.Renames.Keys)
            {
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<Draft>();
                list.Add(draft);
            }
        if (byKey.Count == 0) return;

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
            {
                ExpressionSyntax? expr = node switch
                {
                    MemberAccessExpressionSyntax ma => ma,
                    IdentifierNameSyntax id when id.Parent is not (MemberAccessExpressionSyntax
                        or MemberBindingExpressionSyntax) => id,
                    _ => null,
                };
                if (expr == null) continue;
                if (SignatureSyntaxFacts.BoundSymbol(model, expr) is not { } bound) continue;
                if (!byKey.TryGetValue(NhPlan.SiteKey(bound), out var plans)) continue;
                foreach (var plan in plans)
                    if (plan.Outcome == MemberDesugarOutcome.Planned) plan.Files.Add(filePath);
            }
        }
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
        PrimaryKey = draft.PrimaryKey,
        RenameByKey = draft.Renames,
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
}