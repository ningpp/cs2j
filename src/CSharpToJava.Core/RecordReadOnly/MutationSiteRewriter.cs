using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record SiteFailure(
    RecordReadOnlyDiagnosticCode Code,
    string Reason,
    Location Location);

/// <summary>
/// 用点可行性分类。Task 11 覆盖 MS-1（单段简单赋值）/MS-8/MS-9/MS-13 与 RR050/RR051；
/// 其余用点形态在后续任务打开，之前统一 RR020（pending）。
/// </summary>
internal static class MutationSiteClassifier
{
    public static SiteFailure? Classify(
        RecordAnalysis analysis,
        RecordPlan plan,
        RecordInfo info,
        MutationSiteInfo site)
    {
        var model = analysis.Compilation!.GetSemanticModel(site.WriteNode.SyntaxTree)!;

        switch (site.Kind)
        {
            case SiteKind.IndexerAssign:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR042,
                    "indexer assignment on the record itself cannot be expressed with 'with' " +
                    "(RS-19/RC-9/MS-18).", site.Location);

            // 后续任务支持的形态：在落地前整 record 保持原子跳过。
            case SiteKind.CompoundAssign:
            case SiteKind.Increment:
            case SiteKind.ConditionalAssign:
            case SiteKind.RefOutRecordArg:
            case SiteKind.RefOutMemberArg:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                    $"mutation site kind '{site.Kind}' is rewritten by a later stage.",
                    site.Location);
        }

        if (site.Kind != SiteKind.SimpleAssign)
            return Pending(site);

        if (!site.IsStatementPosition)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                "member assignment in an expression context cannot be rewritten to a " +
                "preceding 'with' statement (MS-16).", site.Location);

        // 仅单段链（recv.M）；多段（a.B.C）属 MS-17。
        var segmentCount = CountRecordMemberSegments(site, info);
        if (segmentCount != 1)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "nested member chains (MS-17) are rewritten by a later stage.", site.Location);

        var memberFailure = MemberIsInitSettable(plan, site.Member);
        if (memberFailure != null) return new SiteFailure(memberFailure.Code,
            memberFailure.Reason, site.Location);

        var receiverFailure = ReceiverWritableAndPure(model, site.Receiver, info);
        return receiverFailure;
    }

    private static SiteFailure Pending(MutationSiteInfo site) =>
        new(RecordReadOnlyDiagnosticCode.RR020,
            $"mutation site kind '{site.Kind}' is rewritten by a later stage.", site.Location);

    private static int CountRecordMemberSegments(MutationSiteInfo site, RecordInfo info)
    {
        // 只统计属于目标 record 类型的成员链段数。
        // 例如 t.a.X = 3：chain 包含 X (在 P 上) 和 a (在 tuple 上)，
        // 只有 X 属于 record P → 单段。
        var count = 0;
        foreach (var seg in site.Chain)
        {
            if (seg.Member != null
                && SymbolEqualityComparer.Default.Equals(seg.Member.ContainingType, info.Symbol))
                count++;
        }
        return count;
    }

    private static SiteFailure? MemberIsInitSettable(RecordPlan plan, ISymbol? member)
    {
        if (member == null)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "target member could not be resolved.", Location.None);
        if (plan.MemberRewrites.Any(r => SymbolEqualityComparer.Default.Equals(r.Symbol, member)))
            return null;
        if (member is IPropertySymbol { SetMethod.IsInitOnly: true })
            return null; // 位置参数等编译器合成 init 属性
        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
            $"member '{member.Name}' cannot be set via 'with'.", member.Locations.FirstOrDefault() ?? Location.None);
    }

    /// <summary>MS-1 可写左值 + 无副作用（接收者会被求值两次）。</summary>
    private static SiteFailure? ReceiverWritableAndPure(SemanticModel model, ExpressionSyntax receiver, RecordInfo info)
    {
        switch (receiver)
        {
            case IdentifierNameSyntax id:
                return IdentifierReceiverWritable(model, id);

            case ElementAccessExpressionSyntax element:
                foreach (var arg in element.ArgumentList.Arguments)
                {
                    if (arg.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any())
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                            "indexer argument invokes a method; evaluating it twice would change " +
                            "semantics (MS-1).", element.GetLocation());
                }
                var type = model.GetTypeInfo(element.Expression).Type;
                if (type is IArrayTypeSymbol) return null;
                if (model.GetSymbolInfo(element).Symbol is IPropertySymbol indexer)
                {
                    if (indexer.ContainingNamespace?.ToDisplayString()
                        == "System.Collections.Immutable")
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR054,
                            "read-only/immutable collection element cannot be written (MS-7).",
                            element.GetLocation());
                    if (indexer.SetMethod is not { IsInitOnly: false })
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR054,
                            "collection element is read-only (MS-7).", element.GetLocation());
                    return null;
                }
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                    "element receiver could not be proven a writable indexer (MS-1/MS-7).",
                    element.GetLocation());

            case MemberAccessExpressionSyntax ma:
                // 单段 record 链的接收者不应该是 record 自身的成员访问。
                // 但 t.a（tuple 字段）、obj.Field（非 record 类的字段）等是合法的接收者，
                // 只要该成员不在目标 record 上。
                {
                    var maSymbol = model.GetSymbolInfo(ma).Symbol;
                    if (maSymbol != null && info.Symbol.Equals(maSymbol.ContainingType, SymbolEqualityComparer.Default))
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                            "nested member chains (MS-17) are rewritten by a later stage.",
                            receiver.GetLocation());
                    // 非 record 成员访问：检查可写性与副作用。
                    if (!RecordSyntaxFacts.IsReceiverSideEffectFree(ma))
                        return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                            "receiver expression has side effects (MS-1).", receiver.GetLocation());
                    return CheckMemberAccessWritable(model, ma);
                }

            case InvocationExpressionSyntax:
                // 方法返回值不可写（MS-1：get-only/method return）。
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                    "method return value is not a writable lvalue (MS-1).", receiver.GetLocation());

            default:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                    "receiver expression is not a provably side-effect-free writable lvalue " +
                    "(MS-1).", receiver.GetLocation());
        }
    }

    private static SiteFailure? IdentifierReceiverWritable(SemanticModel model, IdentifierNameSyntax id)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        switch (symbol)
        {
            case ILocalSymbol local:
                if (local.IsForEach)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "foreach iteration variable is read-only (MS-13).", id.GetLocation());
                if (local.RefKind == RefKind.In)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "'ref readonly' local cannot be written (MS-1).", id.GetLocation());
                return null;

            case IParameterSymbol parameter:
                if (parameter.RefKind == RefKind.In)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        "'in' parameter cannot be written (MS-1).", id.GetLocation());
                return null; // 普通值参 / ref / out 形参可写（MS-9）

            case IFieldSymbol field:
                if (field.IsReadOnly)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        $"field '{field.Name}' is read-only (MS-1).", id.GetLocation());
                return null;

            case IPropertySymbol property:
                if (property.SetMethod is not { IsInitOnly: false })
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        $"property '{property.Name}' has no writable set accessor (MS-1).",
                        id.GetLocation());
                return null;

            default:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR051,
                    "receiver is not a writable local, parameter, field, property, or element " +
                    "(MS-1).", id.GetLocation());
        }
    }

    /// <summary>检查非 record 成员访问（如 tuple 字段、普通类字段/属性）是否可写。</summary>
    private static SiteFailure? CheckMemberAccessWritable(SemanticModel model, MemberAccessExpressionSyntax ma)
    {
        var symbol = model.GetSymbolInfo(ma).Symbol;
        switch (symbol)
        {
            case IFieldSymbol field:
                if (field.IsReadOnly)
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        $"field '{field.Name}' is read-only (MS-1).", ma.GetLocation());
                return null;
            case IPropertySymbol property:
                if (property.SetMethod is not { IsInitOnly: false })
                    return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                        $"property '{property.Name}' has no writable set accessor (MS-1).",
                        ma.GetLocation());
                return null;
            default:
                return new SiteFailure(RecordReadOnlyDiagnosticCode.RR050,
                    "member access receiver is not a writable field or property (MS-1).",
                    ma.GetLocation());
        }
    }
}

/// <summary>
/// Phase 3b：把已计划用点改写为 with 赋值。每个用点在计划阶段被唯一归类，
/// 本重写器只处理 ActivePlans 中 SitePlans 命中的节点。
/// </summary>
internal sealed class MutationSiteRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly Dictionary<(int Start, int Kind), SitePlan> _siteKeys = new();

    public MutationSiteRewriter(RewriteContext context)
    {
        _context = context;
        foreach (var plan in context.ActivePlans)
        {
            foreach (var sitePlan in plan.Sites)
            {
                if (!string.Equals(sitePlan.Site.FilePath, context.FilePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var node = sitePlan.Site.WriteNode;
                _siteKeys[(node.SpanStart, node.RawKind)] = sitePlan;
            }
        }
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        var statements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
            statements.AddRange(Expand(statement));
        return visited.WithStatements(SyntaxFactory.List(statements));
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        var statements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
            statements.AddRange(Expand(statement));
        return visited.WithStatements(SyntaxFactory.List(statements));
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
        if (visited.Statement is ExpressionStatementSyntax es && FindSite(es.Expression) != null)
            visited = visited.WithStatement(SyntaxFactory.Block(Expand(es)));
        if (visited.Else?.Statement is ExpressionStatementSyntax elseEs
            && FindSite(elseEs.Expression) != null)
        {
            visited = visited.WithElse(visited.Else!.WithStatement(
                SyntaxFactory.Block(Expand(elseEs))));
        }
        return visited;
    }

    private IEnumerable<StatementSyntax> Expand(StatementSyntax statement)
    {
        if (statement is ExpressionStatementSyntax expressionStatement
            && FindSite(expressionStatement.Expression) is { } sitePlan)
        {
            var rewritten = RewriteWithAssign(expressionStatement.Expression, sitePlan);
            if (rewritten != null)
            {
                _context.Statistics.MutationSitesRewritten++;
                yield return SyntaxFactory.ExpressionStatement(rewritten)
                    .WithLeadingTrivia(expressionStatement.GetLeadingTrivia())
                    .WithTrailingTrivia(expressionStatement.GetTrailingTrivia())
                    .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
                yield break;
            }
        }
        yield return statement;
    }

    private SitePlan? FindSite(ExpressionSyntax expression) =>
        _siteKeys.TryGetValue((expression.SpanStart, expression.RawKind), out var plan) ? plan : null;

    /// <summary>recv.M = rhs  →  recv = recv with { M = rhs }（MS-1）。</summary>
    private ExpressionSyntax? RewriteWithAssign(ExpressionSyntax writeExpression, SitePlan sitePlan)
    {
        if (sitePlan.Action != SiteActionKind.WithAssign) return null;
        if (writeExpression is not AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Left: MemberAccessExpressionSyntax memberAccess,
            } assignment)
        {
            return null;
        }

        var receiverText = sitePlan.Site.Receiver.WithoutTrivia().ToFullString();
        var memberName = memberAccess.Name.Identifier.Text;
        var rhsText = assignment.Right.WithoutTrivia().ToFullString();
        // 用字符串构造完整表达式，确保格式紧凑（formatter 不会展开单行 with 初始化器）。
        var exprText = $"{receiverText} = {receiverText} with {{ {memberName} = {rhsText} }}";
        var result = SyntaxFactory.ParseExpression(exprText);
        return result.WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}
