using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.ExplicitInterface;

/// <summary>
/// C46 SI 逐文件重写器（设计 §4.3-3/§4.4 SI-1/2/3/5）：单遍同时处理声明侧与调用点侧，
/// 绑定一律取本轮 <c>_model</c>（可能为前置阶段重解析后的新鲜模型），符号匹配一律走
/// <see cref="SiPlan.SiteKey"/> 稳定键（跨 Compilation 的 SymbolEqualityComparer 失效）。
/// <list type="bullet">
/// <item>SI-1：保留实现去 <c>ExplicitInterfaceSpecifier</c>、补 <c>public</c>（其余修饰符原样保留）。</item>
/// <item>SI-2：让位类成员声明改名为 <c>{Name}_cs2jClass</c>，其声明文件引用同步改名。</item>
/// <item>SI-3：冗余显式声明（同组非保留实现）整条删除。</item>
/// <item>SI-5：绑定让位成员的成员访问/未限定引用改名；<c>nameof</c> 字面量化；反射字符串站点保留并告警。</item>
/// <item>SI-7：输出树残留、无 plan 归属的源接口显式方法 → CS2J5199。</item>
/// </list>
/// </summary>
internal sealed class ExplicitInterfaceRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<string, SiPlan> _active;
    private readonly Dictionary<string, SiPlan> _displaced;
    private readonly Dictionary<(string Type, string Name, int Arity), string> _displacedAccessor;
    private readonly HashSet<string> _owned;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public ExplicitInterfaceRewriter(
        SemanticModel model,
        IReadOnlyList<SiPlan> plannedPlans,
        IReadOnlyList<SiPlan> allPlansForFile,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _active = new Dictionary<string, SiPlan>(StringComparer.Ordinal);
        _displaced = new Dictionary<string, SiPlan>(StringComparer.Ordinal);
        _displacedAccessor = new Dictionary<(string, string, int), string>();
        foreach (var plan in plannedPlans)
        {
            foreach (var key in plan.ImplementationKeys) _active[key] = plan;
            if (plan.DisplacedKey != null)
            {
                _displaced[plan.DisplacedKey] = plan;
                // M4c：从让位成员键派生访问器回退项（含入类型 + 访问器原始名 + 参数个数 → 新名）。
                if (plan.DisplacedName != null)
                    SignatureSyntaxFacts.AddAccessorRename(
                        _displacedAccessor, plan.DisplacedKey, plan.DisplacedName);
            }
        }
        _owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in allPlansForFile)
            foreach (var key in plan.ImplementationKeys) _owned.Add(key);
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    // ---- 声明侧（SI-1/2/3 + SI-7 残余对账）----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // 显式实现判定用语法事实（ExplicitInterfaceSpecifier）：C11–C13 方法化后事件访问器
        // 方法符号的 ExplicitInterfaceImplementations 可能为空，符号判定会漏；键仍由符号派生。
        if (node.ExplicitInterfaceSpecifier != null
            && _model.GetDeclaredSymbol(node) is IMethodSymbol method)
        {
            var key = SiPlan.SiteKey(method);
            if (_active.TryGetValue(key, out var plan))
            {
                // SI-3 合组内非保留实现：冗余显式声明整条删除。
                if (!string.Equals(plan.RetainedKey, key, StringComparison.Ordinal))
                    return null;

                _stats.SignaturePlansPromoted++;
                if (plan.Merged) _stats.SignatureDualInterfaceMerged++;
                var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
                return Promote(visited);
            }

            // 未被任何 plan 认领的残留（源接口）→ CS2J5199；带跳过诊断/extern 的显式实现不在此列。
            if (!_owned.Contains(key) && HasInSourceExplicitInterface(node, method))
                ReportResidual(node, method);
            return base.VisitMethodDeclaration(node);
        }

        // SI-2：让位类成员声明改名（方法面）。
        if (_model.GetDeclaredSymbol(node) is { } declared
            && _displaced.TryGetValue(SiPlan.SiteKey(declared), out var displacedPlan))
        {
            _stats.SignatureDisplacedRenamed++;
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            return visited.WithIdentifier(
                IdentifierWithTrivia(visited.Identifier, displacedPlan.DisplacedName!));
        }

        return base.VisitMethodDeclaration(node);
    }

    // ---- 调用点侧（SI-5）----

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        var info = _model.GetSymbolInfo(node);
        if (info.Symbol is { } bound)
        {
            if (bound is IMethodSymbol method && _displaced.TryGetValue(SiPlan.SiteKey(method), out var plan))
            {
                _stats.SignatureCallSitesRewritten++;
                return visited.WithName(Rename(visited.Name, plan.DisplacedName!));
            }
            return visited;   // 真实绑定到非让位成员 → 不动
        }

        // 绑定失败（Symbol 为 null，可能带错误恢复候选）：候选符号（如 nameof/方法组）若命中让位表则沿用。
        var candidate = SignatureSyntaxFacts.BoundSymbol(_model, node);
        if (candidate is IMethodSymbol candMethod
            && _displaced.TryGetValue(SiPlan.SiteKey(candMethod), out var candPlan))
        {
            _stats.SignatureCallSitesRewritten++;
            return visited.WithName(Rename(visited.Name, candPlan.DisplacedName!));
        }

        // M4c 回退：C11–C13 在跨文件使用点合成的访问器调用（c.get_Name()/c.set_Name(...)）在本轮重解析
        // 模型中因声明文件仍是原始形态而无法绑定；以接收者静态类型 + 访问器原始名 + 参数个数查 plan 派生
        // 回退表（most-derived-first）。接口/基类型接收者不在表内 → 接口分派点与基槽调用保持不动。
        if (node.Parent is InvocationExpressionSyntax invocation
            && SignatureSyntaxFacts.IsAccessorMethodName(node.Name.Identifier.ValueText)
            && _model.GetTypeInfo(node.Expression).Type is { } receiver
            && SignatureSyntaxFacts.TryLookupAccessorRename(
                _displacedAccessor, receiver, node.Name.Identifier.ValueText,
                invocation.ArgumentList.Arguments.Count, out var fallbackName))
        {
            _stats.SignatureCallSitesRewritten++;
            return visited.WithName(Rename(visited.Name, fallbackName));
        }
        return visited;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (IdentifierNameSyntax)base.VisitIdentifierName(node)!;
        // 成员访问的 Name 子节点由 VisitMemberAccessExpression 统一处理，避免重复改名。
        if (node.Parent is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
            return visited;
        if (SignatureSyntaxFacts.BoundSymbol(_model, node) is not IMethodSymbol method)
            return visited;
        if (!_displaced.TryGetValue(SiPlan.SiteKey(method), out var plan))
            return visited;
        _stats.SignatureCallSitesRewritten++;
        return visited.WithIdentifier(IdentifierWithTrivia(visited.Identifier, plan.DisplacedName!));
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // nameof(C.Foo) 绑定让位成员 → 字面量化为原名（决策 15）。
        if (SignatureSyntaxFacts.IsNameofSite(node) && node.ArgumentList.Arguments.Count == 1
            && SignatureSyntaxFacts.BoundSymbol(_model, node.ArgumentList.Arguments[0].Expression)
                is IMethodSymbol named
            && _displaced.TryGetValue(SiPlan.SiteKey(named), out var plan))
        {
            _stats.SignatureCallSitesRewritten++;
            return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(plan.DisplacedOriginalName!))
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        // 反射/字符串契约站点命中被改名成员 → CS2J5193（Warning，非阻断，站点保留）。
        if (SignatureSyntaxFacts.IsReflectionContractSite(node, _model)) ReportReflection(node);

        return base.VisitInvocationExpression(node);
    }

    // ---- 构造 ----

    private static MethodDeclarationSyntax Promote(MethodDeclarationSyntax node)
    {
        var modifiers = new List<SyntaxToken>();
        if (!node.Modifiers.Any(AccessorsToMethods.IsAccessibility))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        modifiers.AddRange(node.Modifiers);
        return node
            .WithExplicitInterfaceSpecifier(null)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private static SimpleNameSyntax Rename(SimpleNameSyntax name, string newName)
        => name.WithIdentifier(IdentifierWithTrivia(name.Identifier, newName));

    private static SyntaxToken IdentifierWithTrivia(SyntaxToken original, string newName)
        => SyntaxFactory.Identifier(original.LeadingTrivia, newName, original.TrailingTrivia);

    /// <summary>残留判定：显式实现的接口槽或限定名类型在源内（源接口才纳入 SI-7 对账；
/// 元数据接口的显式实现由转换器侧兜底，不在本 pass 归属）。</summary>
    private bool HasInSourceExplicitInterface(MethodDeclarationSyntax node, IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Any(slot => slot.Locations.Any(l => l.IsInSource)))
            return true;
        if (node.ExplicitInterfaceSpecifier?.Name is { } name
            && _model.GetTypeInfo(name).Type is { } iface)
            return iface.Locations.Any(l => l.IsInSource);
        return false;
    }

    // ---- 诊断 ----

    private void ReportResidual(MethodDeclarationSyntax node, IMethodSymbol method)
    {
        const MemberDesugarDiagnosticCode code = MemberDesugarDiagnosticCode.SignatureResidual;
        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        _diagnostics.Add(SignatureDiagnostics.Create(
            code, SignatureDiagnostics.Residual(_filePath, 1), _options.Signatures,
            method.Name, _filePath, span.Line + 1, span.Character + 1));
        _stats.SignatureResidual++;
        _stats.PlansSkipped++;
        _stats.SkippedByReason.TryGetValue(code, out var count);
        _stats.SkippedByReason[code] = count + 1;
    }

    private void ReportReflection(InvocationExpressionSyntax node)
    {
        var api = (SignatureSyntaxFacts.BoundSymbol(_model, node) as IMethodSymbol)?.Name ?? "reflection";
        foreach (var arg in node.ArgumentList.Arguments)
        {
            if (arg.Expression is not LiteralExpressionSyntax { Token.Value: string text }) continue;
            foreach (var plan in _displaced.Values.Distinct())
            {
                if (plan.DisplacedOriginalName == null
                    || !MatchesReflectionName(text, plan.DisplacedOriginalName)) continue;
                var span = node.GetLocation().GetLineSpan().StartLinePosition;
                _diagnostics.Add(SignatureDiagnostics.Create(
                    MemberDesugarDiagnosticCode.SignatureReflectionContract,
                    SignatureDiagnostics.ReflectionContract(plan.DisplacedOriginalName, api),
                    _options.Signatures, plan.DisplacedOriginalName, _filePath,
                    span.Line + 1, span.Character + 1));
                _stats.SignatureReflectionWarnings++;
            }
        }
    }

    private static bool MatchesReflectionName(string text, string memberName)
        => string.Equals(text, memberName, StringComparison.Ordinal)
           || text.EndsWith("." + memberName, StringComparison.Ordinal);
}