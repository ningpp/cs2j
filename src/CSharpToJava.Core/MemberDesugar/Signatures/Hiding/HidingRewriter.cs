using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Hiding;

/// <summary>
/// C46 NH 逐文件重写器（设计 §4.4 NH-4）：显式 <c>new</c> 的字段/静态方法改名
/// <c>{Name}_cs2jNew</c>、移除 <c>new</c> 令牌、全站点按符号绑定（含静态绑定）改名。
/// 声明与站点绑定一律取本轮 <c>_model</c>，符号匹配走跨 Compilation 稳定键
/// <see cref="NhPlan.SiteKey"/>。NH-3 桥接（Bridged）不改名，故不会出现在本重写器的改名表中。
/// </summary>
internal sealed class HidingRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<string, string> _rename;
    private readonly HashSet<string> _primaryKeys;
    private readonly MemberDesugarStatistics _stats;
    private readonly string _filePath;

    public HidingRewriter(
        SemanticModel model,
        IReadOnlyList<NhPlan> plannedPlans,
        MemberDesugarStatistics stats,
        string filePath)
    {
        _model = model;
        _rename = new Dictionary<string, string>(StringComparer.Ordinal);
        _primaryKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plannedPlans)
        {
            foreach (var (key, name) in plan.RenameByKey) _rename[key] = name;
            if (plan.PrimaryKey != null) _primaryKeys.Add(plan.PrimaryKey);
        }
        _stats = stats;
        _filePath = filePath;
    }

    // ---- 声明侧（NH-4）----

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
        var variables = new List<VariableDeclaratorSyntax>(visited.Declaration.Variables.Count);
        var renamed = false;
        foreach (var variable in visited.Declaration.Variables)
        {
            var countIt = false;
            var replacement = variable;
            if (_model.GetDeclaredSymbol(variable) is { } declared
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

    // ---- 站点侧（NH-4 静态绑定改名）----

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        if (_model.GetSymbolInfo(node).Symbol is not { } bound) return visited;
        if (!_rename.TryGetValue(NhPlan.SiteKey(bound), out var newName)) return visited;
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
        if (_model.GetSymbolInfo(node).Symbol is not { } bound) return visited;
        if (!_rename.TryGetValue(NhPlan.SiteKey(bound), out var newName)) return visited;
        _stats.SignatureCallSitesRewritten++;
        return visited.WithIdentifier(IdentifierWithTrivia(visited.Identifier, newName));
    }

    // ---- 小工具 ----

    private static SyntaxTokenList RemoveNew(SyntaxTokenList modifiers)
        => SyntaxFactory.TokenList(modifiers.Where(m => !m.IsKind(SyntaxKind.NewKeyword)));

    private static SyntaxToken IdentifierWithTrivia(SyntaxToken original, string newName)
        => SyntaxFactory.Identifier(original.LeadingTrivia, newName, original.TrailingTrivia);
}