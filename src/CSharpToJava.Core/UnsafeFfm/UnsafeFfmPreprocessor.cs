using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 unsafe-ffm 预处理 pass 公共门面（C#→C#，设计 §4）。单次 parse：
/// 直接使用调用方已解析树的 <see cref="SemanticModel"/>，规则在内存中顺序
/// 合批：R1 <see cref="UnsafeContextStripper"/> 始终先跑；M1 起 EnableFixed
/// 时同树合批 R2 <see cref="FixedBufferFieldRewriter"/> → R3
/// <see cref="FixedStatementRewriter"/>（字段先改数组，使 fixed(byte* p=字段)
/// 走数组路径），随后做 fixed 段 residual 自检；M2 起 EnableStackAlloc 时
/// R4 <see cref="StackAllocRewriter"/> 在 R3 之后合批并做 stackalloc 段
/// residual 自检；最后格式化并做本地解析校验。
/// 拒绝优先：任一 Error 即文件 <see cref="UnsafeFfmResult.Changed"/>=false、
/// 原文保留；输出可被自身二次安全驱动（幂等）。
/// </summary>
public sealed class UnsafeFfmPreprocessor
{
    private readonly UnsafeFfmOptions _options;

    public UnsafeFfmPreprocessor(UnsafeFfmOptions? options = null)
    {
        _options = options ?? new UnsafeFfmOptions();
    }

    /// <summary>
    /// 改写单个文件。语义模型由调用方在整树编译（allowUnsafe）上一次性提供。
    /// </summary>
    public UnsafeFfmResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new UnsafeFfmStatistics();
        var diagnostics = new List<UnsafeFfmDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // M2 拒绝预检（拒绝优先，先于 R1/R2/R3）：stackalloc 站点为裸指针
        // （含 var）或元素不可安全堆化时直接 UF040，避免 R3 先以 UF020
        // 拦截同一构造。判定走 StackAllocElementSafety 共享事实。
        if (_options.EnableStackAlloc && TryRejectStackAllocSites(
                root, semanticModel, filePath, statistics, out var prescanDiagnostics))
        {
            return Rollback(sourceCode, statistics, prescanDiagnostics);
        }

        // R1 始终先跑（unsafe 基座，无独立开关）。
        var newRoot = (CompilationUnitSyntax)new UnsafeContextStripper(statistics).Visit(root)!;

        // R1 残留自检：unsafe 块/unsafe 修饰符残留即规则缺口，UF010 拒绝。
        if (HasUnsafeContextResidual(newRoot))
        {
            statistics.ResidualCount++;
            statistics.Rejections++;
            diagnostics.Add(new UnsafeFfmDiagnostic(
                UnsafeFfmSeverity.Error,
                UnsafeFfmDiagnosticCode.UF010,
                "unsafe-context residual gate: the rewritten tree still contains an unsafe block " +
                "or unsafe modifier after R1 (internal rule gap); the original file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        // R5 指针签名白名单（M3a）：链位在 R1 之后、R2 之前。probe 实测
        // CS8385 证明参数必须先由 byte* 改为 byte[]，R3 的 fixed 才能走数组路径。
        var flowModel = new PointerFlowModel();
        var pointerRewritten = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var loweredPointerSymbols = Array.Empty<ISymbol>();

        if (_options.EnablePointer)
        {
            var signatureResolver = new OriginalSyntaxResolver(root, newRoot);
            var signatureRewriter = new PointerSignatureRewriter(
                semanticModel,
                new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default),
                flowModel,
                statistics,
                signatureResolver);
            newRoot = (CompilationUnitSyntax)signatureRewriter.Visit(newRoot)!;
            foreach (var diagnostic in signatureRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (signatureRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            foreach (var symbol in signatureRewriter.RewrittenSymbols)
            {
                pointerRewritten.Add(symbol);
            }
        }

        // M1：R2→R3 同树合批，仅在 EnableFixed 时执行。
        // fixed residual 闸门：R6（EnablePointer 时）会继续索引化 R5 域内
        // 的解引用/下标，闸门必须延后到 R6 之后，否则 *p 会被误判为 R3 残留。
        var fixedGatePending = false;
        if (_options.EnableFixed)
        {
            var fieldResolver = new OriginalSyntaxResolver(root, newRoot);
            var fieldRewriter = new FixedBufferFieldRewriter(
                semanticModel, statistics, fieldResolver);
            newRoot = (CompilationUnitSyntax)fieldRewriter.Visit(newRoot)!;
            foreach (var diagnostic in fieldRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (fieldRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            var statementResolver = new OriginalSyntaxResolver(root, newRoot);
            var statementRewriter = new FixedStatementRewriter(
                semanticModel,
                fieldRewriter.RewrittenFields,
                pointerRewritten,
                statistics,
                statementResolver);
            newRoot = (CompilationUnitSyntax)statementRewriter.Visit(newRoot)!;
            foreach (var diagnostic in statementRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (statementRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            loweredPointerSymbols = statementRewriter.LoweredSymbols.ToArray();

            if (!_options.EnablePointer)
            {
                // fixed 段 residual 自检：输出树不得残留 fixed 语句、fixed 字段
                // 以及由 fixed 块根生出的指针节点；命中即内部规则缺口 UF010。
                if (HasFixedResidual(newRoot, out var residualReason))
                {
                    statistics.ResidualCount++;
                    statistics.Rejections++;
                    diagnostics.Add(new UnsafeFfmDiagnostic(
                        UnsafeFfmSeverity.Error,
                        UnsafeFfmDiagnosticCode.UF010,
                        "fixed residual gate: " + residualReason +
                        " (internal rule gap); the original file was preserved.",
                        filePath, 0, 0));
                    return Rollback(sourceCode, statistics, diagnostics);
                }
            }
            else
            {
                fixedGatePending = true;
            }
        }

        // M2：R4 仅在 EnableStackAlloc 时执行，链位在 R3 之后。
        if (_options.EnableStackAlloc)
        {
            var stackAllocResolver = new OriginalSyntaxResolver(root, newRoot);
            var stackAllocRewriter = new StackAllocRewriter(
                semanticModel, statistics, stackAllocResolver);
            newRoot = (CompilationUnitSyntax)stackAllocRewriter.Visit(newRoot)!;
            foreach (var diagnostic in stackAllocRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (stackAllocRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            // stackalloc 段 residual 自检：输出树不得残留任何显式/隐式
            // stackalloc 节点；命中即内部规则缺口 UF010。
            if (HasStackAllocResidual(newRoot, out var residualReason))
            {
                statistics.ResidualCount++;
                statistics.Rejections++;
                diagnostics.Add(new UnsafeFfmDiagnostic(
                    UnsafeFfmSeverity.Error,
                    UnsafeFfmDiagnosticCode.UF010,
                    "stackalloc residual gate: " + residualReason +
                    " (internal rule gap); the original file was preserved.",
                    filePath, 0, 0));
                return Rollback(sourceCode, statistics, diagnostics);
            }
        }

        // R6 指针表达式索引化（M3a 基础族）：消费 R5 的 flowModel 与
        // R3 降级符号集，链位在 R4 之后。
        if (_options.EnablePointer)
        {
            var exprResolver = new OriginalSyntaxResolver(root, newRoot);
            var exprRewriter = new PointerExprRewriter(
                semanticModel,
                flowModel,
                loweredPointerSymbols,
                statistics,
                exprResolver);
            newRoot = (CompilationUnitSyntax)exprRewriter.Visit(newRoot)!;
            foreach (var diagnostic in exprRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (exprRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            // 延后的 fixed residual 闸门：R6 已收编 R5 域指针使用，剩余即真缺口。
            if (fixedGatePending && HasFixedResidual(newRoot, out var fixedReason))
            {
                statistics.ResidualCount++;
                statistics.Rejections++;
                diagnostics.Add(new UnsafeFfmDiagnostic(
                    UnsafeFfmSeverity.Error,
                    UnsafeFfmDiagnosticCode.UF010,
                    "fixed residual gate: " + fixedReason +
                    " (internal rule gap); the original file was preserved.",
                    filePath, 0, 0));
                return Rollback(sourceCode, statistics, diagnostics);
            }

            // pointer residual 闸门：输出树指针语法节点必须归零（指针类型/
            // function pointer/取址/解引用/指针成员访问）；命中即 UF010。
            if (HasPointerResidual(newRoot, out var pointerReason))
            {
                statistics.ResidualCount++;
                statistics.Rejections++;
                diagnostics.Add(new UnsafeFfmDiagnostic(
                    UnsafeFfmSeverity.Error,
                    UnsafeFfmDiagnosticCode.UF010,
                    "pointer residual gate: " + pointerReason +
                    " (internal rule gap); the original file was preserved.",
                    filePath, 0, 0));
                return Rollback(sourceCode, statistics, diagnostics);
            }
        }

        if (newRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new UnsafeFfmDiagnostic(
                UnsafeFfmSeverity.Warning,
                UnsafeFfmDiagnosticCode.UF010,
                "unsafe-ffm rewrite failed local parse validation; the original file was preserved " +
                "pending whole-tree build/test verification.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new UnsafeFfmResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// stackalloc 拒绝预检：扫描原始树全部显式/隐式 stackalloc 站点，
    /// 站点为裸指针（含 var 自然类型）或元素不可安全堆化即收集 UF040。
    /// 返回 true 表示存在拒绝（调用方整文回滚）。
    /// </summary>
    private static bool TryRejectStackAllocSites(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        string filePath,
        UnsafeFfmStatistics statistics,
        out List<UnsafeFfmDiagnostic> rejected)
    {
        rejected = new List<UnsafeFfmDiagnostic>();

        foreach (var node in root.DescendantNodes()
                     .OfType<StackAllocArrayCreationExpressionSyntax>())
        {
            var siteType = StackAllocElementSafety.GetSiteType(semanticModel, node);
            if (siteType is IPointerTypeSymbol)
            {
                rejected.Add(MakeUf040(
                    $"stackalloc expression '{node.ToString().Replace("\n", " ")}' is assigned to a " +
                    "bare pointer target (including 'var', whose natural type for stackalloc is T*); " +
                    "it cannot be converted to a Span/array local without changing pointer-only usages. " +
                    "Rewrite it as 'Span<T> x = stackalloc T[n];' (or an array form) manually.",
                    node, statistics));
                continue;
            }

            var element = StackAllocElementSafety.GetExplicitElement(semanticModel, node);
            var reason = string.Empty;
            if (element == null || !StackAllocElementSafety.IsSafeElement(element, out reason))
            {
                rejected.Add(MakeUf040(
                    $"stackalloc element type '{element?.ToDisplayString() ?? "?"}' cannot be safely " +
                    "heap-allocated: " + reason + " Rewrite the allocation manually or route it through " +
                    "a supported form.",
                    node, statistics));
            }
        }

        foreach (var node in root.DescendantNodes()
                     .OfType<ImplicitStackAllocArrayCreationExpressionSyntax>())
        {
            var siteType = StackAllocElementSafety.GetSiteType(semanticModel, node);
            if (siteType is IPointerTypeSymbol)
            {
                rejected.Add(MakeUf040(
                    $"implicit stackalloc expression '{node}' is used at a bare pointer site (including " +
                    "'var', whose natural type for stackalloc is T*); it cannot be converted to a " +
                    "Span/array form without changing pointer-only usages. Rewrite it manually with an " +
                    "explicit 'Span<T>' target.",
                    node, statistics));
                continue;
            }

            var element = StackAllocElementSafety.GetImplicitElement(semanticModel, node);
            var reason = string.Empty;
            if (element == null || !StackAllocElementSafety.IsSafeElement(element, out reason))
            {
                rejected.Add(MakeUf040(
                    $"implicit stackalloc element type '{element?.ToDisplayString() ?? "?"}' cannot be " +
                    "safely heap-allocated: " + reason + " Rewrite the allocation manually.",
                    node, statistics));
            }
        }

        return rejected.Count > 0;
    }

    private static UnsafeFfmDiagnostic MakeUf040(
        string message, SyntaxNode node, UnsafeFfmStatistics statistics)
    {
        var line = 1;
        var character = 1;
        try
        {
            var span = node.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 行号不可用时退回 (1,1)。
        }

        statistics.Rejections++;
        statistics.Uf040Rejections++;
        return new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF040,
            message,
            null, line, character);
    }

    /// <summary>
    /// R1 残留扫描：仍存在 <see cref="UnsafeStatementSyntax"/> 或 unsafe keyword
    /// token（任何位置的 unsafe 修饰符）即视为残留。
    /// </summary>
    private static bool HasUnsafeContextResidual(CompilationUnitSyntax root)
    {
        if (root.DescendantNodes().OfType<UnsafeStatementSyntax>().Any()) return true;
        return root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.UnsafeKeyword));
    }

    /// <summary>
    /// fixed 段 residual 扫描（EnableFixed 时）：
    /// <list type="bullet">
    /// <item>全树不得残留 <see cref="FixedStatementSyntax"/> 或带 fixed 修饰符的字段；</item>
    /// <item>每个被 R3 改写（带注释）的成员范围内不得残留指针类型、取址/解引用、
    /// 指针成员访问等由 fixed 块根生出的指针节点。</item>
    /// </list>
    /// </summary>
    private static bool HasFixedResidual(CompilationUnitSyntax root, out string reason)
    {
        if (root.DescendantNodes().OfType<FixedStatementSyntax>().Any())
        {
            reason = "the rewritten tree still contains a fixed statement";
            return true;
        }

        if (root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Any(f => f.Modifiers.Any(SyntaxKind.FixedKeyword)))
        {
            reason = "the rewritten tree still contains a fixed-keyword field declaration";
            return true;
        }

        foreach (var annotated in root.GetAnnotatedNodes(
                     FixedStatementRewriter.FixedReplacedAnnotation))
        {
            var member = annotated.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (member == null) continue;

            foreach (var node in member.DescendantNodes())
            {
                if (node is PointerTypeSyntax)
                {
                    reason = $"member '{member}' still contains a pointer type after fixed lowering";
                    return true;
                }

                if (node is PrefixUnaryExpressionSyntax prefix
                    && (prefix.Kind() == SyntaxKind.AddressOfExpression
                        || prefix.Kind() == SyntaxKind.PointerIndirectionExpression))
                {
                    reason = $"member '{member}' still contains an address-of/indirection expression " +
                             "after fixed lowering";
                    return true;
                }

                if (node is MemberAccessExpressionSyntax memberAccess
                    && memberAccess.Kind() == SyntaxKind.PointerMemberAccessExpression)
                {
                    reason = $"member '{member}' still contains a pointer member access (p->m) " +
                             "after fixed lowering";
                    return true;
                }
            }

            if (member.DescendantTokens().Any(t => t.IsKind(SyntaxKind.FixedKeyword)))
            {
                reason = $"member '{member}' still contains a fixed keyword after fixed lowering";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// stackalloc 段 residual 扫描（EnableStackAlloc 时）：全树不得残留
    /// <see cref="StackAllocArrayCreationExpressionSyntax"/> 或
    /// <see cref="ImplicitStackAllocArrayCreationExpressionSyntax"/>。
    /// </summary>
    private static bool HasStackAllocResidual(CompilationUnitSyntax root, out string reason)
    {
        if (root.DescendantNodes().OfType<StackAllocArrayCreationExpressionSyntax>().Any())
        {
            reason = "the rewritten tree still contains an explicit stackalloc expression";
            return true;
        }

        if (root.DescendantNodes().OfType<ImplicitStackAllocArrayCreationExpressionSyntax>().Any())
        {
            reason = "the rewritten tree still contains an implicit stackalloc expression";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// pointer 段 residual 扫描（EnablePointer 时）：R5/R6 之后全树不得残留
    /// <list type="bullet">
    /// <item><see cref="PointerTypeSyntax"/> 或 <see cref="FunctionPointerTypeSyntax"/>；</item>
    /// <item>取址/解引用 <see cref="PrefixUnaryExpressionSyntax"/>；</item>
    /// <item>指针成员访问（p-&gt;m）。</item>
    /// </list>
    /// 命中即内部规则缺口 UF010。
    /// </summary>
    private static bool HasPointerResidual(CompilationUnitSyntax root, out string reason)
    {
        if (root.DescendantNodes().OfType<PointerTypeSyntax>().Any())
        {
            reason = "the rewritten tree still contains a pointer type";
            return true;
        }

        if (root.DescendantNodes().OfType<FunctionPointerTypeSyntax>().Any())
        {
            reason = "the rewritten tree still contains a function pointer type";
            return true;
        }

        if (root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
            .Any(p => p.Kind() is SyntaxKind.AddressOfExpression
                or SyntaxKind.PointerIndirectionExpression))
        {
            reason = "the rewritten tree still contains an address-of or pointer indirection expression";
            return true;
        }

        if (root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Any(m => m.Kind() == SyntaxKind.PointerMemberAccessExpression))
        {
            reason = "the rewritten tree still contains a pointer member access (p->m)";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>原文回滚：Changed=false，OutputCode 即原始源码。</summary>
    private static UnsafeFfmResult Rollback(
        string sourceCode,
        UnsafeFfmStatistics statistics,
        IReadOnlyList<UnsafeFfmDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };

    /// <summary>整树格式化 → 原文候选，每个候选重新解析零 Error 才返回。</summary>
    private static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                candidates.Add(Formatter.Format(root, workspace).ToFullString());
            }
            catch
            {
                // 格式化失败不致命，原文候选兜底。
            }
        }
        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }
}
