using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Events;

/// <summary>
/// C13 event 规则（Kind=Event, Order=30）。三步：
/// 1) 声明收集（EventFieldDeclaration 变量级 + EventDeclaration）+ 声明级闸门；
/// 2) 站点扫描（+=/-= 类内 vs 类外、dynamic、nameof、绑定不可解析）+ 层次联动文件；
/// 3) EventPlan 产出（设计 §4.3 EV-1..EV-17）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Event, 30)]
internal sealed class EventRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Event;
    public int Order => 30;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options;

        // ---- 阶段 1：声明收集 + 声明级闸门 ----
        var candidates = new Dictionary<IEventSymbol, MemberDeclarationSyntax>(SymbolEqualityComparer.Default);
        var failures = new Dictionary<IEventSymbol, (string DeclName, MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        var userSkipped = new Dictionary<IEventSymbol, MemberDeclarationSyntax>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                if (decl is EventFieldDeclarationSyntax fieldDecl)
                {
                    foreach (var variable in fieldDecl.Declaration.Variables)
                    {
                        if (model.GetDeclaredSymbol(variable) is not IEventSymbol evt) continue;
                        if (!evt.IsDefinition) evt = (IEventSymbol)evt.OriginalDefinition;
                        if (candidates.ContainsKey(evt) || failures.ContainsKey(evt) || userSkipped.ContainsKey(evt))
                            continue;
                        Register(evt, fieldDecl, model, options, candidates, failures, userSkipped);
                    }
                }
                else
                {
                    if (model.GetDeclaredSymbol(decl) is not IEventSymbol evt) continue;
                    if (!evt.IsDefinition) evt = (IEventSymbol)evt.OriginalDefinition;
                    if (candidates.ContainsKey(evt) || failures.ContainsKey(evt) || userSkipped.ContainsKey(evt))
                        continue;
                    Register(evt, decl, model, options, candidates, failures, userSkipped);
                }
            }
        }

        // ---- 阶段 2：站点扫描 ----
        var siteFiles = new Dictionary<IEventSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var siteFailures = new Dictionary<IEventSymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        // 站点名快筛用声明标识符（显式接口实现的符号 Name 是 "IFoo.X" 形态）。
        var candidateNames = candidates.Values
            .Select(DeclNameOf)
            .Where(n => n != null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax assign
                        when assign.IsKind(SyntaxKind.AddAssignmentExpression)
                            || assign.IsKind(SyntaxKind.SubtractAssignmentExpression):
                        HandleCompoundSite(assign, model, filePath, candidateNames, candidates,
                            siteFiles, siteFailures);
                        break;

                    case InvocationExpressionSyntax invocation
                        when invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
                            && invocation.ArgumentList.Arguments.Count == 1:
                        HandleNameofSite(invocation.ArgumentList.Arguments[0].Expression, model,
                            filePath, candidateNames, candidates, siteFiles);
                        break;
                }
            }
        }

        // ---- 阶段 3：plan 产出 ----
        foreach (var (evt, decl) in candidates)
        {
            var declName = DeclNameOf(decl) ?? evt.Name;
            var touched = CollectTouchedFiles(evt, siteFiles, candidates.Keys);

            if (siteFailures.TryGetValue(evt, out var siteFail))
            {
                builder.Add(MakePlan(evt, decl, declName, MemberDesugarOutcome.Infeasible,
                    touched, siteFail.Code, siteFail.Reason, options));
                continue;
            }

            builder.Add(MakePlan(evt, decl, declName, MemberDesugarOutcome.Planned,
                touched, null, null, options));
        }

        foreach (var (evt, fail) in failures)
        {
            var decl = candidates.TryGetValue(evt, out var d) ? d
                : userSkipped.TryGetValue(evt, out var s) ? s : null;
            builder.Add(MakePlan(evt, decl, fail.DeclName, MemberDesugarOutcome.Infeasible,
                DeclarationFiles(evt), fail.Code, fail.Reason, options));
        }

        foreach (var (evt, decl) in userSkipped)
        {
            builder.Add(MakePlan(evt, decl, DeclNameOf(decl) ?? evt.Name,
                MemberDesugarOutcome.UserSkipped, DeclarationFiles(evt), null, null, options));
        }
    }

    // ---- 声明注册 + 声明级闸门（partial/WinRT/静态抽象接口/readonly struct → 保留名 → 特性路由）----

    private static void Register(
        IEventSymbol evt, MemberDeclarationSyntax decl, SemanticModel model,
        MemberDesugarOptions options,
        Dictionary<IEventSymbol, MemberDeclarationSyntax> candidates,
        Dictionary<IEventSymbol, (string, MemberDesugarDiagnosticCode, string)> failures,
        Dictionary<IEventSymbol, MemberDeclarationSyntax> userSkipped)
    {
        var declName = DeclNameOf(decl) ?? evt.Name;

        if (MatchesSet(options.SkipQualifiedNames, evt))
        {
            userSkipped[evt] = decl;
            return;
        }

        var gate = DeclarationGate(evt, decl, model);
        if (gate != null)
        {
            failures[evt] = (declName, gate.Value.Code, gate.Value.Reason);
            return;
        }
        candidates[evt] = decl;
    }

    private static (MemberDesugarDiagnosticCode Code, string Reason)? DeclarationGate(
        IEventSymbol evt, MemberDeclarationSyntax decl, SemanticModel model)
    {
        if (EventFacts.IsPartialEvent(evt, decl))
            return (MemberDesugarDiagnosticCode.EventUnsupportedDeclaration,
                EventDiagnostics.Unsupported(evt.Name, "partial events are not supported"));
        // WinRT 事件只存在于 .winmd 元数据（C# 源无法声明），本规则的候选集为源内声明，
        // 天然不可达，无需属性闸门（设计 D20 的文档化依据）。
        if (evt.IsStatic && evt.ContainingType?.TypeKind == TypeKind.Interface
            && (evt.IsAbstract || evt.IsVirtual))
            return (MemberDesugarDiagnosticCode.EventUnsupportedDeclaration,
                EventDiagnostics.Unsupported(evt.Name, "static abstract/virtual interface events are not supported"));
        if (evt.ContainingType is { IsReadOnly: true, TypeKind: TypeKind.Struct })
            return (MemberDesugarDiagnosticCode.EventUnsupportedDeclaration,
                EventDiagnostics.Unsupported(evt.Name, "readonly struct events are not supported"));
        if (string.Equals(DeclNameOf(decl) ?? evt.Name, "value", StringComparison.Ordinal))
            return (MemberDesugarDiagnosticCode.EventNameCollision,
                EventDiagnostics.NameCollision(evt.Name));

        var fieldGenerated = NeedsField(evt, decl);
        foreach (var list in decl.AttributeLists)
        {
            if (EventFacts.RouteAttributeList(list, model, fieldGenerated)
                == EventFacts.EventRoute.Unroutable)
            {
                var attrName = list.Attributes.FirstOrDefault()?.Name.ToString() ?? "<attribute>";
                return (MemberDesugarDiagnosticCode.EventAttributeUnroutable,
                    EventDiagnostics.AttributeUnroutable(evt.Name, attrName));
            }
        }
        return null;
    }

    private static bool NeedsField(IEventSymbol evt, MemberDeclarationSyntax decl)
        => decl is EventFieldDeclarationSyntax
            && evt.ContainingType?.TypeKind != TypeKind.Interface
            && !evt.IsAbstract
            && !decl.Modifiers.Any(SyntaxKind.AbstractKeyword);

    // ---- 站点处理 ----

    private static void HandleCompoundSite(
        AssignmentExpressionSyntax assign, SemanticModel model, string filePath,
        HashSet<string> candidateNames,
        Dictionary<IEventSymbol, MemberDeclarationSyntax> candidates,
        Dictionary<IEventSymbol, HashSet<string>> siteFiles,
        Dictionary<IEventSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures)
    {
        if (assign.Left is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) return;

        var leftName = LeftName(assign.Left);
        if (leftName == null || !candidateNames.Contains(leftName)) return;

        var symbol = model.GetSymbolInfo(assign.Left).Symbol;
        if (symbol == null)
        {
            // 绑定不可解析且名字命中候选（EV-15）→ 保守跳过（CS2J5155）。
            foreach (var evt in candidates.Keys.Where(e => e.Name == leftName).ToList())
                MarkFailure(siteFailures, evt, MemberDesugarDiagnosticCode.EventUnbindableSite,
                    EventDiagnostics.Unbindable(leftName));
            return;
        }
        if (symbol is not IEventSymbol evtBound) return;
        if (!evtBound.IsDefinition) evtBound = (IEventSymbol)evtBound.OriginalDefinition;
        if (!candidates.ContainsKey(evtBound)) return;

        Record(siteFiles, evtBound, filePath);

        if (MemberDesugarSyntaxFacts.IsDynamic(assign.Left, model))
            MarkFailure(siteFailures, evtBound, MemberDesugarDiagnosticCode.EventDynamicSite,
                EventDiagnostics.Dynamic(evtBound.Name));
    }

    private static void HandleNameofSite(
        ExpressionSyntax argument, SemanticModel model, string filePath,
        HashSet<string> candidateNames,
        Dictionary<IEventSymbol, MemberDeclarationSyntax> candidates,
        Dictionary<IEventSymbol, HashSet<string>> siteFiles)
    {
        var name = argument switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            _ => null,
        };
        if (name == null || !candidateNames.Contains(name)) return;
        if (model.GetSymbolInfo(argument).Symbol is not IEventSymbol evt) return;
        if (!evt.IsDefinition) evt = (IEventSymbol)evt.OriginalDefinition;
        if (!candidates.ContainsKey(evt)) return;
        Record(siteFiles, evt, filePath);
    }

    // ---- plan 产出 ----

    private static EventPlan MakePlan(
        IEventSymbol evt, MemberDeclarationSyntax? decl, string declName,
        MemberDesugarOutcome outcome, IReadOnlyList<string> touched,
        MemberDesugarDiagnosticCode? failCode, string? failReason,
        MemberDesugarOptions options)
    {
        var hasField = decl != null && NeedsField(evt, decl);
        var policyCas = options.EventSubscriptionPolicy == EventSubscriptionPolicy.InterlockedCompareExchange;
        var planKind = evt.ExplicitInterfaceImplementations.Length > 0
            ? EventPlanKind.ExplicitInterface
            : !hasField ? EventPlanKind.AbstractOrInterface
            : decl is EventFieldDeclarationSyntax ? EventPlanKind.FieldLike
            : EventPlanKind.Custom;

        return new EventPlan
        {
            Id = EventPlan.BuildId(evt),
            Kind = MemberDesugarKind.Event,
            Symbol = evt,
            Outcome = outcome,
            TouchedFiles = touched,
            FailCode = failCode,
            FailReason = failReason,
            Event = evt,
            DeclName = declName,
            AdderName = EventFacts.AdderName(declName),
            RemoverName = EventFacts.RemoverName(declName),
            HasField = hasField,
            UseCas = hasField && policyCas && evt.ContainingType?.TypeKind != TypeKind.Struct,
            PlanKind = planKind,
        };
    }

    // ---- 小工具 ----

    private static string? DeclNameOf(MemberDeclarationSyntax decl)
        => decl switch
        {
            EventFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
            EventDeclarationSyntax e => e.Identifier.ValueText,
            _ => null,
        };

    private static string? LeftName(ExpressionSyntax left)
        => left switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            _ => null,
        };

    private static void Record(
        Dictionary<IEventSymbol, HashSet<string>> siteFiles, IEventSymbol evt, string filePath)
    {
        if (!siteFiles.TryGetValue(evt, out var set))
            siteFiles[evt] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(filePath);
    }

    private static void MarkFailure(
        Dictionary<IEventSymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        IEventSymbol evt, MemberDesugarDiagnosticCode code, string reason)
        => failures.TryAdd(evt, (code, reason));

    private static bool MatchesSet(IReadOnlySet<string> names, IEventSymbol evt)
    {
        var type = evt.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{evt.Name}")
            || (type != null && names.Contains(type));
    }

    private static IReadOnlyList<string> DeclarationFiles(IEventSymbol evt)
        => evt.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>声明文件 ∪ 站点文件 ∪ 层次联动文件（override 链 / 显式接口 / 隐式接口实现，
    /// 接口事件 ↔ 实现者互加，模式同 C11 CollectHierarchyLinks）。</summary>
    private static IReadOnlyList<string> CollectTouchedFiles(
        IEventSymbol evt,
        Dictionary<IEventSymbol, HashSet<string>> siteFiles,
        IEnumerable<IEventSymbol> allCandidates)
    {
        var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in evt.Locations.Where(l => l.IsInSource))
            touched.Add(loc.SourceTree!.FilePath);
        if (siteFiles.TryGetValue(evt, out var files))
            foreach (var f in files) touched.Add(f);

        foreach (var other in allCandidates)
        {
            if (SymbolEqualityComparer.Default.Equals(other, evt)) continue;
            if (Linked(other, evt) || Linked(evt, other))
                foreach (var loc in other.Locations.Where(l => l.IsInSource))
                    touched.Add(loc.SourceTree!.FilePath);
        }
        return touched.ToList();
    }

    private static bool Linked(IEventSymbol impl, IEventSymbol ifaceOrBase)
    {
        for (var b = impl.OverriddenEvent; b != null; b = b.OverriddenEvent)
            if (SymbolEqualityComparer.Default.Equals(b, ifaceOrBase)) return true;
        if (ifaceOrBase.ContainingType is not { TypeKind: TypeKind.Interface }) return false;
        if (impl.ExplicitInterfaceImplementations.Contains(ifaceOrBase, SymbolEqualityComparer.Default))
            return true;
        return impl.ContainingType != null && SymbolEqualityComparer.Default.Equals(
            impl.ContainingType.FindImplementationForInterfaceMember(ifaceOrBase), impl);
    }
}
