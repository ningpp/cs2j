using System.Linq;
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
/// Task 12 覆盖 MS-3（复合赋值）/MS-4（自增自减）与 RR052/RR053。
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

            case SiteKind.CompoundAssign:
            case SiteKind.Increment:
                return ClassifyReadModifyWrite(analysis, plan, info, site);

            // 后续任务支持的形态：在落地前整 record 保持原子跳过。
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

    private static SiteFailure? ClassifyReadModifyWrite(
        RecordAnalysis analysis, RecordPlan plan, RecordInfo info, MutationSiteInfo site)
    {
        var model = analysis.Compilation!.GetSemanticModel(site.WriteNode.SyntaxTree)!;

        if (!site.IsStatementPosition)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR053,
                site.Kind == SiteKind.Increment
                    ? "increment/decrement in an expression position consumes the value (MS-4)."
                    : "compound assignment in an expression context cannot be rewritten (MS-16).",
                site.Location);

        if (CountRecordMemberSegments(site, info) != 1)
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR020,
                "nested member chains (MS-17) are rewritten by a later stage.", site.Location);

        if (MemberIsInitSettable(plan, site.Member) is { } memberFailure)
            return new SiteFailure(memberFailure.Code, memberFailure.Reason, site.Location);

        if (!GetterIsEffectivelyPure(site.Member))
            return new SiteFailure(RecordReadOnlyDiagnosticCode.RR052,
                "compound/increment rewrite reads the member getter once; the getter is not a " +
                "provably pure auto/field accessor (MS-3/MS-4).", site.Location);

        return ReceiverWritableAndPure(model, site.Receiver, info);
    }

    /// <summary>
    /// 字段读取天然纯；自动属性（含 record 位置属性 / 手写 { get; set; }）的 getter
    /// 只读 backing field，视为纯。Roslyn 中手写 auto-property 的 GetMethod
    /// IsImplicitlyDeclared 为 false（因为有显式 get; 访问器语法节点），
    /// 但其访问器节点没有 Body / ExpressionBody —— 以此判定为自动属性。
    /// </summary>
    private static bool GetterIsEffectivelyPure(ISymbol? member)
    {
        if (member is IFieldSymbol) return true;
        if (member is not IPropertySymbol { GetMethod: { } getter }) return false;
        // 位置参数等编译器合成的 getter：无语法引用 → 一定是 auto。
        if (getter.IsImplicitlyDeclared) return true;
        // 手写 auto-property：get; 访问器语法节点存在但无 Body/ExpressionBody。
        if (getter.DeclaringSyntaxReferences.Length == 0) return true;
        foreach (var reference in getter.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is AccessorDeclarationSyntax accessor
                && accessor.Body == null
                && accessor.ExpressionBody == null)
            {
                return true;
            }
        }
        return false;
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
        return visited.WithStatements(SyntaxFactory.List(ProcessStatements(visited.Statements)));
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        return visited.WithStatements(SyntaxFactory.List(ProcessStatements(visited.Statements)));
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

    // ----- MS-2 adjacent merge -----

    private List<StatementSyntax> ProcessStatements(SyntaxList<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>();
        for (var i = 0; i < statements.Count;)
        {
            if (TryCoalesce(statements, i, out var merged, out var consumed))
            {
                result.Add(merged!);
                i += consumed;
            }
            else
            {
                result.AddRange(Expand(statements[i]));
                i++;
            }
        }
        return result;
    }

    private bool TryCoalesce(
        SyntaxList<StatementSyntax> statements,
        int start,
        out StatementSyntax? merged,
        out int consumed)
    {
        merged = null;
        consumed = 0;

        var run = new List<(ExpressionStatementSyntax Stmt, SitePlan Plan,
            MutationSiteInfo Site, string Receiver, string Member)>();
        var membersInRun = new HashSet<string>(StringComparer.Ordinal);

        for (var i = start; i < statements.Count; i++)
        {
            if (statements[i] is not ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                        Left: MemberAccessExpressionSyntax,
                    } assign
                } stmt
                || FindSite(stmt.Expression) is not { } plan
                || plan.Site.Kind != SiteKind.SimpleAssign
                || CountSegmentsForRewrite(plan.Site.Left) != 1)
            {
                break;
            }

            var receiverText = plan.Site.Receiver.WithoutTrivia().ToFullString();
            var memberName = ((MemberAccessExpressionSyntax)assign.Left).Name.Identifier.Text;
            if (run.Count > 0 && receiverText != run[0].Receiver) break;
            if (!membersInRun.Add(memberName)) break;

            // MS-2 语义前提：本句右值不得读取接收者上本轮已改写的成员。
            if (ReadsPreviouslyWrittenMember(plan.Site, assign.Right, run))
                break;

            run.Add((stmt, plan, plan.Site, receiverText, memberName));
        }

        if (run.Count < 2) return false;

        // 用字符串构造完整表达式，确保格式紧凑（formatter 不会展开单行 with 初始化器）。
        // 不加 RewriteAnnotation：formatter 对多成员 with-initializer 会展开成多行，
        // 而我们需要保持单行。不加注解则 formatter 跳过此节点，保留 parse 时的紧凑格式。
        var recvText = run[0].Receiver;
        var memberAssigns = run.Select(item =>
            $"{item.Member} = {((AssignmentExpressionSyntax)item.Stmt.Expression).Right.WithoutTrivia().ToFullString()}");
        var exprText = $"{recvText} = {recvText} with {{ {string.Join(", ", memberAssigns)} }}";
        var result = SyntaxFactory.ParseExpression(exprText);
        merged = SyntaxFactory.ExpressionStatement(result)
            .WithLeadingTrivia(run[0].Stmt.GetLeadingTrivia())
            .WithTrailingTrivia(run[^1].Stmt.GetTrailingTrivia());
        _context.Statistics.MutationSitesRewritten += run.Count;
        consumed = run.Count;
        return true;
    }

    private static int CountSegmentsForRewrite(ExpressionSyntax left)
    {
        var count = 0;
        ExpressionSyntax? cursor = left;
        while (cursor is MemberAccessExpressionSyntax ma)
        {
            count++;
            cursor = ma.Expression;
        }
        return count;
    }

    private bool ReadsPreviouslyWrittenMember(
        MutationSiteInfo currentSite,
        ExpressionSyntax right,
        List<(ExpressionStatementSyntax Stmt, SitePlan Plan, MutationSiteInfo Site,
            string Receiver, string Member)> run)
    {
        var model = _context.Model;
        var writtenSymbols = run
            .Select(item => item.Site.Member)
            .Where(s => s != null)
            .ToHashSet(SymbolEqualityComparer.Default);
        var receiverSymbol = currentSite.Receiver is IdentifierNameSyntax rid
            ? model.GetSymbolInfo(rid).Symbol : null;

        foreach (var access in right.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            if (model.GetSymbolInfo(access).Symbol is not { } accessed) continue;
            if (!writtenSymbols.Contains(accessed)) continue;
            // 确认读的是同一个接收者（标识符场景；元素/字段接收者保守不合并）。
            if (receiverSymbol == null) return true;
            if (access.Expression is IdentifierNameSyntax inner
                && SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(inner).Symbol, receiverSymbol))
                return true;
        }
        return false;
    }

    // ----- single-statement expansion -----

    private IEnumerable<StatementSyntax> Expand(StatementSyntax statement)
    {
        if (statement is ExpressionStatementSyntax expressionStatement
            && FindSite(expressionStatement.Expression) is { } sitePlan)
        {
            var rewritten = RewriteSite(expressionStatement.Expression, sitePlan);
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

    // ----- per-kind rewrite dispatch -----

    private ExpressionSyntax? RewriteSite(ExpressionSyntax writeExpression, SitePlan sitePlan)
    {
        return sitePlan.Site.Kind switch
        {
            SiteKind.SimpleAssign => RewriteWithAssign(writeExpression, sitePlan),
            SiteKind.CompoundAssign => RewriteCompound(writeExpression, sitePlan.Site),
            SiteKind.Increment => RewriteIncrement(writeExpression, sitePlan.Site),
            _ => null,
        };
    }

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

    private ExpressionSyntax? RewriteCompound(
        ExpressionSyntax writeExpression, MutationSiteInfo site)
    {
        if (writeExpression is not AssignmentExpressionSyntax
            {
                Left: MemberAccessExpressionSyntax memberAccess,
            } assignment)
        {
            return null;
        }

        var receiverText = site.Receiver.WithoutTrivia().ToFullString();
        var memberName = memberAccess.Name.Identifier.Text;
        var read = SyntaxFactory.ParseExpression($"{receiverText}.{memberName}");

        ExpressionSyntax readModify;
        if (assignment.Kind() == SyntaxKind.CoalesceAssignmentExpression)
        {
            readModify = SyntaxFactory.BinaryExpression(
                SyntaxKind.CoalesceExpression, read, assignment.Right);
        }
        else
        {
            readModify = SyntaxFactory.BinaryExpression(
                BinaryKindFor(assignment.Kind()), read,
                BinaryTokenFor(assignment.Kind()), assignment.Right);
        }

        return BuildWithWriteBack(receiverText, memberName, readModify);
    }

    private ExpressionSyntax? RewriteIncrement(
        ExpressionSyntax writeExpression, MutationSiteInfo site)
    {
        if (writeExpression is not (PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax))
            return null;
        if (site.Left is not MemberAccessExpressionSyntax memberAccess) return null;

        var receiverText = site.Receiver.WithoutTrivia().ToFullString();
        var memberName = memberAccess.Name.Identifier.Text;
        var read = SyntaxFactory.ParseExpression($"{receiverText}.{memberName}");
        var one = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
        var decrement = writeExpression.Kind()
            is SyntaxKind.PostDecrementExpression or SyntaxKind.PreDecrementExpression;
        var calc = decrement
            ? SyntaxFactory.BinaryExpression(SyntaxKind.SubtractExpression, read,
                SyntaxFactory.Token(SyntaxKind.MinusToken), one)
            : SyntaxFactory.BinaryExpression(SyntaxKind.AddExpression, read,
                SyntaxFactory.Token(SyntaxKind.PlusToken), one);

        return BuildWithWriteBack(receiverText, memberName, calc);
    }

    private static ExpressionSyntax BuildWithWriteBack(
        string receiverText, string memberName, ExpressionSyntax value)
    {
        var rhsText = value.WithoutTrivia().ToFullString();
        var exprText = $"{receiverText} = {receiverText} with {{ {memberName} = {rhsText} }}";
        return SyntaxFactory.ParseExpression(exprText)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }

    private static SyntaxKind BinaryKindFor(SyntaxKind assignment) => assignment switch
    {
        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
        SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
        SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
        SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
        SyntaxKind.UnsignedRightShiftAssignmentExpression => SyntaxKind.UnsignedRightShiftExpression,
        _ => throw new InvalidOperationException($"Unsupported compound assignment {assignment}."),
    };

    private static SyntaxToken BinaryTokenFor(SyntaxKind assignment) =>
        SyntaxFactory.Token(BinaryKindFor(assignment) switch
        {
            SyntaxKind.AddExpression => SyntaxKind.PlusToken,
            SyntaxKind.SubtractExpression => SyntaxKind.MinusToken,
            SyntaxKind.MultiplyExpression => SyntaxKind.AsteriskToken,
            SyntaxKind.DivideExpression => SyntaxKind.SlashToken,
            SyntaxKind.ModuloExpression => SyntaxKind.PercentToken,
            SyntaxKind.BitwiseAndExpression => SyntaxKind.AmpersandToken,
            SyntaxKind.BitwiseOrExpression => SyntaxKind.BarToken,
            SyntaxKind.ExclusiveOrExpression => SyntaxKind.CaretToken,
            SyntaxKind.LeftShiftExpression => SyntaxKind.LessThanLessThanToken,
            SyntaxKind.RightShiftExpression => SyntaxKind.GreaterThanGreaterThanToken,
            SyntaxKind.UnsignedRightShiftExpression => SyntaxKind.GreaterThanGreaterThanGreaterThanToken,
            _ => throw new InvalidOperationException(),
        });
}
