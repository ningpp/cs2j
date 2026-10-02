using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Covariant;

/// <summary>
/// C46 CR 协变返回逐文件重写器（设计 §4.3-3/§4.4 CR-1..CR-4，决策 10/11/12）：
/// <list type="bullet">
/// <item>CR-2：把擦除冲突协变 override 拆为「基槽签名 <c>override</c> bridge（体 <c>return M_cs2jCovariant(...);</c>）」
/// + 「原体改名 <c>M_cs2jCovariant</c>」两条声明；sealed override 的 bridge 携带 sealed，改名体非虚。
/// 编译内更深派生 override（sub-chain）整组改同一名，顶层改名体保留 <c>virtual</c>、派生保留 <c>override</c>，
/// bridge 的委托调用经虚分派进入最深改名体。</item>
/// <item>站点：绑定到改名符号的调用点（派生静态类型）改新名；基/接口分派点不动（虚分派进 bridge）。
/// <c>nameof</c> 字面量化；反射/字符串契约站点 CS2J5193（非阻断，站点保留）。</item>
/// <item>CR-4 residual：无 plan 归属的协变 override → CS2J5199（strict→Error）。</item>
/// </list>
/// 绑定一律取本轮 <c>_model</c>，跨 Compilation 符号匹配走 <see cref="CrPlan.SiteKey"/> 稳定键。
/// </summary>
internal sealed class CovariantBridgeRewriter : CSharpSyntaxRewriter
{
    private const string BridgeAnchorKind = "Cs2jCovariantBridge";

    private readonly SemanticModel _model;
    private readonly Dictionary<string, CrPlan> _bridgeByKey;
    private readonly Dictionary<string, string> _rename;
    private readonly Dictionary<string, string> _original;
    private readonly HashSet<string> _coveredKeys;
    private readonly Dictionary<string, MethodDeclarationSyntax> _pendingBridges;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public CovariantBridgeRewriter(
        SemanticModel model,
        IReadOnlyList<CrPlan> plannedPlans,
        IReadOnlyList<CrPlan> allPlansForFile,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _bridgeByKey = new Dictionary<string, CrPlan>(StringComparer.Ordinal);
        _rename = new Dictionary<string, string>(StringComparer.Ordinal);
        _original = new Dictionary<string, string>(StringComparer.Ordinal);
        _coveredKeys = new HashSet<string>(StringComparer.Ordinal);
        _pendingBridges = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);

        foreach (var plan in plannedPlans)
        {
            if (plan.Verdict != CrVerdict.Bridged) continue;
            if (plan.BridgeKey != null) _bridgeByKey[plan.BridgeKey] = plan;
            foreach (var (key, name) in plan.RenameByKey)
            {
                _rename[key] = name;
                if (plan.OriginalNameByKey.TryGetValue(key, out var original)) _original[key] = original;
            }
        }
        foreach (var plan in allPlansForFile)
        {
            _coveredKeys.Add(CrPlan.SiteKey(plan.Member));
            foreach (var key in plan.MemberKeys) _coveredKeys.Add(key);
            foreach (var key in plan.RenameByKey.Keys) _coveredKeys.Add(key);
        }

        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    /// <summary>改写入口：先对原件做 CR-4 residual 对账，再进入声明/站点重写。</summary>
    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
    {
        ScanResidual(root);
        return (CompilationUnitSyntax)Visit(root)!;
    }

    // ---- 声明侧（CR-2 bridge + 改名 + sub-chain 整组）----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (_model.GetDeclaredSymbol(node) is not IMethodSymbol declared) return visited;
        var key = CrPlan.SiteKey(declared);

        if (_bridgeByKey.TryGetValue(key, out var plan))
        {
            _stats.SignatureCovariantBridgesSynthesized++;
            _stats.SignatureCovariantRenamed++;
            _pendingBridges[plan.Id] = BuildBridge(visited, plan);
            return RenamedBridgePoint(visited, plan)
                .WithAdditionalAnnotations(new SyntaxAnnotation(BridgeAnchorKind, plan.Id));
        }

        if (_rename.TryGetValue(key, out var newName))
        {
            _stats.SignatureCovariantRenamed++;
            return visited.WithIdentifier(IdentifierWithTrivia(visited.Identifier, newName));
        }
        return visited;
    }

    // 类型级成员表重建：把带 bridge 锚点的方法展开为 [bridge, 改名体] 两条声明。
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        return visited.WithMembers(RebuildMembers(visited.Members));
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
        return visited.WithMembers(RebuildMembers(visited.Members));
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        return visited.WithMembers(RebuildMembers(visited.Members));
    }

    private SyntaxList<MemberDeclarationSyntax> RebuildMembers(SyntaxList<MemberDeclarationSyntax> members)
    {
        List<MemberDeclarationSyntax>? rebuilt = null;
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var annotation = member is MethodDeclarationSyntax method
                ? method.GetAnnotations(BridgeAnchorKind).FirstOrDefault()
                : null;
            if (annotation == null)
            {
                rebuilt?.Add(member);
                continue;
            }

            rebuilt ??= members.Take(i).ToList();
            var renamed = ((MethodDeclarationSyntax)member).WithoutAnnotations(BridgeAnchorKind);
            if (annotation.Data != null && _pendingBridges.TryGetValue(annotation.Data, out var bridge))
                rebuilt.Add(bridge);
            rebuilt.Add(renamed);
        }
        return rebuilt == null ? members : SyntaxFactory.List(rebuilt);
    }

    // ---- 站点侧（CR-2 改名点 + 决策 15 闸门）----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // nameof(绑定改名成员) → 字面量化原名（决策 15）。
        if (SignatureSyntaxFacts.IsNameofSite(node) && node.ArgumentList.Arguments.Count == 1
            && SignatureSyntaxFacts.BoundSymbol(_model, node.ArgumentList.Arguments[0].Expression)
                is { } named
            && _original.TryGetValue(CrPlan.SiteKey(named), out var originalName))
        {
            _stats.SignatureCallSitesRewritten++;
            return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(originalName))
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        // 反射/字符串契约站点命中被改名成员 → CS2J5193（Warning，非阻断，站点保留）。
        if (SignatureSyntaxFacts.IsReflectionContractSite(node, _model)) ReportReflection(node);

        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        if (SignatureSyntaxFacts.BoundSymbol(_model, node) is not { } bound) return visited;
        if (!_rename.TryGetValue(CrPlan.SiteKey(bound), out var newName)) return visited;
        _stats.SignatureCallSitesRewritten++;
        return visited.WithName(visited.Name.WithIdentifier(
            IdentifierWithTrivia(visited.Name.Identifier, newName)));
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (IdentifierNameSyntax)base.VisitIdentifierName(node)!;
        // 成员访问的 Name 子节点由 VisitMemberAccessExpression 统一处理，避免重复改名。
        if (node.Parent is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
            return visited;
        if (SignatureSyntaxFacts.BoundSymbol(_model, node) is not { } bound) return visited;
        if (!_rename.TryGetValue(CrPlan.SiteKey(bound), out var newName)) return visited;
        _stats.SignatureCallSitesRewritten++;
        return visited.WithIdentifier(IdentifierWithTrivia(visited.Identifier, newName));
    }

    // ---- CR-4 residual 对账 ----

    private void ScanResidual(CompilationUnitSyntax root)
    {
        foreach (var decl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (_model.GetDeclaredSymbol(decl) is not IMethodSymbol method) continue;
            if (method.MethodKind != MethodKind.Ordinary || !method.IsOverride || method.ReturnsVoid) continue;
            if (method.OverriddenMethod is not { } overridden) continue;
            if (SymbolEqualityComparer.Default.Equals(method.ReturnType, overridden.ReturnType)) continue;
            if (_coveredKeys.Contains(CrPlan.SiteKey(method))) continue;
            ReportResidual(decl, method);
        }
    }

    // ---- 构造 ----

    private static MethodDeclarationSyntax BuildBridge(MethodDeclarationSyntax original, CrPlan plan)
    {
        var arguments = string.Join(", ", original.ParameterList.Parameters.Select(ArgumentText));
        var call = SyntaxFactory.ParseExpression($"{plan.RenamedName}({arguments})");
        var returnType = SyntaxFactory.ParseTypeName(plan.BaseReturnTypeText!);
        return original
            .WithReturnType(returnType)
            .WithBody(null)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(call))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private static MethodDeclarationSyntax RenamedBridgePoint(MethodDeclarationSyntax visited, CrPlan plan)
    {
        var modifiers = SyntaxFactory.TokenList(visited.Modifiers.Where(m =>
            !m.IsKind(SyntaxKind.OverrideKeyword) && !m.IsKind(SyntaxKind.SealedKeyword)));
        if (plan.HasDescendants && !plan.Sealed)
            modifiers = modifiers.Add(
                SyntaxFactory.Token(SyntaxKind.VirtualKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        return visited
            .WithIdentifier(IdentifierWithTrivia(visited.Identifier, plan.RenamedName!))
            .WithModifiers(modifiers)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private static string ArgumentText(ParameterSyntax parameter)
    {
        var prefix = parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? "ref "
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? "out "
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)) ? "in "
            : string.Empty;
        return prefix + parameter.Identifier.Text;
    }

    private static SyntaxToken IdentifierWithTrivia(SyntaxToken original, string newName)
        => SyntaxFactory.Identifier(original.LeadingTrivia, newName, original.TrailingTrivia);

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
            foreach (var original in _original.Values.Distinct(StringComparer.Ordinal))
            {
                if (!MatchesReflectionName(text, original)) continue;
                var span = node.GetLocation().GetLineSpan().StartLinePosition;
                _diagnostics.Add(SignatureDiagnostics.Create(
                    MemberDesugarDiagnosticCode.SignatureReflectionContract,
                    SignatureDiagnostics.ReflectionContract(original, api),
                    _options.Signatures, original, _filePath, span.Line + 1, span.Character + 1));
                _stats.SignatureReflectionWarnings++;
            }
        }
    }

    private static bool MatchesReflectionName(string text, string memberName)
        => string.Equals(text, memberName, StringComparison.Ordinal)
           || text.EndsWith("." + memberName, StringComparison.Ordinal);
}