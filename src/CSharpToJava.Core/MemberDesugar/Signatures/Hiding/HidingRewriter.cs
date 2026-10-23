using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Hiding;

/// <summary>
/// C46 NH 逐文件重写器（设计 §4.4 NH-1/NH-2/NH-4）：隐藏成员改名 <c>{Name}_cs2jNew</c>、
/// 移除 <c>new</c> 令牌、全站点按符号绑定（含静态/虚分派静态绑定）改名。
/// <list type="bullet">
/// <item>NH-1：实例化石隐藏；属性/索引器/事件经 C11–C13 方法化后落到访问器方法对，按配对改名。</item>
/// <item>NH-2：newslot 链整组改名（声明 + 派生 override 一并在 <see cref="NhPlan.RenameByKey"/> 内）。</item>
/// <item>NH-4：字段/静态方法改名。</item>
/// <item>nameof：绑定改成员 → 字面量化原名（决策 15）；反射字符串站点命中 → CS2J5193（站点保留）。</item>
/// </list>
/// 声明与站点绑定一律取本轮 <c>_model</c>，符号匹配走跨 Compilation 稳定键 <see cref="NhPlan.SiteKey"/>。
/// NH-3 桥接（Bridged）与 NH-5 residual（Infeasible）不改名，故不会出现在本重写器的改名表中。
/// </summary>
internal sealed class HidingRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<string, string> _rename;
    private readonly Dictionary<string, string> _original;
    private readonly Dictionary<(string Type, string Name, int Arity), string> _accessorRename;
    private readonly HashSet<string> _primaryKeys;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public HidingRewriter(
        SemanticModel model,
        IReadOnlyList<NhPlan> plannedPlans,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _rename = new Dictionary<string, string>(StringComparer.Ordinal);
        _original = new Dictionary<string, string>(StringComparer.Ordinal);
        _accessorRename = new Dictionary<(string, string, int), string>();
        _primaryKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plannedPlans)
        {
            foreach (var (key, name) in plan.RenameByKey)
            {
                _rename[key] = name;
                // M4c：从 plan 键派生访问器回退项（含入类型 + 访问器原始名 + 参数个数 → 新名）。
                SignatureSyntaxFacts.AddAccessorRename(_accessorRename, key, name);
            }
            foreach (var (key, name) in plan.OriginalNameByKey) _original[key] = name;
            foreach (var key in plan.PrimaryKeys) _primaryKeys.Add(key);
        }
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    // ---- 声明侧（NH-1/NH-2/NH-4）----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (_model.GetDeclaredSymbol(node) is not { } declared) return visited;
        if (!_rename.TryGetValue(NhPlan.SiteKey(declared), out var newName)) return visited;

        if (_primaryKeys.Contains(NhPlan.SiteKey(declared))) _stats.SignatureHideRenamed++;
        return visited
            .WithIdentifier(IdentifierWithTrivia(visited.Identifier, newName))
            .WithModifiers(RemoveNew(visited.Modifiers))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var visited = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        // 符号查询一律走访问前的原始节点：初始化器内的改名站点会让 base.Visit
        // 产出离树的 VariableDeclarator 克隆，对其 GetDeclaredSymbol 抛
        // "Syntax node is not within syntax tree"（与 VisitMethodDeclaration
        // 查询原始 node 同纪律，两列表按索引一一对应）。
        var originalVariables = node.Declaration.Variables;
        var variables = new List<VariableDeclaratorSyntax>(visited.Declaration.Variables.Count);
        var renamed = false;
        for (var i = 0; i < visited.Declaration.Variables.Count; i++)
        {
            var variable = visited.Declaration.Variables[i];
            var original = originalVariables[i];
            var countIt = false;
            var replacement = variable;
            if (_model.GetDeclaredSymbol(original) is { } declared
                && _rename.TryGetValue(NhPlan.SiteKey(declared), out var newName))
            {
                replacement = variable.WithIdentifier(IdentifierWithTrivia(variable.Identifier, newName));
                renamed = true;
                countIt = _primaryKeys.Contains(NhPlan.SiteKey(declared));
            }
            if (countIt) _stats.SignatureHideRenamed++;
            variables.Add(replacement);
        }
        if (!renamed) return visited;

        return visited
            .WithDeclaration(visited.Declaration.WithVariables(SyntaxFactory.SeparatedList(variables)))
            .WithModifiers(RemoveNew(visited.Modifiers))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    // ---- 站点侧（NH-1/NH-2/NH-4 静态绑定改名 + 决策 15 闸门）----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // nameof(绑定改成员) → 字面量化原名（决策 15）。
        if (SignatureSyntaxFacts.IsNameofSite(node) && node.ArgumentList.Arguments.Count == 1
            && SignatureSyntaxFacts.BoundSymbol(_model, node.ArgumentList.Arguments[0].Expression)
                is { } named
            && _rename.ContainsKey(NhPlan.SiteKey(named)))
        {
            _stats.SignatureCallSitesRewritten++;
            return SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(_original[NhPlan.SiteKey(named)]))
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        // 反射/字符串契约站点命中被改名成员 → CS2J5193（Warning，非阻断，站点保留）。
        if (SignatureSyntaxFacts.IsReflectionContractSite(node, _model)) ReportReflection(node);

        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        var info = _model.GetSymbolInfo(node);
        if (info.Symbol is { } bound)
        {
            if (_rename.TryGetValue(NhPlan.SiteKey(bound), out var newName))
                return Rename(visited, newName);
            // 同文件使用点：C11–C13 已在本文件展开访问器方法对，站点直接绑定到合成方法
            // （声明文件即本文件，故不会像跨文件那样绑定失败）——plan 键是索引器/属性符号，
            // 按接收者静态类型 + 访问器原始名回退，否则声明改名而站点漏改（CS0571）。
            if (bound is IMethodSymbol
                && TryFallbackAccessorRename(visited, out var boundFallbackName))
                return Rename(visited, boundFallbackName);
            return visited;   // 真实绑定到非改名成员 → 不动
        }

        // 绑定失败（Symbol 为 null，可能带错误恢复候选）：候选符号（如 nameof/方法组）若命中改名表则沿用。
        var candidate = SignatureSyntaxFacts.BoundSymbol(_model, node);
        if (candidate != null && _rename.TryGetValue(NhPlan.SiteKey(candidate), out var candidateName))
            return Rename(visited, candidateName);

        // M4c 回退：C11–C13 在跨文件使用点合成的访问器调用（d.get_Name()/d.set_Name(...) 等）
        // 在本轮重解析模型中因声明文件仍是原始形态而无法绑定；以接收者静态类型 + 访问器原始名 +
        // 参数个数查 plan 派生回退表（most-derived-first）。仅在符号绑定失败时启用。
        if (TryFallbackAccessorRename(visited, out var fallbackName))
            return Rename(visited, fallbackName);
        return visited;
    }

    private SyntaxNode? Rename(MemberAccessExpressionSyntax node, string newName)
    {
        _stats.SignatureCallSitesRewritten++;
        return node.WithName(node.Name.WithIdentifier(
            IdentifierWithTrivia(node.Name.Identifier, newName)));
    }

    /// <summary>M4c 接收者静态类型锚定回退：仅当节点为 <c>recv.get_/set_/add_/remove_X(...)</c>
    /// 调用形且命中 plan 回退表时返回新名。</summary>
    private bool TryFallbackAccessorRename(MemberAccessExpressionSyntax node, out string newName)
    {
        newName = string.Empty;
        var name = node.Name.Identifier.ValueText;
        if (!SignatureSyntaxFacts.IsAccessorMethodName(name)) return false;
        if (node.Parent is not InvocationExpressionSyntax invocation) return false;
        if (_model.GetTypeInfo(node.Expression).Type is not { } receiver) return false;
        return SignatureSyntaxFacts.TryLookupAccessorRename(
            _accessorRename, receiver, name, invocation.ArgumentList.Arguments.Count, out newName);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (IdentifierNameSyntax)base.VisitIdentifierName(node)!;
        // 成员访问的 Name 子节点由 VisitMemberAccessExpression 统一处理，避免重复改名。
        if (node.Parent is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
            return visited;
        if (_model.GetSymbolInfo(node).Symbol is not { } bound) return visited;
        if (!_rename.TryGetValue(NhPlan.SiteKey(bound), out var newName)) return visited;
        _stats.SignatureCallSitesRewritten++;
        return visited.WithIdentifier(IdentifierWithTrivia(visited.Identifier, newName));
    }

    // ---- 小工具 ----

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

    private static SyntaxTokenList RemoveNew(SyntaxTokenList modifiers)
        => SyntaxFactory.TokenList(modifiers.Where(m => !m.IsKind(SyntaxKind.NewKeyword)));

    private static SyntaxToken IdentifierWithTrivia(SyntaxToken original, string newName)
        => SyntaxFactory.Identifier(original.LeadingTrivia, newName, original.TrailingTrivia);
}