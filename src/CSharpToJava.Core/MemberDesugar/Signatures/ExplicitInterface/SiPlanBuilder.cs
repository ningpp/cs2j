using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.ExplicitInterface;

/// <summary>
/// C46 SI plan 构建器（设计 §4.3-2/§4.4 SI-1..SI-5）：从 M1 <see cref="InterfaceMappingIndex"/>
/// 取显式实现（M2 方法面），按「含入类型 :: 规范槽名(参数签名)」合组：
/// <list type="bullet">
/// <item>SI-1 无碰撞：去限定名 + 提 public（保留规范槽名）。</item>
/// <item>SI-2 让位改名：类自有同槽成员改名 <c>{Name}_cs2jClass</c>（按类型预留表防撞，二撞 <c>_2</c>）。</item>
/// <item>SI-3 同名双接口：体等价合并一 public 方法；分歧 CS2J5191 整组不动。</item>
/// <item>SI-5 调用点：全树站点按 <see cref="SiPlan.SiteKey"/> 符号绑定分类，绑定让位成员者纳入 TouchedFiles；
/// 表达式树/dynamic 不可判定 → CS2J5194 原子跳过；反射字符串命中 → CS2J5193（站点保留，非阻断）。</item>
/// </list>
/// 一切判定以 <c>SemanticModel.GetSymbolInfo</c> 绑定符号为准，零接收者文本/形态推测。
/// </summary>
internal static class SiPlanBuilder
{
    /// <summary>合组过程的可变草稿（TouchedFiles 需站点扫描后定稿）。</summary>
    private sealed class Draft
    {
        public List<ISymbol> Implementations { get; } = new();
        public List<ISymbol> Slots { get; } = new();
        public string ContainingTypeFqn { get; set; } = string.Empty;
        public string PromotedName { get; set; } = string.Empty;
        public string ParameterSignature { get; set; } = string.Empty;
        public string RetainedKey { get; set; } = string.Empty;
        public bool Merged { get; set; }
        public ISymbol? Displaced { get; set; }
        public string? DisplacedName { get; set; }
        public string? DisplacedOriginalName { get; set; }
        public MemberDesugarOutcome Outcome { get; set; } = MemberDesugarOutcome.Planned;
        public MemberDesugarDiagnosticCode? FailCode { get; set; }
        public string? FailReason { get; set; }
        public SortedSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static void Build(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options.Signatures;
        var mapping = context.SignatureAnalysis!.InterfaceMapping;
        var reservations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var drafts = new List<Draft>();

        foreach (var group in mapping.ExplicitImplementations
                     .Where(e => e.Kind == SignatureKind.Method)
                     .GroupBy(GroupKey, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            drafts.Add(CreateDraft(group.ToList(), options, reservations));
        }

        // 站点分类：只对「绑定到让位类成员」的站点（即需要改名的站点）做闸门/纳入 TouchedFiles。
        var displacedByKey = new Dictionary<string, Draft>(StringComparer.Ordinal);
        foreach (var draft in drafts)
            if (draft.Displaced != null)
                displacedByKey[SiPlan.SiteKey(draft.Displaced)] = draft;
        ClassifySites(context, displacedByKey);

        foreach (var draft in drafts)
            builder.Add(ToPlan(draft));
    }

    // ---- 合组与声明侧判定 ----

    private static Draft CreateDraft(
        IReadOnlyList<ExplicitImplementationEntry> entries,
        SignatureOptions options,
        Dictionary<string, HashSet<string>> reservations)
    {
        var draft = new Draft
        {
            ContainingTypeFqn = entries[0].ContainingTypeFqn,
            PromotedName = entries[0].Slot.Name,
        };

        // 去重并按源 span 排序（确定性；保留实现取首）。
        var pairs = entries
            .Select(e => (e.Implementation, e.Slot,
                Span: e.Implementation.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue))
            .GroupBy(p => SiPlan.SiteKey(p.Implementation), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(p => p.Span)
            .ThenBy(p => SiPlan.SiteKey(p.Implementation), StringComparer.Ordinal)
            .ToList();
        foreach (var (impl, slot, _) in pairs)
        {
            draft.Implementations.Add(impl);
            draft.Slots.Add(slot);
        }
        foreach (var loc in entries.SelectMany(e => e.Implementation.Locations).Where(l => l.IsInSource))
            draft.Files.Add(loc.SourceTree!.FilePath);
        foreach (var e in entries)
            if (e.DeclaringFile.Length > 0) draft.Files.Add(e.DeclaringFile);

        var retained = draft.Implementations[0];
        draft.RetainedKey = SiPlan.SiteKey(retained);
        draft.ParameterSignature = ParameterSignature(retained);

        if (options.IsSkipped(retained))
        {
            draft.Outcome = MemberDesugarOutcome.UserSkipped;
            return draft;
        }

        // 静态抽象/虚接口成员：本规则不领，归 C36/CS2J0585（SI-6，决策 13）。
        if (draft.Implementations.Any(i => i.IsStatic))
        {
            draft.Outcome = MemberDesugarOutcome.Infeasible;
            draft.FailCode = MemberDesugarDiagnosticCode.SignatureStaticAbstractInfo;
            draft.FailReason = SignatureDiagnostics.StaticAbstractInfo(draft.PromotedName);
            return draft;
        }

        // SI-3：同名多接口体等价合并 / 分歧 CS2J5191。
        if (draft.Implementations.Count > 1)
        {
            if (!draft.Implementations.Skip(1).All(i =>
                    SignatureSyntaxFacts.AreBodiesEquivalent(retained, i)))
            {
                draft.Outcome = MemberDesugarOutcome.Infeasible;
                draft.FailCode = MemberDesugarDiagnosticCode.SignatureDivergentInterfaceBodies;
                var ifaceA = draft.Slots[0].ContainingType?.Name ?? "?";
                var ifaceB = draft.Slots[1].ContainingType?.Name ?? "?";
                draft.FailReason = SignatureDiagnostics.DivergentInterfaceBodies(
                    draft.ContainingTypeFqn, draft.PromotedName, ifaceA, ifaceB);
                return draft;
            }
            draft.Merged = true;
        }

        // SI-2：类自有同槽成员让位改名。
        var type = retained.ContainingType;
        if (type != null)
        {
            var candidates = type.GetMembers(draft.PromotedName)
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind != MethodKind.ExplicitInterfaceImplementation
                            && m.Arity == ((IMethodSymbol)retained).Arity
                            && ParameterSignature(m) == draft.ParameterSignature)
                .ToList();
            if (candidates.Count > 1)
            {
                // 同槽多候选、无法确定让位者 → CS2J5192 跳过（设计决策 2/SI-2）。
                draft.Outcome = MemberDesugarOutcome.Infeasible;
                draft.FailCode = MemberDesugarDiagnosticCode.SignatureNameReservationConflict;
                draft.FailReason = SignatureDiagnostics.NameReservationConflict(
                    draft.ContainingTypeFqn, draft.PromotedName);
                return draft;
            }
            if (candidates.Count == 1)
            {
                var displaced = candidates[0];
                // 让位成员若本身是某接口槽的隐式实现，改名会把它承载的槽改绑到提升方法；
                // 体分歧即真实语义差 → CS2J5191 保守跳过（决策 2）。
                if (IsImplicitInterfaceImplementation(displaced)
                    && !SignatureSyntaxFacts.AreBodiesEquivalent(retained, displaced))
                {
                    draft.Outcome = MemberDesugarOutcome.Infeasible;
                    draft.FailCode = MemberDesugarDiagnosticCode.SignatureDivergentInterfaceBodies;
                    draft.FailReason = SignatureDiagnostics.DivergentInterfaceBodies(
                        draft.ContainingTypeFqn, draft.PromotedName,
                        displaced.ContainingType?.Name ?? "?", displaced.ContainingType?.Name ?? "?");
                    return draft;
                }
                draft.Displaced = displaced;
                draft.DisplacedOriginalName = displaced.Name;
                draft.DisplacedName = ReserveName(type, draft.PromotedName, reservations);
                foreach (var loc in displaced.Locations.Where(l => l.IsInSource))
                    draft.Files.Add(loc.SourceTree!.FilePath);
            }
        }

        return draft;
    }

    // ---- 站点分类（SI-5/SI-6）----

    private static void ClassifySites(
        MemberDesugarAnalysisContext context, Dictionary<string, Draft> displacedByKey)
    {
        if (displacedByKey.Count == 0) return;
        var reflectionByName = new Dictionary<string, Draft>(StringComparer.Ordinal);
        foreach (var draft in displacedByKey.Values)
            if (draft.DisplacedOriginalName != null)
                reflectionByName[draft.DisplacedOriginalName] = draft;

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
                        foreach (var (name, draft) in reflectionByName)
                            if (draft.Outcome == MemberDesugarOutcome.Planned && MatchesReflectionName(text, name))
                                draft.Files.Add(filePath);
                    }
                    continue;
                }

                ExpressionSyntax? expr = node switch
                {
                    MemberAccessExpressionSyntax ma => ma,
                    // 类型内未限定引用；成员访问的 Name 子节点由 member access 通道处理。
                    IdentifierNameSyntax id when id.Parent is not (MemberAccessExpressionSyntax
                        or MemberBindingExpressionSyntax) => id,
                    _ => null,
                };
                if (expr == null) continue;
                if (SignatureSyntaxFacts.BoundSymbol(model, expr) is not IMethodSymbol method) continue;
                if (!displacedByKey.TryGetValue(SiPlan.SiteKey(method), out var plan)) continue;
                if (plan.Outcome != MemberDesugarOutcome.Planned) continue;

                plan.Files.Add(filePath);
                if (SignatureSyntaxFacts.ClassifyCallSite(expr, model)
                    == SignatureCallSiteClass.Undecidable)
                {
                    plan.Outcome = MemberDesugarOutcome.Infeasible;
                    plan.FailCode = MemberDesugarDiagnosticCode.SignatureExpressionTreeOrDynamic;
                    plan.FailReason = SignatureDiagnostics.ExpressionTreeOrDynamic(
                        method.Name, SignatureSyntaxFacts.IsDynamic(expr, model));
                }
            }
        }
    }

    // ---- 小工具 ----

    private static SiPlan ToPlan(Draft draft) => new()
    {
        Id = SiPlan.BuildId(draft.Implementations[0], draft.ContainingTypeFqn,
            draft.PromotedName, draft.ParameterSignature),
        Kind = MemberDesugarKind.Signature,
        Symbol = draft.Implementations[0],
        Outcome = draft.Outcome,
        TouchedFiles = draft.Files.ToList(),
        FailCode = draft.FailCode,
        FailReason = draft.FailReason,
        Implementations = draft.Implementations,
        Slots = draft.Slots,
        ContainingTypeFqn = draft.ContainingTypeFqn,
        PromotedName = draft.PromotedName,
        RetainedKey = draft.RetainedKey,
        ExplicitKind = SignatureKind.Method,
        Merged = draft.Merged,
        DisplacedMember = draft.Displaced,
        DisplacedKey = draft.Displaced != null ? SiPlan.SiteKey(draft.Displaced) : null,
        DisplacedName = draft.DisplacedName,
        DisplacedOriginalName = draft.DisplacedOriginalName,
        ImplementationKeys = draft.Implementations.Select(SiPlan.SiteKey).ToList(),
    };

    private static string GroupKey(ExplicitImplementationEntry entry)
        => $"{entry.ContainingTypeFqn}::{entry.Slot.Name}({ParameterSignature(entry.Implementation)})";

    private static string ParameterSignature(ISymbol symbol)
        => symbol is IMethodSymbol m
            ? string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString()))
            : string.Empty;

    /// <summary>确定性防撞命名：<c>{Name}_cs2jClass</c>；占用则追加 <c>_2</c>/<c>_3</c>…（设计裁决 1）。</summary>
    private static string ReserveName(
        INamedTypeSymbol type, string memberName, Dictionary<string, HashSet<string>> reservations)
    {
        var typeKey = type.ToDisplayString();
        if (!reservations.TryGetValue(typeKey, out var reserved))
            reservations[typeKey] = reserved = new HashSet<string>(StringComparer.Ordinal);
        var baseName = memberName + "_cs2jClass";
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

    /// <summary>让位候选是否是某接口槽的隐式实现（用于分歧保守闸门）。</summary>
    private static bool IsImplicitInterfaceImplementation(IMethodSymbol candidate)
    {
        var type = candidate.ContainingType;
        if (type == null) return false;
        foreach (var iface in type.AllInterfaces)
            foreach (var member in iface.GetMembers())
                if (member is IMethodSymbol slot
                    && SymbolEqualityComparer.Default.Equals(
                        type.FindImplementationForInterfaceMember(slot), candidate))
                    return true;
        return false;
    }

    private static bool MatchesReflectionName(string text, string memberName)
        => string.Equals(text, memberName, StringComparison.Ordinal)
           || text.EndsWith("." + memberName, StringComparison.Ordinal);
}