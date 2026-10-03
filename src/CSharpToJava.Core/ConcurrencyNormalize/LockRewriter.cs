using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>
/// CSharpSyntaxRewriter：CN-2 逆向归一（Enter/Exit → lock，删除独占 bool 声明，BODY
/// 原样入锁块）、CN-3 TryEnter 规范形（统一 3 参 int 毫秒 + lockTaken + try/finally
/// 守卫；TimeSpan→__cs2j_to int 毫秒临时量；0ms 补 0；裸 bool 形分支结构保留）、
/// CN-4 Wait 重载归一（TimeSpan→int 毫秒临时量转 int 重载；exitContext:false 折叠）。
/// 合成临时量按方法级已用标识符确定性命名（碰撞加序号）；改动节点统一挂
/// <see cref="ConcurrencySyntaxFacts.RewriteAnnotation"/>；只接受语义绑定命中的单元
/// （Analyzer 产出），span 不做任何文本匹配。
/// </summary>
internal sealed class LockRewriter : CSharpSyntaxRewriter
{
    private readonly ConcurrencyOptions _options;
    private readonly ConcurrencyStatistics _statistics;
    private readonly List<ConcurrencyDiagnostic> _diagnostics;
    private readonly string _filePath;
    private readonly List<EnterExitPairUnit> _pairUnits = new();
    private readonly List<TryEnterUnit> _tryEnterUnits = new();
    private readonly List<WaitUnit> _waitUnits = new();

    /// <summary>plan Id → (CN-2 对数, CN-3 站点数, CN-4 站点数)，CN001 Info 用。</summary>
    public Dictionary<string, (int Pairs, int TryEnters, int Waits)> RewriteCountsByPlan { get; }
        = new(StringComparer.Ordinal);

    public LockRewriter(
        ConcurrencyOptions options,
        IReadOnlyList<ConcurrencyRegionPlan> filePlans,
        string filePath,
        ConcurrencyStatistics statistics,
        List<ConcurrencyDiagnostic> diagnostics)
    {
        _options = options;
        _filePath = filePath;
        _statistics = statistics;
        _diagnostics = diagnostics;
        foreach (var plan in filePlans)
        {
            _pairUnits.AddRange(plan.PairUnits);
            _tryEnterUnits.AddRange(plan.TryEnterUnits);
            _waitUnits.AddRange(plan.WaitUnits);
            RewriteCountsByPlan.TryAdd(plan.Id, (0, 0, 0));
        }
    }

    // ----- 语句级入口 -----

    /// <summary>
    /// 块级 walker：语句列表位置消费 CN-2/CN-3 单元（需平铺多条语句输出）；
    /// 其余语句交给 <see cref="Visit"/>（嵌入位置的 CN-3 兜底与 CN-4 在那里处理）。
    /// 单元一律按锚语句节点身份匹配（未改动子树保持节点同一性）。
    /// </summary>
    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var rewritten = RewriteStatementList(node.Statements);
        return node.WithStatements(SyntaxFactory.List(rewritten));
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is StatementSyntax statement && statement is not BlockSyntax)
        {
            // CN-3 裸 bool if：语句列表位置由 walker 消费；此处兜底嵌入/顶层位置
            //（只能返回单语句 → 多语句包成块）。
            var tryEnterUnit = _tryEnterUnits.FirstOrDefault(u => !u.Handled
                && ReferenceEquals(u.AnchorStatement, statement));
            if (tryEnterUnit != null)
            {
                tryEnterUnit.Handled = true;
                var canonical = tryEnterUnit.Feasible
                    && tryEnterUnit.Plan.PairClassActive
                    && _options.CanonicalizeTryEnter
                    && tryEnterUnit.IsBareBoolIf;
                if (canonical)
                {
                    var statements = BuildTryEnterCanonical(tryEnterUnit);
                    tryEnterUnit.Rewritten = true;
                    OnTryEnterCanonicalized(tryEnterUnit);
                    return statements.Count == 1
                        ? statements[0]
                        : (SyntaxNode)SyntaxFactory.Block(statements);
                }
            }

            // CN-4 Wait 归一：单站点独立（与配对类原子性无关）。
            var waits = _waitUnits.Where(u => !u.Handled
                && ReferenceEquals(u.AnchorStatement, statement)).ToList();
            if (waits.Count > 0)
            {
                foreach (var wait in waits) wait.Handled = true;
                var (declarations, rewritten) = BuildWaitRewrite(statement, waits);
                if (declarations.Count > 0)
                {
                    var wrapped = new List<StatementSyntax>(declarations) { rewritten };
                    return SyntaxFactory.Block(wrapped)
                        .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation);
                }
                return rewritten.WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation);
            }
        }
        return base.Visit(node);
    }

    private List<StatementSyntax> RewriteStatementList(SyntaxList<StatementSyntax> statements)
        => RewriteStatementList((IReadOnlyList<StatementSyntax>)statements);

    private List<StatementSyntax> RewriteStatementList(IReadOnlyList<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>(statements.Count);
        var index = 0;
        while (index < statements.Count)
        {
            var statement = statements[index];

            // CN-2：Enter/Exit 配对 → lock（BODY 原样入锁块）。
            var pair = _pairUnits.FirstOrDefault(u => !u.Handled
                && ReferenceEquals(u.AnchorStatement, statement));
            if (pair != null)
            {
                pair.Handled = true;
                if (pair.Feasible && pair.Plan.PairClassActive && _options.RewriteEnterExitPairs)
                {
                    result.Add(BuildLock(pair));
                    pair.Rewritten = true;
                    _statistics.EnterExitPairsRewritten++;
                    CountRewrite(pair.Plan, pairs: 1);
                }
                else
                {
                    for (var k = 0; k < pair.ConsumedStatements && index + k < statements.Count; k++)
                        result.Add(VisitStatementSafe(statements[index + k]));
                }
                index += Math.Min(pair.ConsumedStatements, statements.Count - index);
                continue;
            }

            // CN-3：TryEnter 规范形（列表位置平铺）。
            var tryEnter = _tryEnterUnits.FirstOrDefault(u => !u.Handled
                && ReferenceEquals(u.AnchorStatement, statement));
            if (tryEnter != null)
            {
                tryEnter.Handled = true;
                var canonical = tryEnter.Feasible
                    && tryEnter.Plan.PairClassActive
                    && _options.CanonicalizeTryEnter
                    && !tryEnter.IsCanonicalThreeArg;
                if (canonical && tryEnter.IsBareBoolIf)
                {
                    result.AddRange(BuildTryEnterCanonical(tryEnter));
                    tryEnter.Rewritten = true;
                    OnTryEnterCanonicalized(tryEnter);
                }
                else if (canonical)
                {
                    // ref 语句形：替换调用语句（2 参补 0 / 3 参 TimeSpan→int 临时量），
                    // taken 声明与 try/finally 结构原样保留。
                    var pendingTemps = BuildTryEnterCall(tryEnter, out var newCallStatement);
                    result.AddRange(pendingTemps);
                    for (var k = 0; k < tryEnter.ConsumedStatements && index + k < statements.Count; k++)
                    {
                        var consumed = statements[index + k];
                        result.Add(ReferenceEquals(consumed, tryEnter.CallStatement)
                            ? newCallStatement
                            : VisitStatementSafe(consumed));
                    }
                    tryEnter.Rewritten = true;
                    OnTryEnterCanonicalized(tryEnter);
                }
                else
                {
                    // 已规范形或不可改写：原样深访问全部被消费语句。
                    for (var k = 0; k < tryEnter.ConsumedStatements && index + k < statements.Count; k++)
                        result.Add(VisitStatementSafe(statements[index + k]));
                }
                index += Math.Min(tryEnter.ConsumedStatements, statements.Count - index);
                continue;
            }

            result.Add(VisitStatementSafe(statement));
            index++;
        }
        return result;
    }

    private StatementSyntax VisitStatementSafe(StatementSyntax statement)
        => (StatementSyntax)Visit(statement)!;

    // ----- CN-2：Enter/Exit → lock -----

    private StatementSyntax BuildLock(EnterExitPairUnit pair)
    {
        var body = RewriteStatementList(pair.BodyStatements);
        var block = SyntaxFactory.Block(SyntaxFactory.List(body));
        return SyntaxFactory.LockStatement(pair.LockExpression, block)
            .WithLeadingTrivia(pair.AnchorStatement.GetLeadingTrivia())
            .WithTrailingTrivia(pair.TryStatement.GetTrailingTrivia())
            .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation);
    }

    // ----- CN-3：TryEnter 规范形 -----

    /// <summary>
    /// 裸 bool 形 → 规范形：[int __cs2j_to = (int)ts.TotalMilliseconds;]
    /// bool __cs2j_mt = false; Monitor.TryEnter(o, ms, ref __cs2j_mt);
    /// try { if (__cs2j_mt) WORK else ELSE } finally { if (__cs2j_mt) Exit(o); }。
    /// 0ms 形补 0；else 超时分支显式保留。
    /// </summary>
    private List<StatementSyntax> BuildTryEnterCanonical(TryEnterUnit unit)
    {
        var result = new List<StatementSyntax>();
        var plan = unit.Plan;

        ExpressionSyntax timeoutArgument;
        if (unit.TimeoutArgument == null)
        {
            timeoutArgument = ZeroLiteral();
        }
        else if (unit.TimeoutIsTimeSpan)
        {
            var tempName = GetTempName(plan, ConcurrencySyntaxFacts.TempTimeoutName);
            result.Add(ParseStatement($"int {tempName} = (int){unit.TimeoutArgument}.TotalMilliseconds;")
                .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation));
            timeoutArgument = SyntaxFactory.IdentifierName(tempName);
        }
        else
        {
            timeoutArgument = unit.TimeoutArgument!;
        }

        var takenName = GetTempName(plan, ConcurrencySyntaxFacts.TempTakenName);
        result.Add(ParseStatement($"bool {takenName} = false;")
            .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation));

        var takenArgument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(takenName))
            .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space));
        var callStatement = SyntaxFactory.ExpressionStatement(
            RebuildInvocation(unit.Invocation, new[] { unit.Invocation.ArgumentList.Arguments[0], SyntaxFactory.Argument(timeoutArgument), takenArgument }))
            .WithLeadingTrivia(unit.IfStatement!.GetLeadingTrivia())
            .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation);
        result.Add(callStatement);

        // WORK：原 try 块语句（深访问以处理嵌套单元）。
        var body = RewriteStatementList(unit.GuardedBodyStatements!);
        IfStatementSyntax innerIf = SyntaxFactory.IfStatement(
            SyntaxFactory.IdentifierName(takenName),
            SyntaxFactory.Block(SyntaxFactory.List(body)));
        if (unit.ElseStatement != null)
        {
            innerIf = innerIf.WithElse(SyntaxFactory.ElseClause(
                (StatementSyntax)Visit(unit.ElseStatement)!));
        }

        var exitGuard = SyntaxFactory.IfStatement(
            SyntaxFactory.IdentifierName(takenName),
            SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(unit.ExitInvocation!)));
        var tryStatement = SyntaxFactory.TryStatement(
            SyntaxFactory.Block(innerIf),
            SyntaxFactory.List<CatchClauseSyntax>(),
            SyntaxFactory.FinallyClause(SyntaxFactory.Block(exitGuard)));
        result.Add(tryStatement
            .WithTrailingTrivia(unit.IfStatement.GetTrailingTrivia())
            .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation));
        return result;
    }

    /// <summary>
    /// ref 语句形调用语句替换：2 参 (o, ref t) 补 0；3 参 (o, TimeSpan, ref t) 提升
    /// int 毫秒临时量后转 int。返回前置临时量声明列表与新调用语句。
    /// </summary>
    private List<StatementSyntax> BuildTryEnterCall(TryEnterUnit unit, out StatementSyntax newCallStatement)
    {
        var pendingTemps = new List<StatementSyntax>();
        var originalArguments = unit.Invocation.ArgumentList.Arguments;
        var newArguments = new List<ArgumentSyntax>();

        if (unit.TimeoutArgument == null)
        {
            // (o, ref t) → (o, 0, ref t)
            newArguments.Add(originalArguments[0]);
            newArguments.Add(SyntaxFactory.Argument(ZeroLiteral()));
            newArguments.Add(originalArguments[1]);
        }
        else
        {
            // (o, TimeSpan, ref t) → (o, __cs2j_to, ref t)
            var tempName = GetTempName(unit.Plan, ConcurrencySyntaxFacts.TempTimeoutName);
            pendingTemps.Add(ParseStatement($"int {tempName} = (int){unit.TimeoutArgument}.TotalMilliseconds;")
                .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation));
            newArguments.Add(originalArguments[0]);
            newArguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(tempName)));
            newArguments.Add(originalArguments[2]);
        }

        newCallStatement = SyntaxFactory.ExpressionStatement(
                RebuildInvocation(unit.Invocation, newArguments))
            .WithLeadingTrivia(unit.CallStatement!.GetLeadingTrivia())
            .WithTrailingTrivia(unit.CallStatement.GetTrailingTrivia())
            .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation);
        return pendingTemps;
    }

    private static InvocationExpressionSyntax RebuildInvocation(
        InvocationExpressionSyntax invocation,
        IEnumerable<ArgumentSyntax> arguments)
        => invocation.WithArgumentList(SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(arguments)));

    private static LiteralExpressionSyntax ZeroLiteral()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(0));

    private void OnTryEnterCanonicalized(TryEnterUnit unit)
    {
        _statistics.TryEnterSitesCanonicalized++;
        CountRewrite(unit.Plan, tryEnters: 1);
        var lineSpan = unit.Invocation.GetLocation().GetLineSpan().StartLinePosition;
        _diagnostics.Add(new ConcurrencyDiagnostic(
            ConcurrencySeverity.Warning,
            ConcurrencyRule.CN3,
            ConcurrencyDiagnosticCodes.TryEnterGate,
            "TryEnter site canonicalized to the explicit 3-arg int-milliseconds + " +
            "lockTaken form; Java intrinsic locks have no tryLock and the compat bridge " +
            "is not ready — do not wire this site to an always-succeed stub.",
            _filePath,
            lineSpan.Line + 1,
            lineSpan.Character + 1,
            unit.Plan.MethodQualifiedName));
    }

    // ----- CN-4：Wait 重载归一 -----

    private (List<StatementSyntax> Declarations, StatementSyntax Rewritten) BuildWaitRewrite(
        StatementSyntax statement,
        List<WaitUnit> waits)
    {
        var declarations = new List<StatementSyntax>();
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var wait in waits)
        {
            if (wait.SkipRewrite)
            {
                // CS2J2921：exitContext true/变量 → 站点原样（诊断在 Analyzer 已发）。
                continue;
            }

            var arguments = wait.Invocation.ArgumentList.Arguments;
            var newArguments = arguments.ToList();
            var changed = false;

            if (wait.TimeSpanArgument != null)
            {
                var tempName = GetTempName(wait.Plan, ConcurrencySyntaxFacts.TempTimeoutName);
                declarations.Add(ParseStatement(
                        $"int {tempName} = (int){wait.TimeSpanArgument}.TotalMilliseconds;")
                    .WithAdditionalAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation));
                newArguments[1] = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(tempName));
                changed = true;
            }

            if (wait.ExitContextArgumentIndex >= 0 && wait.ExitContextFoldable)
            {
                // 常量 false 折叠 → 去掉 exitContext 实参（转 int/TimeSpan 2 参重载）。
                newArguments.RemoveAt(wait.ExitContextArgumentIndex);
                changed = true;
            }

            if (changed)
            {
                replacements[wait.Invocation.ArgumentList] = SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(newArguments));
                _statistics.WaitSitesCanonicalized++;
                CountRewrite(wait.Plan, waits: 1);
            }
        }

        var rewritten = statement;
        if (replacements.Count > 0)
        {
            rewritten = statement.ReplaceNodes(
                replacements.Keys,
                (original, _) => replacements[original]);
        }
        rewritten = (StatementSyntax)Visit(rewritten)!;
        return (declarations, rewritten);
    }

    // ----- 统计收尾与临时量命名 -----

    /// <summary>Visit 完成后调用：检测/分桶/拒绝计数一次性落账（Rewritten 在改写点累加）。</summary>
    public void FinalizeCounters()
    {
        foreach (var unit in _pairUnits)
        {
            _statistics.EnterExitPairsDetected++;
            if (!unit.Rewritten) _statistics.EnterExitPairsRejected++;
        }

        foreach (var unit in _tryEnterUnits)
        {
            _statistics.TryEnterSitesDetected++;
            if (unit.IsTimed) _statistics.TryEnterTimed++;
            if (unit.IsZeroTimeout) _statistics.TryEnterZeroTimeout++;
            if (unit.IsLockTaken) _statistics.TryEnterLockTaken++;
            if (unit.IsBareBool) _statistics.TryEnterBareBool++;
            // 非规范/plan 被阻塞/选项关闭的站点留原样（2910 痕迹由 Analyzer 发诊断时累计）。
        }
    }

    private void CountRewrite(ConcurrencyRegionPlan plan, int pairs = 0, int tryEnters = 0, int waits = 0)
    {
        var current = RewriteCountsByPlan.TryGetValue(plan.Id, out var value)
            ? value
            : (Pairs: 0, TryEnters: 0, Waits: 0);
        RewriteCountsByPlan[plan.Id] = (current.Pairs + pairs, current.TryEnters + tryEnters, current.Waits + waits);
    }

    /// <summary>合成临时量确定性命名：基名未占用即用，占用则加序号（__cs2j_mt2…）。</summary>
    private static string GetTempName(ConcurrencyRegionPlan plan, string baseName)
    {
        var used = plan.UsedIdentifiers;
        if (!used.Contains(baseName))
        {
            used.Add(baseName);
            return baseName;
        }
        for (var sequence = 2; ; sequence++)
        {
            var candidate = baseName + sequence.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate))
            {
                used.Add(candidate);
                return candidate;
            }
        }
    }

    private static StatementSyntax ParseStatement(string text)
        => SyntaxFactory.ParseStatement(text, options: new CSharpParseOptions(LanguageVersion.Latest));
}
