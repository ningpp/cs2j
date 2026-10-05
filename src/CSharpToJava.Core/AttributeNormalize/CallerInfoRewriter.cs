using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// 按 active plan 重写单个文件（design §4.1/§4.3 AN-C3/AN-C4/AN-C7）：
/// ①调用点插入显式常量实参（全位置表位置插入、含命名实参则插命名形、
/// params 前插入、this/base 初始化器同形）；
/// ②移除源内目标形参上的 Caller attribute（同列表其它 attribute、trivia、
/// 默认值表达式保留）。改动节点挂 <see cref="RewriteAnnotation"/>。
/// 只接受语义绑定命中，禁止文本替换。
/// </summary>
internal sealed class CallerInfoRewriter : CSharpSyntaxRewriter
{
    public static readonly SyntaxAnnotation RewriteAnnotation
        = new("Cs2jAttributeNormalizeRewrite");

    private readonly SemanticModel _model;
    private readonly IReadOnlyList<CallerInfoPlan> _activePlans;
    private readonly AttributeNormalizeStatistics _statistics;

    private readonly Dictionary<SyntaxNode, List<(CallerInfoPlan Plan, CallerSite Site)>> _sitesByNode
        = new();

    public CallerInfoRewriter(
        SemanticModel model,
        IReadOnlyList<CallerInfoPlan> activePlans,
        AttributeNormalizeStatistics statistics)
    {
        _model = model;
        _activePlans = activePlans;
        _statistics = statistics;

        BuildSiteMap(model.SyntaxTree);
    }

    private void BuildSiteMap(SyntaxTree syntaxTree)
    {
        foreach (var plan in _activePlans)
        {
            foreach (var site in plan.Sites)
            {
                if (!site.IsFeasible) continue;
                if (site.CallNode.SyntaxTree != syntaxTree) continue;

                if (!_sitesByNode.TryGetValue(site.CallNode, out var list))
                {
                    list = new List<(CallerInfoPlan, CallerSite)>();
                    _sitesByNode[site.CallNode] = list;
                }

                list.Add((plan, site));
            }
        }
    }

    public bool HasSitesInTree => _sitesByNode.Count > 0;

    // =====================================================================
    // SyntaxRewriter visits
    // =====================================================================

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (!_sitesByNode.TryGetValue(node, out var entries)) return visited;
        return (SyntaxNode)RewriteCall(visited, node, entries);
    }

    public override SyntaxNode? VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        var visited = (ConstructorInitializerSyntax)base.VisitConstructorInitializer(node)!;
        if (!_sitesByNode.TryGetValue(node, out var entries)) return visited;
        return (SyntaxNode)RewriteCall(visited, node, entries);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        if (!_sitesByNode.TryGetValue(node, out var entries)) return visited;
        return (SyntaxNode)RewriteCall(visited, node, entries);
    }

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        var visited = (ElementAccessExpressionSyntax)base.VisitElementAccessExpression(node)!;
        if (!_sitesByNode.TryGetValue(node, out var entries)) return visited;
        return (SyntaxNode)RewriteCall(visited, node, entries);
    }

    public override SyntaxNode? VisitParameter(ParameterSyntax node)
    {
        var visited = (ParameterSyntax)base.VisitParameter(node)!;

        var parameterSymbol = _model.GetDeclaredSymbol(node);
        if (parameterSymbol == null) return visited;

        var plan = FindDeclarationPlan(parameterSymbol);
        if (plan == null) return visited;

        return RemoveCallerAttributes(visited, parameterSymbol);
    }

    // =====================================================================
    // 调用点填参
    // =====================================================================

    private SyntaxNode RewriteCall(
        SyntaxNode visitedNode,
        SyntaxNode originalNode,
        List<(CallerInfoPlan Plan, CallerSite Site)> entries)
    {
        var originalArgumentList = GetArgumentList(originalNode)
            ?? throw new InvalidOperationException("call node has no argument list");

        var hasNamed = originalArgumentList.Arguments.Any(a => a.NameColon != null);
        var explicitOrdinals = GetExplicitArgumentOrdinals(originalNode);

        // 计算每个站点的插入位置：原表中形参序号小于本站点的显式实参数。
        // 降序插入保持下标稳定；同槽位按形参序号降序，使低序号站点最终排在前面。
        var insertion = entries
            .Select(e =>
            {
                var siteOrdinal = e.Site.ParameterOrdinal;
                var existingBefore = originalArgumentList.Arguments
                    .Count(a => TryGetOrdinal(explicitOrdinals, a, out var ord) && ord < siteOrdinal);
                return (Slot: existingBefore, e.Site);
            })
            .OrderByDescending(t => t.Slot)
            .ThenByDescending(t => t.Site.ParameterOrdinal)
            .ToList();

        var newArguments = new List<ArgumentSyntax>(originalArgumentList.Arguments);
        foreach (var (slot, site) in insertion)
            newArguments.Insert(slot, BuildArgument(site, hasNamed));

        var newList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(newArguments));

        var result = WithArgumentList(visitedNode, newList)
            .WithAdditionalAnnotations(RewriteAnnotation);
        return result;
    }

    private static ArgumentSyntax BuildArgument(CallerSite site, bool hasNamed)
    {
        ExpressionSyntax literal = site.Kind == CallerInfoKind.CallerLineNumber
            ? SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(site.IntValue ?? 0))
            : SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(site.StringValue ?? string.Empty));


        var argument = SyntaxFactory.Argument(literal)
            .WithLeadingTrivia(SyntaxFactory.ElasticSpace)
            .WithTrailingTrivia(SyntaxFactory.ElasticSpace);

        if (hasNamed)
        {
            argument = argument.WithNameColon(
                SyntaxFactory.NameColon(site.Parameter.Name));
        }

        return argument.WithAdditionalAnnotations(RewriteAnnotation);
    }

    private static bool TryGetOrdinal(
        Dictionary<ArgumentSyntax, int> explicitOrdinals,
        ArgumentSyntax argument,
        out int ordinal)
        => explicitOrdinals.TryGetValue(argument, out ordinal);

    private Dictionary<ArgumentSyntax, int> GetExplicitArgumentOrdinals(SyntaxNode node)
    {
        var result = new Dictionary<ArgumentSyntax, int>();
        var operation = _model.GetOperation(node);
        IEnumerable<IArgumentOperation> arguments = operation switch
        {
            IInvocationOperation i => i.Arguments,
            IObjectCreationOperation oc => oc.Arguments,
            IPropertyReferenceOperation pr => pr.Arguments,
            _ => Array.Empty<IArgumentOperation>(),
        };

        foreach (var argument in arguments)
        {
            if (argument.IsImplicit) continue;
            if (argument.Syntax is ArgumentSyntax argumentSyntax && argument.Parameter != null)
                result[argumentSyntax] = argument.Parameter.Ordinal;
        }

        return result;
    }

    private static BaseArgumentListSyntax? GetArgumentList(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax i => i.ArgumentList,
        ObjectCreationExpressionSyntax oc => oc.ArgumentList,
        ConstructorInitializerSyntax ci => ci.ArgumentList,
        ElementAccessExpressionSyntax ea => ea.ArgumentList,
        _ => null,
    };

    private static SyntaxNode WithArgumentList(SyntaxNode node, BaseArgumentListSyntax list) => node switch
    {
        InvocationExpressionSyntax i => i.WithArgumentList((ArgumentListSyntax)list),
        ObjectCreationExpressionSyntax oc => oc.WithArgumentList((ArgumentListSyntax)list),
        ConstructorInitializerSyntax ci => ci.WithArgumentList((ArgumentListSyntax)list),
        ElementAccessExpressionSyntax ea => ea.WithArgumentList((BracketedArgumentListSyntax)list),
        _ => throw new InvalidOperationException("unsupported call node"),
    };

    // =====================================================================
    // 声明端 Caller attribute 移除（AN-C4）
    // =====================================================================

    private CallerInfoPlan? FindDeclarationPlan(IParameterSymbol parameterSymbol)
    {
        foreach (var plan in _activePlans)
        {
            switch (parameterSymbol.ContainingSymbol)
            {
                case IMethodSymbol method
                    when SymbolEqualityComparer.Default.Equals(plan.TargetMethod, method):
                    return plan;
                case IPropertySymbol property:
                    if (SymbolEqualityComparer.Default.Equals(plan.TargetMethod, property.GetMethod)
                        || SymbolEqualityComparer.Default.Equals(plan.TargetMethod, property.SetMethod))
                        return plan;
                    break;
            }
        }

        return null;
    }

    private SyntaxNode RemoveCallerAttributes(ParameterSyntax node, IParameterSymbol parameterSymbol)
    {
        var callerSpans = new HashSet<TextSpan>();
        foreach (var data in parameterSymbol.GetAttributes())
        {
            if (!KnownAttributeCatalog.IsCallerAttribute(data.AttributeClass)) continue;
            if (data.ApplicationSyntaxReference is { } reference
                && reference.SyntaxTree == _model.SyntaxTree)
            {
                callerSpans.Add(reference.Span);
            }
        }

        if (callerSpans.Count == 0) return node;

        var attributesToRemove = node.DescendantNodes()
            .OfType<AttributeSyntax>()
            .Where(a => callerSpans.Contains(a.Span))
            .ToList();

        // 列表内属性将被全部移除时，整列表（含括号）一起移除；
        // 其余情况只移除个别属性。
        var emptyLists = node.AttributeLists
            .Where(list => list.Attributes.All(attributesToRemove.Contains))
            .ToList();
        var partialAttributes = attributesToRemove
            .Where(a => !emptyLists.Contains(a.Parent))
            .Cast<SyntaxNode>()
            .ToList();
        var trackedNodes = emptyLists.Cast<SyntaxNode>().Concat(partialAttributes).ToList();

        var result = node.TrackNodes(trackedNodes);

        foreach (var list in emptyLists)
        {
            var current = result.GetCurrentNode(list);
            if (current == null) continue;
            result = (ParameterSyntax)result.RemoveNode(
                current,
                SyntaxRemoveOptions.KeepExteriorTrivia)!;
        }

        foreach (var attribute in partialAttributes.Cast<AttributeSyntax>())
        {
            var current = result.GetCurrentNode(attribute);
            if (current == null) continue;
            result = (ParameterSyntax)result.RemoveNode(
                current,
                SyntaxRemoveOptions.KeepExteriorTrivia)!;
        }

        _statistics.CallerAttributesRemoved += callerSpans.Count;
        return result.WithAdditionalAnnotations(RewriteAnnotation);
    }
}
