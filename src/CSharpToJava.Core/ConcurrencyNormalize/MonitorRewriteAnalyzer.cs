using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>
/// 整树一次语义分析（设计 §4.1/§4.3）：Compilation.GetTypeByMetadataName 取
/// System.Threading.Monitor / System.Threading.Volatile 符号；调用站点一律
/// GetSymbolInfo + IMethodSymbol 身份判定，禁止文本匹配。逐方法建 region plan：
/// lock 区域枚举、Enter/Exit 配对（CN-2 形 A/形 B）、TryEnter/Wait/Pulse 站点分类、
/// 锁对象符号绑定、持锁所有权判定（词法-保守）、锁对象身份审计、volatile 字段/
/// 静态 API/范围外类型计数。产出确定性排序 plan。
/// </summary>
internal sealed class MonitorRewriteAnalyzer
{
    private static readonly HashSet<string> OutOfScopeTypeNames = new(StringComparer.Ordinal)
    {
        "System.Threading.Mutex",
        "System.Threading.Semaphore",
        "System.Threading.SemaphoreSlim",
        "System.Threading.WaitHandle",
        "System.Threading.AutoResetEvent",
        "System.Threading.ManualResetEvent",
        "System.Threading.ManualResetEventSlim",
        "System.Threading.EventWaitHandle",
        "System.Threading.Barrier",
        "System.Threading.CountdownEvent",
        "System.Threading.SpinLock",
        "System.Threading.SpinWait",
        "System.Threading.ReaderWriterLock",
        "System.Threading.ReaderWriterLockSlim",
        "System.Threading.Tasks.TaskCompletionSource",
    };

    private readonly ConcurrencyOptions _options;
    private readonly ConcurrencyAnalysis _analysis = new();
    private readonly Dictionary<IMethodSymbol, ConcurrencyRegionPlan> _plansByMethod =
        new(SymbolEqualityComparer.Default);
    private readonly HashSet<InvocationExpressionSyntax> _consumedExits = new();
    private readonly List<(string Code, string File, TextSpan Span)> _baselineErrors = new();
    private INamedTypeSymbol? _monitorType;
    private INamedTypeSymbol? _volatileType;

    public MonitorRewriteAnalyzer(ConcurrencyOptions options)
    {
        _options = options;
    }

    public ConcurrencyAnalysis Analyze(CSharpCompilation compilation)
    {
        _monitorType = compilation.GetTypeByMetadataName("System.Threading.Monitor");
        _volatileType = compilation.GetTypeByMetadataName("System.Threading.Volatile");
        CollectBaselineErrors(compilation);

        foreach (var tree in compilation.SyntaxTrees
            .OrderBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            // 性能：绝多数文件不含任何并发原语。先做轻量级文本预筛——
        // 仅当文件含 Monitor/lock/Volatile/Mutex/Semaphore/EventWait/SpinLock/
        // ReaderWriter/Barrier/Countdown/TaskCompletionSource/Thread.Join 等
        // 关键字时才进入语义分析（画像 2026-10-18：933 文件中仅 ~70 个含并发原语，
        // 跳过 863 个文件的 DescendantNodes+GetSymbolInfo ≈ 节省 ~20s）。
        var text = tree.ToString();
        if (!HasConcurrencyKeyword(text))
            continue;

            AnalyzeTree(tree, compilation.GetSemanticModel(tree));
        }

        FinalizePlans();
        return _analysis;
    }

    /// <summary>文本级预筛：文件是否可能含任何并发原语站点。
    /// 关键字覆盖 Monitor/lock/volatile + 全部 OutOfScopeTypeNames 类型名 + Thread.Join。
    /// C# 中这些类型/关键字只能以标识符形式出现，文本级检查零漏报。</summary>
    private static bool HasConcurrencyKeyword(string text)
    {
        // 'lock' 和 'volatile' 是 C# 关键字——出现即需语义分析。
        // 'Monitor'/'Volatile' 是类型名——需进一步绑定判定。
        // OutOfScope 类型名从 OutOfScopeTypeNames 集合取短名。
        // 'Thread' 用于 Thread.Join 检测。
        return text.Contains("Monitor", StringComparison.Ordinal)
            || text.Contains("lock", StringComparison.Ordinal)
            || text.Contains("Volatile", StringComparison.Ordinal)
            || text.Contains("Mutex", StringComparison.Ordinal)
            || text.Contains("Semaphore", StringComparison.Ordinal)
            || text.Contains("WaitHandle", StringComparison.Ordinal)
            || text.Contains("ResetEvent", StringComparison.Ordinal)
            || text.Contains("SpinLock", StringComparison.Ordinal)
            || text.Contains("SpinWait", StringComparison.Ordinal)
            || text.Contains("ReaderWriter", StringComparison.Ordinal)
            || text.Contains("Barrier", StringComparison.Ordinal)
            || text.Contains("CountdownEvent", StringComparison.Ordinal)
            || text.Contains("TaskCompletionSource", StringComparison.Ordinal)
            || text.Contains("Thread", StringComparison.Ordinal);
    }

    // ----- 整树遍历 -----

    private void AnalyzeTree(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        var filePath = tree.FilePath;

        // 性能：文件级文本预筛已跳过绝多数不相关文件（HasConcurrencyKeyword）。
        // 此处对通过预筛的文件做完整语义分析。不再做 invocation 级文本预筛，
        // 因为变量名可能不含类型名（如 sem.Wait() 绑定 SemaphoreSlim.Wait）。
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) continue;
            var containingType = method.OriginalDefinition?.ContainingType;
            if (containingType != null && _monitorType != null
                && SymbolEqualityComparer.Default.Equals(containingType, _monitorType))
            {
                ClassifyMonitorCall(invocation, method, model, filePath);
            }
            else if (VolatileRewriter.IsVolatileStaticApiMethod(method, _volatileType))
            {
                RecordVolatileStaticApiSite(invocation, model, filePath);
            }
            else
            {
                RecordOutOfScopeSite(invocation, method, model, filePath);
            }
        }

        // 性能：ObjectCreation 预筛——类型名不在 OutOfScope 集合中的跳过 GetTypeInfo。
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeText = creation.Type?.ToString();
            if (typeText == null
                || !IsPotentialOutOfScopeExpression(typeText))
                continue;
            var kind = MatchOutOfScopeType(model.GetTypeInfo(creation).Type);
            if (kind != null) RecordOutOfScope(kind, creation.GetLocation(), filePath);
        }

        foreach (var lockStatement in root.DescendantNodes().OfType<LockStatementSyntax>())
        {
            AuditLockStatement(lockStatement, model, filePath);
        }

        foreach (var fieldDeclaration in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            AuditVolatileField(fieldDeclaration, model, filePath);
        }
    }

    /// <summary>表达式文本是否可能是 OutOfScope 类型。
    /// 检查 OutOfScopeTypeNames 中所有短名是否出现在表达式中。</summary>
    private static bool IsPotentialOutOfScopeExpression(string exprText)
    {
        // 短名提取自 OutOfScopeTypeNames（System.Threading.* 命名空间下）。
        // 只需检查短名部分——表达式如 'new Mutex()' 或 'SemaphoreSlim(...)' 会含短名。
        return exprText.Contains("Mutex", StringComparison.Ordinal)
            || exprText.Contains("Semaphore", StringComparison.Ordinal)
            || exprText.Contains("WaitHandle", StringComparison.Ordinal)
            || exprText.Contains("ResetEvent", StringComparison.Ordinal)
            || exprText.Contains("SpinLock", StringComparison.Ordinal)
            || exprText.Contains("SpinWait", StringComparison.Ordinal)
            || exprText.Contains("ReaderWriter", StringComparison.Ordinal)
            || exprText.Contains("Barrier", StringComparison.Ordinal)
            || exprText.Contains("CountdownEvent", StringComparison.Ordinal)
            || exprText.Contains("TaskCompletionSource", StringComparison.Ordinal)
            || exprText.Contains("Thread", StringComparison.Ordinal)
            || exprText.Contains("Channel", StringComparison.Ordinal);
    }

    private void CollectBaselineErrors(CSharpCompilation compilation)
    {
        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error
                || diagnostic.Location.SourceTree == null) continue;
            var code = diagnostic.Descriptor.Id;
            if (code is "CS1996" or "CS0185" or "CS0677")
            {
                _baselineErrors.Add((
                    code,
                    diagnostic.Location.SourceTree.FilePath,
                    diagnostic.Location.SourceSpan));
            }
        }
    }

    private bool BaselineContains(string code, string filePath, TextSpan outer)
        => _baselineErrors.Any(e =>
            e.Code == code
            && string.Equals(e.File, filePath, StringComparison.OrdinalIgnoreCase)
            && e.Span.Start >= outer.Start
            && e.Span.End <= outer.End);

    // ----- Monitor 调用分类 -----

    private void ClassifyMonitorCall(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        string filePath)
    {
        var kind = ClassifyCallKind(method);
        if (kind == null) return;

        var methodSymbol = EnclosingOrdinaryMethod(model, invocation.SpanStart);
        if (methodSymbol == null)
        {
            // 字段初始化器等无法归属普通方法的站点：显式 2910（残余闸门白名单依据）。
            EmitNonCanonical(
                invocation,
                plan: null,
                filePath,
                ConcurrencyRule.CN2,
                "the Monitor call is outside an ordinary method body and cannot be " +
                "attributed to a method-level region plan.");
            return;
        }

        var plan = GetOrCreatePlan(methodSymbol, model);
        var site = new MonitorCallSite
        {
            Kind = kind.Value,
            Node = invocation,
            Span = invocation.Span,
            FilePath = filePath,
            MethodQualifiedName = plan.MethodQualifiedName,
            OverloadShape = DescribeOverload(method),
        };
        plan.Sites.Add(site);
        plan.TouchedFiles.Add(filePath);

        switch (kind.Value)
        {
            case MonitorCallKind.Enter:
            case MonitorCallKind.EnterWithTaken:
                PairEnter(plan, site, invocation, kind.Value == MonitorCallKind.EnterWithTaken, model, filePath);
                break;

            case MonitorCallKind.TryEnter:
            case MonitorCallKind.TryEnterWithTaken:
                PairTryEnter(plan, site, invocation, kind.Value == MonitorCallKind.TryEnterWithTaken, method, model, filePath);
                break;

            case MonitorCallKind.Wait:
                HandleWait(plan, site, invocation, method, model, filePath);
                break;

            case MonitorCallKind.Pulse:
                _analysis.PulseSites++;
                CheckOwnership(plan, site, invocation, model, filePath);
                break;

            case MonitorCallKind.PulseAll:
                _analysis.PulseAllSites++;
                CheckOwnership(plan, site, invocation, model, filePath);
                break;

            case MonitorCallKind.Exit:
                // Exit 是否被某次配对尝试消费在 FinalizePlans 统一裁决。
                break;
        }
    }

    private static MonitorCallKind? ClassifyCallKind(IMethodSymbol method)
    {
        var parameters = method.Parameters;
        switch (method.Name)
        {
            case "Enter":
                if (parameters.Length == 1
                    && parameters[0].Type.SpecialType == SpecialType.System_Object)
                    return MonitorCallKind.Enter;
                if (parameters.Length == 2
                    && parameters[0].Type.SpecialType == SpecialType.System_Object
                    && parameters[1].RefKind == RefKind.Ref
                    && parameters[1].Type.SpecialType == SpecialType.System_Boolean)
                    return MonitorCallKind.EnterWithTaken;
                return null;

            case "Exit":
                return parameters.Length == 1 ? MonitorCallKind.Exit : null;

            case "TryEnter":
                if (parameters.Length == 1) return MonitorCallKind.TryEnter;
                if (parameters.Length == 2)
                    return parameters[1].RefKind == RefKind.Ref
                        ? MonitorCallKind.TryEnterWithTaken
                        : MonitorCallKind.TryEnter;
                if (parameters.Length == 3) return MonitorCallKind.TryEnterWithTaken;
                return null;

            case "Wait":
                return parameters.Length is >= 1 and <= 3 ? MonitorCallKind.Wait : null;

            case "Pulse":
                return parameters.Length == 1 ? MonitorCallKind.Pulse : null;

            case "PulseAll":
                return parameters.Length == 1 ? MonitorCallKind.PulseAll : null;

            default:
                return null;
        }
    }

    private static string DescribeOverload(IMethodSymbol method)
        => "(" + string.Join(", ", method.Parameters.Select(p =>
            p.RefKind == RefKind.None
                ? p.Type.ToDisplayString()
                : p.RefKind.ToString().ToLowerInvariant() + " " + p.Type.ToDisplayString())) + ")";

    private static IMethodSymbol? EnclosingOrdinaryMethod(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol != null)
        {
            if (symbol is IMethodSymbol method
                && method.MethodKind is MethodKind.Ordinary
                    or MethodKind.Constructor
                    or MethodKind.StaticConstructor
                    or MethodKind.PropertyGet
                    or MethodKind.PropertySet
                    or MethodKind.EventAdd
                    or MethodKind.EventRemove)
                return method;
            symbol = symbol.ContainingSymbol;
        }
        return null;
    }

    private ConcurrencyRegionPlan GetOrCreatePlan(IMethodSymbol method, SemanticModel model)
    {
        if (_plansByMethod.TryGetValue(method, out var existing)) return existing;

        var qualifiedName = method.ToDisplayString();
        var primaryLocation = method.Locations.FirstOrDefault(l => l.IsInSource);
        var plan = new ConcurrencyRegionPlan
        {
            Id = "CN:" + qualifiedName,
            Method = method,
            MethodQualifiedName = qualifiedName,
            PrimaryFile = primaryLocation?.SourceTree?.FilePath ?? string.Empty,
            MethodSpanStart = primaryLocation?.SourceSpan.Start ?? 0,
            ContainingTypeName = method.ContainingType?.Name ?? string.Empty,
            MethodName = method.Name,
        };
        CollectUsedIdentifiers(plan, method);
        _plansByMethod.Add(method, plan);
        _analysis.Plans.Add(plan);
        return plan;
    }

    private static void CollectUsedIdentifiers(ConcurrencyRegionPlan plan, IMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not SyntaxNode syntax) continue;
            foreach (var identifier in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
                plan.UsedIdentifiers.Add(identifier.Identifier.ValueText);
            foreach (var parameter in syntax.DescendantNodes().OfType<ParameterSyntax>())
                plan.UsedIdentifiers.Add(parameter.Identifier.ValueText);
            foreach (var typeParameter in syntax.DescendantNodes().OfType<TypeParameterSyntax>())
                plan.UsedIdentifiers.Add(typeParameter.Identifier.ValueText);
        }
    }

    // ----- CN-2：Enter/Exit 配对 -----

    private void PairEnter(
        ConcurrencyRegionPlan plan,
        MonitorCallSite site,
        InvocationExpressionSyntax invocation,
        bool withTaken,
        SemanticModel model,
        string filePath)
    {
        var unit = BuildEnterPairUnit(plan, invocation, withTaken, model);
        plan.PairUnits.Add(unit);
        if (!unit.Feasible)
        {
            BlockPlanForPairFailure(plan, invocation);
            EmitNonCanonical(invocation, plan, filePath, ConcurrencyRule.CN2, unit.FailReason!);
        }
        else
        {
            _consumedExits.Add(unit.ExitInvocation);
        }
    }

    private EnterExitPairUnit BuildEnterPairUnit(
        ConcurrencyRegionPlan plan,
        InvocationExpressionSyntax invocation,
        bool withTaken,
        SemanticModel model)
    {
        var lockArgument = GetLockArgument(invocation);
        var lockSymbol = BindLockObject(lockArgument, model);

        var enterStatement = EnclosingStatement(invocation);
        TryStatementSyntax? tryStatement = null;
        BlockSyntax? outerBlock = null;
        var enterInsideTry = false;
        var enterIndex = -1;

        if (enterStatement is ExpressionStatementSyntax enterExprStmt
            && Unwrap(enterExprStmt.Expression) == invocation
            && enterExprStmt.Parent is BlockSyntax parentBlock)
        {
            var index = parentBlock.Statements.IndexOf(enterExprStmt);
            // 形 1：Enter 语句紧邻后继 try。
            if (index + 1 < parentBlock.Statements.Count
                && parentBlock.Statements[index + 1] is TryStatementSyntax adjacentTry)
            {
                tryStatement = adjacentTry;
                outerBlock = parentBlock;
                enterIndex = index;
            }
            // 形 2：Enter 是某 try 块首语句（编译器降低形）。
            else if (index == 0
                && parentBlock.Parent is TryStatementSyntax owningTry
                && owningTry.Block == parentBlock
                && owningTry.Parent is BlockSyntax outerOfTry)
            {
                tryStatement = owningTry;
                outerBlock = outerOfTry;
                enterInsideTry = true;
            }
        }

        if (tryStatement == null || outerBlock == null)
        {
            return InfeasibleEnterPair(
                plan, invocation, lockArgument, lockSymbol, model,
                "the Enter call is not immediately adjacent to a try/finally block " +
                "(conditional Enter, cross-method pairing, or intervening statements).");
        }

        // taken 声明（形 A）：紧邻于首个被消费语句之前。
        LocalDeclarationStatementSyntax? takenDeclaration = null;
        ILocalSymbol? takenLocal = null;
        string? fail = lockSymbol == null
            ? "the lock object expression cannot be statically bound to a single " +
              "local/parameter/field (or is a volatile read)."
            : null;

        if (withTaken)
        {
            var declarationIndex = enterInsideTry
                ? outerBlock.Statements.IndexOf(tryStatement) - 1
                : enterIndex - 1;
            (takenDeclaration, takenLocal, fail) = ResolveTakenDeclaration(
                plan, outerBlock, declarationIndex, invocation, model, fail);
        }
        else if (enterInsideTry)
        {
            // 形 B 的 Enter-inside-try 变体：非编译器形，保守拒绝。
            fail = "form B (plain Enter) must have the try block immediately follow the " +
                   "Enter statement.";
        }

        var consumed = enterInsideTry
            ? (takenDeclaration != null ? 2 : 1)
            : (takenDeclaration != null ? 3 : 2);
        var anchor = takenDeclaration
            ?? (StatementSyntax?)(enterInsideTry ? tryStatement : enterStatement)
            ?? tryStatement;
        var bodyStatements = enterInsideTry
            ? (IReadOnlyList<StatementSyntax>)tryStatement.Block.Statements.Skip(1).ToList()
            : tryStatement.Block.Statements;

        InvocationExpressionSyntax? exitInvocation = null;
        if (fail == null)
        {
            (exitInvocation, fail) = ValidateFinallyExit(
                tryStatement.Finally?.Block,
                withTaken,
                takenLocal,
                lockSymbol,
                model);
            if (exitInvocation != null) _consumedExits.Add(exitInvocation);
        }
        if (fail == null && tryStatement.Catches.Any(catchClause =>
                catchClause.Block != null && ContainsMonitorCall(catchClause.Block, model)))
        {
            fail = "a catch clause contains Monitor calls; release-in-catch or conditional " +
                   "release paths cannot be normalized to a lock statement.";
        }
        if (fail == null && HasOtherExitOnSameLock(exitInvocation!, lockSymbol!, model))
        {
            fail = "multiple Exit paths release the same lock object; only a single " +
                   "finally release can be normalized.";
        }
        if (fail == null && takenLocal != null
            && !IsTakenExclusive(takenLocal, takenDeclaration!, invocation, exitInvocation!, model))
        {
            fail = "the lockTaken variable is referenced outside the canonical pattern " +
                   "(declaration, ref argument, finally guard).";
        }

        return new EnterExitPairUnit
        {
            Plan = plan,
            IsFormA = withTaken,
            EnterInsideTry = enterInsideTry,
            EnterInvocation = invocation,
            LockExpression = lockArgument!,
            LockSymbol = lockSymbol,
            TryStatement = tryStatement,
            BodyStatements = bodyStatements,
            ExitInvocation = exitInvocation!,
            TakenDeclaration = takenDeclaration,
            TakenLocal = takenLocal,
            AnchorStatement = anchor,
            AnchorSpan = anchor.Span,
            ConsumedStatements = consumed,
            Feasible = fail == null,
            FailReason = fail,
        };
    }

    private static EnterExitPairUnit InfeasibleEnterPair(
        ConcurrencyRegionPlan plan,
        InvocationExpressionSyntax invocation,
        ExpressionSyntax? lockArgument,
        ISymbol? lockSymbol,
        SemanticModel model,
        string reason)
    {
        return new EnterExitPairUnit
        {
            Plan = plan,
            IsFormA = false,
            EnterInsideTry = false,
            EnterInvocation = invocation,
            LockExpression = lockArgument ?? invocation,
            LockSymbol = lockSymbol,
            TryStatement = null!,
            BodyStatements = Array.Empty<StatementSyntax>(),
            ExitInvocation = null!,
            AnchorStatement = EnclosingStatement(invocation) ?? (SyntaxNode)invocation,
            AnchorSpan = invocation.Span,
            ConsumedStatements = 1,
            Feasible = false,
            FailReason = reason,
        };
    }

    private (LocalDeclarationStatementSyntax?, ILocalSymbol?, string?) ResolveTakenDeclaration(
        ConcurrencyRegionPlan plan,
        BlockSyntax outerBlock,
        int declarationIndex,
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        string? fail)
    {
        if (fail != null) return (null, null, fail);
        if (declarationIndex < 0
            || outerBlock.Statements[declarationIndex] is not LocalDeclarationStatementSyntax declaration
            || !IsExclusiveBoolFalseDeclaration(declaration))
        {
            return (null, null,
                "the lockTaken variable is not declared as 'bool taken = false;' immediately " +
                "before the pattern.");
        }

        var takenLocal = model.GetDeclaredSymbol(declaration.Declaration.Variables[0]) as ILocalSymbol;
        var refArgument = GetRefTakenArgument(invocation);
        var refSymbol = refArgument != null
            ? model.GetSymbolInfo(refArgument.Expression).Symbol
            : null;
        if (takenLocal == null || refSymbol == null
            || !SymbolEqualityComparer.Default.Equals(takenLocal, refSymbol))
        {
            return (null, null,
                "the ref lockTaken argument does not bind to the exclusively declared local.");
        }

        return (declaration, takenLocal, null);
    }

    private static bool IsExclusiveBoolFalseDeclaration(LocalDeclarationStatementSyntax declaration)
    {
        if (declaration.Declaration.Variables.Count != 1) return false;
        var variable = declaration.Declaration.Variables[0];
        if (declaration.Declaration.Type is not PredefinedTypeSyntax predefined
            || !predefined.Keyword.IsKind(SyntaxKind.BoolKeyword)) return false;
        return variable.Initializer?.Value.IsKind(SyntaxKind.FalseLiteralExpression) == true;
    }

    private (InvocationExpressionSyntax?, string?) ValidateFinallyExit(
        BlockSyntax? finallyBlock,
        bool withTaken,
        ILocalSymbol? takenLocal,
        ISymbol? lockSymbol,
        SemanticModel model)
    {
        if (finallyBlock == null)
            return (null, "the paired try block has no finally clause; the monitor release " +
                          "must be the single finally statement.");

        ExpressionSyntax? exitExpression = null;
        if (finallyBlock.Statements.Count == 1)
        {
            exitExpression = finallyBlock.Statements[0] switch
            {
                ExpressionStatementSyntax expressionStatement => expressionStatement.Expression,
                IfStatementSyntax guard when withTaken
                    && guard.Statement is ExpressionStatementSyntax guarded
                    => guarded.Expression,
                _ => null,
            };
        }
        if (exitExpression == null)
        {
            return (null, "the finally block does not match the canonical release shape " +
                          "(exactly one 'Monitor.Exit(o)', or one 'if (taken) Monitor.Exit(o)' " +
                          "for the lockTaken form).");
        }
        if (Unwrap(exitExpression) is not InvocationExpressionSyntax exitInvocation
            || !IsMonitorExit(exitInvocation, model))
        {
            return (null, "the finally release is not a bound Monitor.Exit call.");
        }

        var exitLockSymbol = BindLockObject(GetLockArgument(exitInvocation), model);
        if (lockSymbol == null || exitLockSymbol == null
            || !SymbolEqualityComparer.Default.Equals(lockSymbol, exitLockSymbol))
        {
            return (null, "the Enter/Exit lock object expressions do not bind to the same " +
                          "local/parameter/field symbol.");
        }

        if (withTaken)
        {
            var guard = (IfStatementSyntax)finallyBlock.Statements[0];
            var conditionSymbol = model.GetSymbolInfo(Unwrap(guard.Condition)).Symbol;
            if (takenLocal == null || conditionSymbol is not ILocalSymbol
                || !SymbolEqualityComparer.Default.Equals(conditionSymbol, takenLocal))
            {
                return (null, "the finally guard does not test the lockTaken variable itself.");
            }
        }

        return (exitInvocation, null);
    }

    private bool HasOtherExitOnSameLock(
        InvocationExpressionSyntax canonicalExit,
        ISymbol lockSymbol,
        SemanticModel model)
    {
        var memberNode = canonicalExit.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (memberNode == null) return false;
        foreach (var invocation in memberNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation == canonicalExit) continue;
            if (!IsMonitorExit(invocation, model)) continue;
            var exitLockSymbol = BindLockObject(GetLockArgument(invocation), model);
            if (exitLockSymbol != null
                && SymbolEqualityComparer.Default.Equals(exitLockSymbol, lockSymbol))
                return true;
        }
        return false;
    }

    private bool IsTakenExclusive(
        ILocalSymbol takenLocal,
        LocalDeclarationStatementSyntax takenDeclaration,
        InvocationExpressionSyntax enterInvocation,
        InvocationExpressionSyntax canonicalExit,
        SemanticModel model)
    {
        var memberNode = canonicalExit.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (memberNode == null) return false;
        var guardCondition = canonicalExit.Ancestors()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault()?.Condition;
        var refArgument = GetRefTakenArgument(enterInvocation);

        foreach (var identifier in memberNode.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol local
                || !SymbolEqualityComparer.Default.Equals(local, takenLocal)) continue;
            if (Within(identifier.Span, takenDeclaration.Span)) continue;
            if (refArgument != null && Within(identifier.Span, refArgument.Span)) continue;
            if (guardCondition != null && Within(identifier.Span, guardCondition.Span)) continue;
            return false;
        }
        return true;
    }

    private static bool Within(TextSpan inner, TextSpan outer)
        => inner.Start >= outer.Start && inner.End <= outer.End;

    private bool ContainsMonitorCall(BlockSyntax block, SemanticModel model)
        => block.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
                && _monitorType != null
                && SymbolEqualityComparer.Default.Equals(
                    method.OriginalDefinition?.ContainingType, _monitorType));

    private bool IsMonitorExit(InvocationExpressionSyntax invocation, SemanticModel model)
        => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
            && method.Name == "Exit"
            && method.Parameters.Length == 1
            && _monitorType != null
            && SymbolEqualityComparer.Default.Equals(
                method.OriginalDefinition?.ContainingType, _monitorType);

    // ----- CN-3：TryEnter 站点 -----

    private void PairTryEnter(
        ConcurrencyRegionPlan plan,
        MonitorCallSite site,
        InvocationExpressionSyntax invocation,
        bool withTaken,
        IMethodSymbol method,
        SemanticModel model,
        string filePath)
    {
        var hasTimedParameter = HasTimedParameter(method);

        var unit = BuildTryEnterUnit(plan, invocation, withTaken, hasTimedParameter, model);
        plan.TryEnterUnits.Add(unit);
        if (!unit.Feasible)
        {
            BlockPlanForPairFailure(plan, invocation);
            EmitNonCanonical(invocation, plan, filePath, ConcurrencyRule.CN3, unit.FailReason!);
        }
        else
        {
            _consumedExits.Add(unit.ExitInvocation!);
        }
    }

    private TryEnterUnit BuildTryEnterUnit(
        ConcurrencyRegionPlan plan,
        InvocationExpressionSyntax invocation,
        bool withTaken,
        bool hasTimedParameter,
        SemanticModel model)
    {
        var lockArgument = GetLockArgument(invocation);
        var lockSymbol = BindLockObject(lockArgument, model);
        var timeoutArgument = GetTimeoutArgument(invocation, withTaken);
        var timeoutIsTimeSpan = timeoutArgument != null && IsTimeSpanExpression(timeoutArgument, model);

        // 裸 bool if 形：if 条件即调用。
        var ifStatement = invocation.Ancestors().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => Unwrap(i.Condition) == invocation);
        if (!withTaken && ifStatement != null)
        {
            var tryStatement = ifStatement.Statement switch
            {
                TryStatementSyntax direct => direct,
                BlockSyntax { Statements.Count: 1 } single
                    when single.Statements[0] is TryStatementSyntax nested => nested,
                _ => null,
            };
            var fail = tryStatement == null
                ? "the bare-bool TryEnter if body is not a try/finally block; the guarded " +
                  "region cannot be identified."
                : null;

            InvocationExpressionSyntax? exitInvocation = null;
            if (fail == null)
            {
                (exitInvocation, fail) = ValidateFinallyExit(
                    tryStatement!.Finally?.Block, withTaken: false,
                    takenLocal: null, lockSymbol, model);
                if (exitInvocation != null) _consumedExits.Add(exitInvocation);
            }
            if (fail == null && tryStatement!.Catches.Any(c => c.Block != null
                    && ContainsMonitorCall(c.Block, model)))
            {
                fail = "a catch clause contains Monitor calls; conditional release paths " +
                       "cannot be normalized.";
            }
            if (fail == null && HasOtherExitOnSameLock(exitInvocation!, lockSymbol!, model))
            {
                fail = "multiple Exit paths release the same lock object; only a single " +
                       "finally release can be normalized.";
            }

            return new TryEnterUnit
            {
                Plan = plan,
                IsBareBoolIf = true,
                IsRefTaken = false,
                IsCanonicalThreeArg = false,
                Invocation = invocation,
                LockExpression = lockArgument!,
                LockSymbol = lockSymbol,
                TimeoutArgument = timeoutArgument,
                TimeoutIsTimeSpan = timeoutIsTimeSpan,
                IfStatement = ifStatement,
                TryStatement = tryStatement,
                GuardedBodyStatements = tryStatement?.Block.Statements,
                ExitInvocation = exitInvocation,
                ElseStatement = ifStatement.Else?.Statement,
                AnchorStatement = ifStatement,
                AnchorSpan = ifStatement.Span,
                ConsumedStatements = 1,
                Feasible = fail == null,
                FailReason = fail,
                IsTimed = timeoutArgument != null,
                IsZeroTimeout = timeoutArgument == null,
                IsLockTaken = false,
                IsBareBool = true,
            };
        }

        // lockTaken 语句形（ref 实参）：与 CN-2 相同的相邻 try/finally 结构要求。
        var callStatement = EnclosingStatement(invocation) as ExpressionStatementSyntax;
        TryStatementSyntax? refTryStatement = null;
        BlockSyntax? outerBlock = null;
        var callInsideTry = false;
        var callIndex = -1;

        if (withTaken && callStatement != null
            && Unwrap(callStatement.Expression) == invocation
            && callStatement.Parent is BlockSyntax parentBlock)
        {
            var index = parentBlock.Statements.IndexOf(callStatement);
            if (index + 1 < parentBlock.Statements.Count
                && parentBlock.Statements[index + 1] is TryStatementSyntax adjacentTry)
            {
                refTryStatement = adjacentTry;
                outerBlock = parentBlock;
                callIndex = index;
            }
            else if (index == 0
                && parentBlock.Parent is TryStatementSyntax owningTry
                && owningTry.Block == parentBlock
                && owningTry.Parent is BlockSyntax outerOfTry)
            {
                refTryStatement = owningTry;
                outerBlock = outerOfTry;
                callInsideTry = true;
            }
        }

        if (!withTaken || refTryStatement == null || outerBlock == null)
        {
            return InfeasibleTryEnterUnit(
                plan, invocation, lockArgument, lockSymbol, timeoutArgument, timeoutIsTimeSpan,
                withTaken, timeoutArgument != null,
                "the TryEnter site is not a canonical bare-bool if form or a lockTaken " +
                "statement form followed by try/finally with a single Exit (captured bool " +
                "result, discarded result, conditional Enter, or cross-method pairing).");
        }

        var declarationIndex = callInsideTry
            ? outerBlock.Statements.IndexOf(refTryStatement) - 1
            : callIndex - 1;
        var (takenDeclaration, takenLocal, fail2) = ResolveTakenDeclaration(
            plan, outerBlock, declarationIndex, invocation, model, lockSymbol == null
                ? "the lock object expression cannot be statically bound to a single " +
                  "local/parameter/field (or is a volatile read)."
                : null);

        InvocationExpressionSyntax? refExitInvocation = null;
        if (fail2 == null)
        {
            (refExitInvocation, fail2) = ValidateFinallyExit(
                refTryStatement.Finally?.Block, withTaken: true,
                takenLocal, lockSymbol, model);
            if (refExitInvocation != null) _consumedExits.Add(refExitInvocation);
        }
        if (fail2 == null && refTryStatement.Catches.Any(c => c.Block != null
                && ContainsMonitorCall(c.Block, model)))
        {
            fail2 = "a catch clause contains Monitor calls; conditional release paths " +
                    "cannot be normalized.";
        }
        if (fail2 == null && HasOtherExitOnSameLock(refExitInvocation!, lockSymbol!, model))
        {
            fail2 = "multiple Exit paths release the same lock object; only a single " +
                    "finally release can be normalized.";
        }
        if (fail2 == null && takenLocal != null
            && !IsTakenExclusive(takenLocal, takenDeclaration!, invocation, refExitInvocation!, model))
        {
            fail2 = "the lockTaken variable is referenced outside the canonical pattern " +
                    "(declaration, ref argument, finally guard).";
        }

        var isCanonicalThreeArg = fail2 == null
            && timeoutArgument != null
            && !timeoutIsTimeSpan;

        return new TryEnterUnit
        {
            Plan = plan,
            IsBareBoolIf = false,
            IsRefTaken = true,
            IsCanonicalThreeArg = isCanonicalThreeArg,
            Invocation = invocation,
            LockExpression = lockArgument!,
            LockSymbol = lockSymbol,
            TimeoutArgument = timeoutArgument,
            TimeoutIsTimeSpan = timeoutIsTimeSpan,
            TakenLocal = takenLocal,
            TakenDeclaration = takenDeclaration,
            TryStatement = refTryStatement,
            GuardedBodyStatements = refTryStatement.Block.Statements,
            ExitInvocation = refExitInvocation,
            CallStatement = callStatement,
            AnchorStatement = (StatementSyntax?)takenDeclaration ?? callStatement!,
            AnchorSpan = ((StatementSyntax?)takenDeclaration ?? callStatement!).Span,
            ConsumedStatements = callInsideTry
                ? (takenDeclaration != null ? 2 : 1)
                : (takenDeclaration != null ? 3 : 2),
            Feasible = fail2 == null,
            FailReason = fail2,
            IsTimed = timeoutArgument != null,
            IsZeroTimeout = timeoutArgument == null,
            IsLockTaken = true,
            IsBareBool = false,
        };
    }

    private static TryEnterUnit InfeasibleTryEnterUnit(
        ConcurrencyRegionPlan plan,
        InvocationExpressionSyntax invocation,
        ExpressionSyntax? lockArgument,
        ISymbol? lockSymbol,
        ExpressionSyntax? timeoutArgument,
        bool timeoutIsTimeSpan,
        bool withTaken,
        bool isTimed,
        string reason)
    {
        var anchor = EnclosingStatement(invocation) ?? (SyntaxNode)invocation;
        return new TryEnterUnit
        {
            Plan = plan,
            IsBareBoolIf = false,
            IsRefTaken = withTaken,
            IsCanonicalThreeArg = false,
            Invocation = invocation,
            LockExpression = lockArgument ?? invocation,
            LockSymbol = lockSymbol,
            TimeoutArgument = timeoutArgument,
            TimeoutIsTimeSpan = timeoutIsTimeSpan,
            AnchorStatement = anchor,
            AnchorSpan = anchor.Span,
            ConsumedStatements = 1,
            Feasible = false,
            FailReason = reason,
            IsTimed = timeoutArgument != null,
            IsZeroTimeout = timeoutArgument == null,
            IsLockTaken = withTaken,
            IsBareBool = !withTaken,
        };
    }

    private static bool HasTimedParameter(IMethodSymbol method)
        => method.Parameters.Any(p => p.RefKind == RefKind.None
            && (p.Type.SpecialType == SpecialType.System_Int32
                || p.Type.ToDisplayString() == "System.TimeSpan"));

    private static ExpressionSyntax? GetTimeoutArgument(InvocationExpressionSyntax invocation, bool withTaken)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (withTaken)
        {
            return arguments.Count == 3 ? arguments[1].Expression : null;
        }
        return arguments.Count == 2 ? arguments[1].Expression : null;
    }

    private static bool IsTimeSpanExpression(ExpressionSyntax expression, SemanticModel model)
        => model.GetTypeInfo(expression).Type?.ToDisplayString() == "System.TimeSpan";

    // ----- CN-4/CN-5：Wait / Pulse / PulseAll -----

    private void HandleWait(
        ConcurrencyRegionPlan plan,
        MonitorCallSite site,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        string filePath)
    {
        var overload = ClassifyWaitOverload(method);
        _analysis.WaitSites++;
        switch (overload)
        {
            case WaitOverloadKind.NoTimeout:
                _analysis.WaitSitesNoTimeout++;
                break;
            case WaitOverloadKind.IntMs:
                _analysis.WaitSitesIntMs++;
                break;
            case WaitOverloadKind.TimeSpan:
                _analysis.WaitSitesTimeSpan++;
                break;
            case WaitOverloadKind.IntMsExitContext:
                _analysis.WaitSitesExitContext++;
                break;
            case WaitOverloadKind.TimeSpanExitContext:
                _analysis.WaitSitesExitContext++;
                break;
        }

        var anchorStatement = EnclosingStatement(invocation);
        var arguments = invocation.ArgumentList.Arguments;
        var anchor = (SyntaxNode?)anchorStatement ?? invocation;
        var waitUnit = new WaitUnit
        {
            Plan = plan,
            Overload = overload,
            Invocation = invocation,
            TimeSpanArgument = overload is WaitOverloadKind.TimeSpan
                or WaitOverloadKind.TimeSpanExitContext
                ? arguments[1].Expression
                : null,
            ExitContextArgumentIndex = overload is WaitOverloadKind.IntMsExitContext
                or WaitOverloadKind.TimeSpanExitContext
                ? 2
                : -1,
            AnchorStatement = anchor,
            AnchorSpan = anchor.Span,
        };

        if (waitUnit.ExitContextArgumentIndex >= 0)
        {
            var exitContext = arguments[waitUnit.ExitContextArgumentIndex].Expression;
            if (exitContext.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                waitUnit.ExitContextFoldable = true;
            }
            else
            {
                // 常量 true / 变量 → CS2J2921，站点原样。
                waitUnit.SkipRewrite = true;
                var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
                _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
                    ConcurrencySeverity.Warning,
                    ConcurrencyRule.CN4,
                    ConcurrencyDiagnosticCodes.WaitExitContext,
                    "Wait(exitContext: …) has no Java equivalent (context marshalling); " +
                    "the site is left unchanged.",
                    filePath,
                    lineSpan.Line + 1,
                    lineSpan.Character + 1,
                    plan.MethodQualifiedName));
            }
        }

        // CN002：timed Wait 站点逐条说明（bool 近似 + 虚假唤醒）。
        if (overload is not WaitOverloadKind.NoTimeout
            && _options.ReportInfo)
        {
            var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
            _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
                ConcurrencySeverity.Info,
                ConcurrencyRule.CN4,
                ConcurrencyDiagnosticCodes.WaitSpuriousWakeup,
                "timed Monitor.Wait keeps its bool timeout result; the Java wait(long) " +
                "bridge can only approximate it. Keep the caller's spurious-wakeup " +
                "predicate loop — this normalization never introduces or removes one.",
                filePath,
                lineSpan.Line + 1,
                lineSpan.Character + 1,
                plan.MethodQualifiedName));
        }

        plan.WaitUnits.Add(waitUnit);
        CheckOwnership(plan, site, invocation, model, filePath);
    }

    private static WaitOverloadKind ClassifyWaitOverload(IMethodSymbol method)
    {
        var parameters = method.Parameters;
        if (parameters.Length == 1) return WaitOverloadKind.NoTimeout;
        if (parameters.Length == 2)
            return parameters[1].Type.SpecialType == SpecialType.System_Int32
                ? WaitOverloadKind.IntMs
                : WaitOverloadKind.TimeSpan;
        if (parameters.Length == 3)
            return parameters[1].Type.SpecialType == SpecialType.System_Int32
                ? WaitOverloadKind.IntMsExitContext
                : WaitOverloadKind.TimeSpanExitContext;
        return WaitOverloadKind.NoTimeout;
    }

    /// <summary>
    /// CN 锁所有权判定（v1 词法-保守，设计 §4.3 末段）：站点须词法位于同对象 lock 体内
    /// （含 CN-2/CN-3 归一出的 try 守卫区），锁在条件/循环外；锁表达式须为 local/
    /// parameter/field 绑定（方法调用/volatile 读/属性访问 → 不可证明）。宁假阳不漏放。
    /// </summary>
    private void CheckOwnership(
        ConcurrencyRegionPlan plan,
        MonitorCallSite site,
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        string filePath)
    {
        var objectArgument = GetLockArgument(invocation);
        var objectSymbol = BindLockObject(objectArgument, model);
        var proven = false;

        if (objectSymbol != null)
        {
            var invocationSpan = invocation.Span;
            foreach (var lockStatement in invocation.Ancestors().OfType<LockStatementSyntax>())
            {
                if (!lockStatement.Statement.Span.Contains(invocationSpan)) continue;
                var lockSymbol = BindLockObject(Unwrap(lockStatement.Expression), model);
                if (lockSymbol != null
                    && SymbolEqualityComparer.Default.Equals(lockSymbol, objectSymbol))
                {
                    proven = true;
                    break;
                }
            }

            if (!proven)
            {
                foreach (var unit in plan.PairUnits)
                {
                    if (!unit.Feasible || unit.LockSymbol == null) continue;
                    if (!unit.BodyStatements.Any(body => body.Span.Contains(invocationSpan))) continue;
                    if (SymbolEqualityComparer.Default.Equals(unit.LockSymbol, objectSymbol))
                    {
                        proven = true;
                        break;
                    }
                }
            }

            if (!proven)
            {
                foreach (var unit in plan.TryEnterUnits)
                {
                    if (!unit.Feasible || unit.LockSymbol == null
                        || unit.GuardedBodyStatements == null) continue;
                    if (!unit.GuardedBodyStatements.Any(body => body.Span.Contains(invocationSpan)))
                        continue;
                    if (SymbolEqualityComparer.Default.Equals(unit.LockSymbol, objectSymbol))
                    {
                        proven = true;
                        break;
                    }
                }
            }
        }

        if (!proven)
        {
            _analysis.OwnershipWarnings++;
            _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
                ConcurrencySeverity.Warning,
                ConcurrencyRule.CN5,
                ConcurrencyDiagnosticCodes.LockOwnershipUnproven,
                "cannot lexically prove the caller holds the same lock object at this " +
                "site; Java would throw IllegalMonitorStateException. Ownership is " +
                "verified lexically only, so false positives stay Warnings.",
                filePath,
                site.Line,
                site.Column,
                plan.MethodQualifiedName));
        }
    }

    // ----- CN-6/CN-10：lock 语句审计 -----

    private void AuditLockStatement(LockStatementSyntax lockStatement, SemanticModel model, string filePath)
    {
        var lineSpan = lockStatement.GetLocation().GetLineSpan().StartLinePosition;

        // CN-10：lock 体内 await 的防御镜像（基线 CS1996 优先）。
        if (lockStatement.Statement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any()
            && !BaselineContains("CS1996", filePath, lockStatement.Span))
        {
            _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
                ConcurrencySeverity.Warning,
                ConcurrencyRule.CN10,
                ConcurrencyDiagnosticCodes.AwaitInLockMirror,
                "await inside a lock region (defensive mirror of CS1996; the baseline " +
                "compiler error takes precedence when already reported).",
                filePath,
                lineSpan.Line + 1,
                lineSpan.Character + 1));
        }

        if (!_options.AuditLockIdentity) return;

        // CN-6：锁对象身份审计（值类型锁对象 CS0185 基线已拒不发）。
        var kind = ClassifyLockIdentityKind(Unwrap(lockStatement.Expression), model);
        if (kind == null || BaselineContains("CS0185", filePath, lockStatement.Expression.Span)) return;

        _analysis.IdentityAuditsByKind[kind] =
            _analysis.IdentityAuditsByKind.TryGetValue(kind, out var existing)
                ? existing + 1
                : 1;
        if (_options.ReportInfo)
        {
            _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
                ConcurrencySeverity.Info,
                ConcurrencyRule.CN6,
                ConcurrencyDiagnosticCodes.LockIdentityAudit,
                DescribeLockIdentityKind(kind),
                filePath,
                lineSpan.Line + 1,
                lineSpan.Character + 1));
        }
    }

    private static string? ClassifyLockIdentityKind(ExpressionSyntax expression, SemanticModel model)
    {
        switch (expression)
        {
            case ThisExpressionSyntax:
                return "This";
            case TypeOfExpressionSyntax:
                return "TypeOf";
            case InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } invocation
                when invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name.Identifier.ValueText == "GetType":
                return "TypeVariable";
            case LiteralExpressionSyntax literal
                when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return "String";
            default:
                var type = model.GetTypeInfo(expression).Type;
                if (type?.SpecialType == SpecialType.System_String) return "String";
                if (type?.ToDisplayString() == "System.Type") return "TypeVariable";
                return null;
        }
    }

    private static string DescribeLockIdentityKind(string kind) => kind switch
    {
        "This" => "locking on 'this' exposes the monitor to external callers; external " +
                  "code can deadlock against it on both C# and Java sides.",
        "TypeOf" => "locking on 'typeof(T)' locks a per-closed-type RuntimeType; the Java " +
                    "side emits 'T.class' with erasure so multiple closed instantiations " +
                    "share one lock.",
        "String" => "locking on a string value interlocks with every other equal string " +
                    "(interning rules differ between .NET and Java).",
        _ => "locking on a System.Type/GetType() value has unstable per-evaluation " +
             "identity; recorded for audit only.",
    };

    // ----- CN-7：volatile 字段审计 -----

    private void AuditVolatileField(FieldDeclarationSyntax declaration, SemanticModel model, string filePath)
    {
        if (!VolatileRewriter.IsVolatileFieldDeclaration(declaration)) return;
        foreach (var variable in declaration.Declaration.Variables)
        {
            if (model.GetDeclaredSymbol(variable) is not IFieldSymbol field) continue;
            if (VolatileRewriter.ClassifyField(field) == VolatileRewriter.VolatileFieldClassification.Primitive)
            {
                _analysis.VolatileFieldsPrimitive++;
                continue;
            }

            _analysis.VolatileFieldsNonPrimitive++;
            if (!_options.AuditVolatile || !_options.ReportInfo
                || BaselineContains("CS0677", filePath, declaration.Span)) continue;

            var lineSpan = declaration.GetLocation().GetLineSpan().StartLinePosition;
            _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
                ConcurrencySeverity.Info,
                ConcurrencyRule.CN7,
                ConcurrencyDiagnosticCodes.VolatileNonPrimitiveField,
                $"volatile field '{field.ContainingType.Name}.{field.Name}' is not " +
                "primitive; the Java converter only emits a suggestion comment for " +
                "non-primitive volatile fields (this diagnostic upgrades it to a visible one).",
                filePath,
                lineSpan.Line + 1,
                lineSpan.Character + 1));
        }
    }

    // ----- CN-9/CN-11：Volatile 静态 API / 范围外原语 -----

    private void RecordVolatileStaticApiSite(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        string filePath)
    {
        _analysis.VolatileStaticApiSites++;
        if (!_options.AuditVolatile || !_options.ReportInfo) return;
        var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
        _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
            ConcurrencySeverity.Info,
            ConcurrencyRule.CN9,
            ConcurrencyDiagnosticCodes.VolatileStaticApi,
            "System.Threading.Volatile static API site maps to the compat " +
            "io.github.ningpp.compat.Volatile VarHandle bridge (acquire/release over " +
            "the generated ref holder); pointer/unsafe overloads remain unsupported — " +
            "counted, not rewritten.",
            filePath,
            lineSpan.Line + 1,
            lineSpan.Character + 1));
    }

    private void RecordOutOfScopeSite(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        string filePath)
    {
        // Thread.Join 重载单独计入。
        if (method.Name == "Join"
            && method.OriginalDefinition?.ContainingType?.ToDisplayString() == "System.Threading.Thread")
        {
            RecordOutOfScope("Thread.Join", invocation.GetLocation(), filePath);
            return;
        }
        var kind = MatchOutOfScopeType(method.OriginalDefinition?.ContainingType);
        if (kind != null) RecordOutOfScope(kind, invocation.GetLocation(), filePath);
    }

    private void RecordOutOfScope(string kind, Location location, string filePath)
    {
        _analysis.OutOfScopeSitesByType[kind] =
            _analysis.OutOfScopeSitesByType.TryGetValue(kind, out var existing)
                ? existing + 1
                : 1;
        if (!_options.ReportInfo) return;
        var lineSpan = location.GetLineSpan().StartLinePosition;
        _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
            ConcurrencySeverity.Info,
            ConcurrencyRule.CN11,
            ConcurrencyDiagnosticCodes.OutOfScopePrimitive,
            $"'{kind}' is outside the C29 v1 scope (Mutex/SemaphoreSlim/EventWaitHandle " +
            "family); counted only — not rewritten, not residual-tracked.",
            filePath,
            lineSpan.Line + 1,
            lineSpan.Character + 1));
    }

    private static string? MatchOutOfScopeType(ITypeSymbol? type)
    {
        if (type == null) return null;
        var original = type.OriginalDefinition;
        var namespaceDisplay = original.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : original.ContainingNamespace.ToDisplayString();
        if (namespaceDisplay.StartsWith("System.Threading.Channels", StringComparison.Ordinal))
            return original.Name;
        var fullName = string.IsNullOrEmpty(namespaceDisplay)
            ? original.Name
            : namespaceDisplay + "." + original.Name;
        return OutOfScopeTypeNames.Contains(fullName) ? original.Name : null;
    }

    // ----- 共用小工具 -----

    private static StatementSyntax? EnclosingStatement(SyntaxNode node)
        => node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();

    private static ExpressionSyntax Unwrap(ExpressionSyntax? expression)
    {
        var current = expression;
        while (current is ParenthesizedExpressionSyntax parenthesized)
            current = parenthesized.Expression;
        return current!;
    }

    private static ExpressionSyntax? GetLockArgument(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.Count > 0
            ? Unwrap(invocation.ArgumentList.Arguments[0].Expression)
            : null;

    private static ArgumentSyntax? GetRefTakenArgument(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.FirstOrDefault(argument =>
            argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword));

    private ISymbol? BindLockObject(ExpressionSyntax? expression, SemanticModel model)
    {
        if (expression == null) return null;
        var symbol = expression switch
        {
            IdentifierNameSyntax identifier => model.GetSymbolInfo(identifier).Symbol,
            MemberAccessExpressionSyntax memberAccess => model.GetSymbolInfo(memberAccess).Symbol,
            _ => null,
        };
        if (symbol is not ILocalSymbol and not IParameterSymbol and not IFieldSymbol) return null;
        if (symbol is IFieldSymbol { IsVolatile: true }) return null;
        return symbol;
    }

    private void BlockPlanForPairFailure(ConcurrencyRegionPlan plan, InvocationExpressionSyntax invocation)
    {
        // 配对类 plan 级原子：任一不可行站点 → 该方法 CN-2/CN-3 整体不动。
        if (plan.PairClassActive)
        {
            plan.Outcome = Outcome.Skipped;
            plan.FailCode = ConcurrencyDiagnosticCodes.NonCanonicalStructure;
            plan.FailReason = "a pairing-class site in this method is not canonical; CN-2/CN-3 " +
                              "are skipped atomically for the whole method (Wait/Pulse sites " +
                              "remain independently rewritable).";
            plan.FailLocation = invocation.GetLocation();
        }
    }

    private void EmitNonCanonical(
        InvocationExpressionSyntax invocation,
        ConcurrencyRegionPlan? plan,
        string filePath,
        ConcurrencyRule rule,
        string reason)
    {
        _analysis.InfeasibleSites++;
        var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
        _analysis.Diagnostics.Add(new ConcurrencyDiagnostic(
            ConcurrencySeverity.Warning,
            rule,
            ConcurrencyDiagnosticCodes.NonCanonicalStructure,
            "explicit Monitor structure is not canonical and is left unchanged: " + reason,
            filePath,
            lineSpan.Line + 1,
            lineSpan.Character + 1,
            plan?.MethodQualifiedName));
    }

    // ----- 收尾：孤立 Exit、确定性排序、绑定摘要 -----

    private void FinalizePlans()
    {
        foreach (var plan in _analysis.Plans)
        {
            foreach (var site in plan.Sites)
            {
                if (site.Kind != MonitorCallKind.Exit) continue;
                if (_consumedExits.Contains(site.Node)) continue;
                EmitNonCanonical(
                    site.Node,
                    plan,
                    site.FilePath,
                    ConcurrencyRule.CN2,
                    "Monitor.Exit has no canonical Enter pairing in the same method " +
                    "(cross-method pairing or standalone release); intrinsic locks cannot " +
                    "express it.");
            }
        }

        _analysis.Plans.Sort((left, right) =>
        {
            var byFile = string.Compare(
                left.PrimaryFile, right.PrimaryFile, StringComparison.OrdinalIgnoreCase);
            if (byFile != 0) return byFile;
            var byPosition = left.MethodSpanStart.CompareTo(right.MethodSpanStart);
            return byPosition != 0
                ? byPosition
                : string.Compare(
                    left.MethodQualifiedName, right.MethodQualifiedName, StringComparison.Ordinal);
        });

        foreach (var plan in _analysis.Plans)
        {
            plan.Sites.Sort((left, right) => left.Span.Start.CompareTo(right.Span.Start));
            plan.PairUnits.Sort((left, right) => left.AnchorSpan.Start.CompareTo(right.AnchorSpan.Start));
            plan.TryEnterUnits.Sort((left, right) => left.AnchorSpan.Start.CompareTo(right.AnchorSpan.Start));
            plan.WaitUnits.Sort((left, right) => left.AnchorSpan.Start.CompareTo(right.AnchorSpan.Start));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in plan.PairUnits)
            {
                AddBindingSummary(plan, unit.LockSymbol, seen);
            }
            foreach (var unit in plan.TryEnterUnits)
            {
                AddBindingSummary(plan, unit.LockSymbol, seen);
            }
        }
    }

    private static void AddBindingSummary(ConcurrencyRegionPlan plan, ISymbol? symbol, HashSet<string> seen)
    {
        if (symbol == null) return;
        var summary = DescribeSymbol(symbol);
        if (!seen.Add(summary)) return;
        plan.LockObjectBindings.Add(summary);
    }

    private static string DescribeSymbol(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => $"local '{local.Name}'",
        IParameterSymbol parameter => $"parameter '{parameter.Name}'",
        IFieldSymbol field => $"field '{field.ContainingType.Name}.{field.Name}'",
        _ => symbol.ToDisplayString(),
    };
}
