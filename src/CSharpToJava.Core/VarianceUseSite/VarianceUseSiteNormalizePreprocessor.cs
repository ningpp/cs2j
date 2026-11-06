using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.VarianceAudit;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Core.VarianceUseSite;

/// <summary>单文件 RewriteFile 汇总。<see cref="Changed"/> 为 false 时输出即原源码。</summary>
public sealed record VarianceUseSiteNormalizeResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required IReadOnlyList<VarianceUseSiteDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<VarianceUseSiteRecord> Sites { get; init; }

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == VarianceUseSiteSeverity.Error);
}

/// <summary>
/// 方案 §6.4-R4：链末 <c>variance-use-site-normalize</c>（VR）pass。
/// <para>
/// 分析面直接复用 C33 的 <see cref="VarianceAuditPreprocessor"/> /
/// <see cref="UseSiteSafetyClassifier"/>：本类不做第二套型变分析，只消费
/// <c>VarianceAuditAnalysis.ConversionSites</c>（VA010/011/015 判定）与
/// <c>UseSites</c>（位置流向、mapped mutability、成员解析完整性）。
/// </para>
/// <para>
/// 改写规则（与方案 VR-01~08 对齐）：
/// </para>
/// <list type="bullet">
/// <item>VR-01 producer：<c>(IEnumerable&lt;Base&gt;)(object)expr</c>；</item>
/// <item>VR-02 consumer：<c>(Func&lt;Derived,R&gt;)(object)expr</c>；</item>
/// <item>VR-03 嵌套 invariant / VR-05 Both / VR-06 值类型实参 / VR-07 方法组 /
/// VR-08 动态未绑定 → <see cref="VarianceUseSiteActions.DeclaredUnsupported"/> + CS2J1404；</item>
/// <item>VR-04 引用数组协变 → 保留原样（Java 数组协变），仅逐站点验证元素映射无装箱差异。</item>
/// </list>
/// 安全五条件：引用类型实参 / raw 同型（ConversionContext.MapType 稳定性验证）/
/// 读取传参位不写回 / 不跨 ref|out|in 与 ref-like / per-file 解析与语义后校验。
/// 任一改写后校验失败 → 整文件回滚（不半改写），并报 CS2J1404。
/// </summary>
public sealed class VarianceUseSiteNormalizePreprocessor
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly VarianceUseSiteNormalizeOptions _options;
    private readonly TypeMutabilityTable _mutability = new();

    private CSharpCompilation? _compilation;
    private VarianceAuditAnalysis? _analysis;
    private readonly Dictionary<string, List<VarianceConversionFinding>> _findingsByFile =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string File, int Line, int Column), VarianceUseRecord> _usesByAnchor =
        new();
    private readonly List<VarianceUseSiteRecord> _sites = new();

    private ConversionContext? _mappingContext;
    private bool _mappingAttempted;

    public VarianceUseSiteNormalizePreprocessor(VarianceUseSiteNormalizeOptions? options = null)
        => _options = options ?? new VarianceUseSiteNormalizeOptions();

    /// <summary>全链统计（summary 行与报告 totals）。</summary>
    public VarianceUseSiteNormalizeStatistics Statistics { get; } = new();

    /// <summary>本 pass 已分类的全部站点（按处理顺序，报告时再稳定排序）。</summary>
    public IReadOnlyList<VarianceUseSiteRecord> Sites => _sites;

    public void BindCompilation(CSharpCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
        _analysis = null;
        _findingsByFile.Clear();
        _usesByAnchor.Clear();
        _sites.Clear();
        Statistics.FilesScanned = 0;
        Statistics.FilesTransformed = 0;
        Statistics.FilesSkippedClean = 0;
        Statistics.FilesRolledBack = 0;
        Statistics.SitesTotal = 0;
        Statistics.SitesRewritten = 0;
        Statistics.SitesPreserved = 0;
        Statistics.SitesDeclaredUnsupported = 0;
        Statistics.RewrittenByRule.Clear();
        Statistics.PreservedByRule.Clear();
        Statistics.UnsupportedByRule.Clear();
    }

    /// <summary>
    /// 改写单文件。读取 <paramref name="semanticModel"/> 所在整树分析结果，只处理
    /// 该文件的 VA010/011/015 转换站点。
    /// </summary>
    public VarianceUseSiteNormalizeResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        EnsurePrepared();
        var diagnostics = new List<VarianceUseSiteDiagnostic>();
        var pending = new List<PendingSite>();
        Statistics.FilesScanned++;

        var model = semanticModel;
        var path = model.SyntaxTree.FilePath;
        var reportPath = string.IsNullOrEmpty(relativePath) ? path : relativePath;
        if (!_findingsByFile.TryGetValue(path, out var findings) || findings.Count == 0)
        {
            Statistics.FilesSkippedClean++;
            return Unchanged(sourceCode, diagnostics, Array.Empty<VarianceUseSiteRecord>());
        }

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var processedAnchors = new HashSet<(int Line, int Column)>();
        var processedExpressions = new HashSet<TextSpan>();

        foreach (var finding in findings)
        {
            if (!processedAnchors.Add((finding.Line, finding.Column)))
            {
                continue;
            }

            var anchor = ResolveAnchor(root, finding.Line, finding.Column);
            if (anchor == null)
            {
                pending.Add(finding.Direction == VarianceConversionDirection.Array
                    ? PreservedArraySite(finding, reportPath)
                    : Unsupported(
                        VarianceUseSiteRules.BothFlow,
                        finding.SourceType,
                        finding.TargetType,
                        finding.Direction,
                        "site anchor could not be resolved from the audit position",
                        reportPath,
                        finding.Line,
                        finding.Column));
                continue;
            }

            var candidates = ResolveCandidates(anchor, model);
            if (candidates.Count == 0)
            {
                // Reference-array edges are preserved by VR-04 anyway, so an
                // identity/annotation-only array edge (e.g. object[] -> object[])
                // must not be reported as an unsupported rewrite.
                pending.Add(finding.Direction == VarianceConversionDirection.Array
                    ? PreservedArraySite(finding, reportPath)
                    : Unsupported(
                        VarianceUseSiteRules.BothFlow,
                        finding.SourceType,
                        finding.TargetType,
                        finding.Direction,
                        "audit reported a conversion edge but no source expression could be bound",
                        reportPath,
                        finding.Line,
                        finding.Column));
                continue;
            }

            var use = LookupUse(path, finding);
            foreach (var candidate in candidates)
            {
                if (!processedExpressions.Add(candidate.Expression.Span))
                {
                    continue;
                }

                pending.Add(Classify(finding, use, candidate, model, reportPath));
            }
        }

        if (pending.Count == 0)
        {
            Statistics.FilesSkippedClean++;
            return Unchanged(sourceCode, diagnostics, Array.Empty<VarianceUseSiteRecord>());
        }

        var rewriteMap = new Dictionary<ExpressionSyntax, ITypeSymbol>();
        foreach (var site in pending)
        {
            if (site.TargetType != null)
            {
                rewriteMap.TryAdd(site.Expression!, site.TargetType);
            }
        }

        if (!_options.Rewrite || rewriteMap.Count == 0)
        {
            var unchangedRecords = Emit(pending, diagnostics, reportPath);
            Statistics.FilesSkippedClean++;
            return Unchanged(sourceCode, diagnostics, unchangedRecords);
        }

        var newRoot = (CompilationUnitSyntax)root.ReplaceNodes(
            rewriteMap.Keys,
            (original, _) => BuildCast(original, rewriteMap[original]));
        var outputCode = newRoot.ToFullString();

        var failure = ValidateRewrittenFile(model, newRoot, outputCode);
        if (failure != null)
        {
            Statistics.FilesRolledBack++;
            var rolledBack = pending
                .Select(site => site.TargetType == null
                    ? site.Record
                    : site.Record with
                    {
                        Action = VarianceUseSiteActions.DeclaredUnsupported,
                        Rewritten = false,
                        Code = nameof(VarianceUseSiteDiagnosticCode.CS2J1404),
                        Reason = "whole-file rollback: " + failure,
                    })
                .ToList();
            diagnostics.Add(new VarianceUseSiteDiagnostic(
                VarianceUseSiteSeverity.Error,
                VarianceUseSiteDiagnosticCode.CS2J1404,
                "variance-use-site-normalize rewrite failed per-file validation and the " +
                $"whole file was preserved: {failure}",
                path,
                0,
                0));
            var relativeRolledBack = new List<VarianceUseSiteRecord>(rolledBack.Count);
            foreach (var record in rolledBack)
            {
                var relativeRecord = record with { FilePath = reportPath };
                relativeRolledBack.Add(relativeRecord);
                Statistics.Add(relativeRecord);
                _sites.Add(relativeRecord);
            }

            return Unchanged(sourceCode, diagnostics, relativeRolledBack);
        }

        var records = Emit(pending, diagnostics, reportPath);
        Statistics.FilesTransformed++;
        return new VarianceUseSiteNormalizeResult
        {
            OutputCode = outputCode,
            Changed = true,
            Diagnostics = diagnostics,
            Sites = records,
        };
    }

    /// <summary>
    /// 稳定、无时间戳的报告 JSON。站点按 (file, line, column, rule) 排序，totals
    /// 与 summary 同源，保证两次运行字节一致。
    /// </summary>
    public string BuildReportJson()
    {
        var sites = _sites
            .OrderBy(site => site.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(site => site.Line)
            .ThenBy(site => site.Column)
            .ThenBy(site => site.Rule, StringComparer.Ordinal)
            .ToList();

        var report = new VarianceUseSiteReport(
            SchemaVersion: 1,
            Options: new VarianceUseSiteReportOptions(
                _options.Rewrite, _options.Strict, _options.CheckMappedMutability),
            Sites: sites,
            Totals: new VarianceUseSiteReportTotals(
                FilesScanned: Statistics.FilesScanned,
                FilesTransformed: Statistics.FilesTransformed,
                FilesSkippedClean: Statistics.FilesSkippedClean,
                FilesRolledBack: Statistics.FilesRolledBack,
                SitesTotal: Statistics.SitesTotal,
                SitesRewritten: Statistics.SitesRewritten,
                SitesPreserved: Statistics.SitesPreserved,
                SitesDeclaredUnsupported: Statistics.SitesDeclaredUnsupported,
                RewrittenByRule: Statistics.RewrittenByRule,
                PreservedByRule: Statistics.PreservedByRule,
                UnsupportedByRule: Statistics.UnsupportedByRule));

        return JsonSerializer.Serialize(report, ReportJsonOptions);
    }

    // ---- analysis reuse (single source of truth: C33) ----

    private void EnsurePrepared()
    {
        if (_analysis != null)
        {
            return;
        }

        if (_compilation == null)
        {
            throw new InvalidOperationException(
                "BindCompilation must be called before RewriteFile.");
        }

        // Reuse the C33 inventory and UseSiteSafetyClassifier verbatim; this pass
        // never re-implements variance flow/mutability analysis.
        var audit = new VarianceAuditPreprocessor(new VarianceAuditOptions
        {
            ReportInfo = false,
            CheckMappedMutability = _options.CheckMappedMutability,
        });
        audit.BindCompilation(_compilation);
        var analysis = audit.Analyze();

        foreach (var finding in analysis.ConversionSites)
        {
            var file = finding.FilePath ?? string.Empty;
            if (!_findingsByFile.TryGetValue(file, out var list))
            {
                list = new List<VarianceConversionFinding>();
                _findingsByFile[file] = list;
            }

            list.Add(finding);
        }

        foreach (var use in analysis.UseSites)
        {
            _usesByAnchor.TryAdd(
                (use.FilePath ?? string.Empty, use.Line, use.Column), use);
        }

        _analysis = analysis;
    }

    private VarianceUseRecord? LookupUse(string path, VarianceConversionFinding finding)
        => _usesByAnchor.TryGetValue((path, finding.Line, finding.Column), out var use)
            ? use
            : null;

    // ---- per-site classification ----

    private PendingSite Classify(
        VarianceConversionFinding finding,
        VarianceUseRecord? use,
        SourceCandidate candidate,
        SemanticModel model,
        string path)
    {
        var expression = candidate.Expression;
        var location = expression.GetLocation().GetLineSpan().StartLinePosition;
        var line = location.Line + 1;
        var column = location.Character + 1;
        var info = model.GetTypeInfo(expression);
        var sourceType = info.Type;
        var targetType = info.ConvertedType;
        var direction = finding.Direction;

        if (sourceType == null)
        {
            var nullRule = finding.SourceType == "<method group>"
                           || model.GetSymbolInfo(expression).Symbol is IMethodSymbol
                ? VarianceUseSiteRules.MethodGroupConversion
                : VarianceUseSiteRules.DynamicOrUnbound;
            return Unsupported(
                nullRule,
                finding.SourceType,
                finding.TargetType,
                direction,
                nullRule == VarianceUseSiteRules.MethodGroupConversion
                    ? "generic delegate method-group conversion requires an explicit lambda before the cast (VR-07)"
                    : "source expression type is unbound/dynamic (VR-08)",
                path,
                line,
                column,
                expression);
        }

        if (targetType == null)
        {
            return Unsupported(
                VarianceUseSiteRules.DynamicOrUnbound,
                sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                finding.TargetType,
                direction,
                "target type could not be bound (dynamic/unbound, VR-08)",
                path,
                line,
                column,
                expression);
        }

        if (sourceType is IDynamicTypeSymbol
            || sourceType.TypeKind == TypeKind.Error
            || targetType.TypeKind == TypeKind.Error
            || use is { MemberResolutionIncomplete: true }
            || use is { EnclosedByDynamic: true })
        {
            return Unsupported(
                VarianceUseSiteRules.DynamicOrUnbound,
                Disp(sourceType),
                Disp(targetType),
                direction,
                "dynamic or unbound symbol prevents a provable rewrite (VR-08)",
                path,
                line,
                column,
                expression);
        }

        if (IsRefLikeOrPointer(sourceType) || IsRefLikeOrPointer(targetType))
        {
            return Unsupported(
                VarianceUseSiteRules.ValueTypeArgument,
                Disp(sourceType),
                Disp(targetType),
                direction,
                "ref-like/pointer type cannot be boxed through (object) (safety condition 4)",
                path,
                line,
                column,
                expression);
        }

        if (IsRefOrOutOrInArgument(expression))
        {
            return Unsupported(
                VarianceUseSiteRules.BothFlow,
                Disp(sourceType),
                Disp(targetType),
                direction,
                "site crosses a ref/out/in argument; write-back cannot be excluded (safety condition 3/4)",
                path,
                line,
                column,
                expression);
        }

        if (IsInRefReturningMember(expression, model))
        {
            return Unsupported(
                VarianceUseSiteRules.BothFlow,
                Disp(sourceType),
                Disp(targetType),
                direction,
                "site is the value of a ref-returning member; reference flow cannot be proven (safety condition 4)",
                path,
                line,
                column,
                expression);
        }

        var pair = ClassifyPair(sourceType, targetType);
        var (rule, expectedDirection) = pair switch
        {
            VariancePairKind.Covariant => (VarianceUseSiteRules.ProducerConversion, VarianceConversionDirection.Covariant),
            VariancePairKind.Contravariant => (VarianceUseSiteRules.ConsumerConversion, VarianceConversionDirection.Contravariant),
            VariancePairKind.Array => (VarianceUseSiteRules.ArrayCovariance, VarianceConversionDirection.Array),
            VariancePairKind.NestedInvariant => (VarianceUseSiteRules.NestedInvariant, direction),
            VariancePairKind.ValueType => (VarianceUseSiteRules.ValueTypeArgument, direction),
            VariancePairKind.Both => (VarianceUseSiteRules.BothFlow, direction),
            _ => (VarianceUseSiteRules.BothFlow, direction),
        };

        switch (pair)
        {
            case VariancePairKind.ValueType:
                return Unsupported(
                    rule, Disp(sourceType), Disp(targetType), direction,
                    "value-type type argument: boxing/erasure semantics differ (VR-06)",
                    path, line, column, expression);
            case VariancePairKind.NestedInvariant:
                return Unsupported(
                    rule, Disp(sourceType), Disp(targetType), direction,
                    "nested invariant type argument between variant layers (VR-03)",
                    path, line, column, expression);
            case VariancePairKind.Both:
                return Unsupported(
                    rule, Disp(sourceType), Disp(targetType), direction,
                    "both-direction variance flow; the boundary is not provably read-only (VR-05)",
                    path, line, column, expression);
            case VariancePairKind.None:
                return Unsupported(
                    rule, Disp(sourceType), Disp(targetType), direction,
                    "variance direction could not be re-derived from the shared audit pair comparison (VR-05)",
                    path, line, column, expression);
            case VariancePairKind.Array:
                // VR-04: Java arrays are covariant, so the C# reference-array conversion
                // is legal as-is; verify the element mapping has no boxing difference.
                return Preserved(
                    rule, Disp(sourceType), Disp(targetType), direction,
                    "Java reference arrays are covariant; element mapping is reference-to-reference (VR-04)",
                    path, line, column, expression);
        }

        // Keep the shared audit authoritative: the re-derived pair must agree with
        // the VA010/VA011 direction the classifier recorded.
        if (finding.Direction != expectedDirection
            || UseSiteSafetyClassifier.ComparePair(sourceType, targetType) != expectedDirection)
        {
            return Unsupported(
                rule, Disp(sourceType), Disp(targetType), direction,
                "re-derived variance direction disagrees with the shared audit classification",
                path, line, column, expression);
        }

        if (use is { Flow: UseFlow.Both })
        {
            return Unsupported(
                VarianceUseSiteRules.BothFlow,
                Disp(sourceType), Disp(targetType), direction,
                "shared classifier refines this site to Both; read-only flow is not proven (VR-05)",
                path, line, column, expression);
        }

        if (candidate.Position is SitePosition.Initializer or SitePosition.Assignment)
        {
            if (!TryProveReadOnlyLocal(candidate, use, out var localReason))
            {
                return Unsupported(
                    VarianceUseSiteRules.BothFlow,
                    Disp(sourceType), Disp(targetType), direction, localReason,
                    path, line, column, expression);
            }
        }
        else if (use != null)
        {
            var expectedFlow = candidate.Position == SitePosition.Argument
                ? UseFlow.Consumer
                : UseFlow.Producer;
            if (use.Flow != expectedFlow)
            {
                return Unsupported(
                    VarianceUseSiteRules.BothFlow,
                    Disp(sourceType), Disp(targetType), direction,
                    $"shared classifier refines the site to {use.Flow}, expected {expectedFlow} (VR-05)",
                    path, line, column, expression);
            }
        }

        if (IsMutatingTarget(targetType))
        {
            return Unsupported(
                VarianceUseSiteRules.BothFlow,
                Disp(sourceType), Disp(targetType), direction,
                "the Java mapping of the target type exposes mutating member(s) the C# contract does not have (VR-05)",
                path, line, column, expression);
        }

        if (!JavaRawTypeMatches(sourceType, targetType))
        {
            return Unsupported(
                rule, Disp(sourceType), Disp(targetType), direction,
                "Java raw mapping verification failed (ConversionContext.MapType) (safety condition 2)",
                path, line, column, expression);
        }

        var safety = pair == VariancePairKind.Contravariant
            ? "reference type arguments; same raw delegate type (MapType-stable); read-only argument position; " +
              "not ref/out/in or ref-like; per-file parse+semantics revalidated"
            : "reference type arguments; same raw type (MapType-stable); read-only producer position; " +
              "no mutating mapped member; not ref/out/in or ref-like; per-file parse+semantics revalidated";

        return new PendingSite(
            new VarianceUseSiteRecord(
                Rule: rule,
                SourceType: Disp(sourceType),
                TargetType: Disp(targetType),
                Direction: direction.ToString(),
                Safety: safety,
                Action: VarianceUseSiteActions.Rewritten,
                Rewritten: true,
                Code: null,
                Reason: null,
                FilePath: path,
                Line: line,
                Column: column),
            expression,
            targetType);
    }

    private static bool TryProveReadOnlyLocal(
        SourceCandidate candidate,
        VarianceUseRecord? use,
        out string reason)
    {
        if (candidate.TargetSymbol is not ILocalSymbol)
        {
            reason = "variance target is not a local variable; write-back from other members cannot be excluded";
            return false;
        }

        if (use == null)
        {
            reason = "local read-only flow could not be classified; refusing the rewrite (VR-05)";
            return false;
        }

        if (use.Flow != UseFlow.Producer)
        {
            reason = $"local is used as {use.Flow}, so a read-only producer flow is not proven (VR-05)";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsMutatingTarget(ITypeSymbol targetType)
    {
        if (!_options.CheckMappedMutability)
        {
            return false;
        }

        var entry = _mutability.Lookup(targetType);
        if (entry is { MutatingMembers.Count: > 0 })
        {
            return true;
        }

        return _mutability.IsConservativelyMutable(targetType);
    }

    /// <summary>
    /// Safety condition 2: identical raw definitions are the precondition for
    /// <see cref="ConversionContext.MapType"/> stability. The mapping is checked
    /// explicitly when a mapping context is available; raw identity remains the
    /// proof when the (configuration-dependent) mapper cannot be constructed.
    /// </summary>
    private bool JavaRawTypeMatches(ITypeSymbol sourceType, ITypeSymbol targetType)
    {
        if (sourceType is IArrayTypeSymbol || targetType is IArrayTypeSymbol)
        {
            // Arrays are handled by VR-04 (preserved); never cast through (object).
            return false;
        }

        if (sourceType is not INamedTypeSymbol sourceNamed
            || targetType is not INamedTypeSymbol targetNamed
            || !SymbolEqualityComparer.Default.Equals(
                sourceNamed.OriginalDefinition, targetNamed.OriginalDefinition))
        {
            return false;
        }

        var context = GetMappingContext();
        if (context == null)
        {
            return true;
        }

        try
        {
            return string.Equals(
                context.MapType(sourceNamed.OriginalDefinition),
                context.MapType(targetNamed.OriginalDefinition),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private ConversionContext? GetMappingContext()
    {
        if (_mappingAttempted)
        {
            return _mappingContext;
        }

        _mappingAttempted = true;
        try
        {
            _mappingContext = new ConversionContext(
                new ConversionOptions(),
                new TypeMappingRegistry(new TypeMappingConfig()));
        }
        catch
        {
            _mappingContext = null;
        }

        return _mappingContext;
    }

    private static bool IsRefLikeOrPointer(ITypeSymbol type)
        => type.IsRefLikeType
           || type.TypeKind == TypeKind.Pointer
           || type.TypeKind == TypeKind.FunctionPointer;

    private static bool IsRefOrOutOrInArgument(ExpressionSyntax expression)
    {
        foreach (var argument in expression.Ancestors().OfType<ArgumentSyntax>())
        {
            if (!argument.Expression.Span.Contains(expression.Span))
            {
                continue;
            }

            return argument.RefKindKeyword.Kind() != SyntaxKind.None;
        }

        return false;
    }

    private static bool IsInRefReturningMember(ExpressionSyntax expression, SemanticModel model)
        => model.GetEnclosingSymbol(expression.SpanStart) is IMethodSymbol method
           && (method.ReturnsByRef || method.ReturnsByRefReadonly);

    private static string Disp(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    // ---- source expression resolution ----

    private static SyntaxNode? ResolveAnchor(CompilationUnitSyntax root, int line, int column)
    {
        if (line <= 0 || column <= 0)
        {
            return null;
        }

        var text = root.SyntaxTree.GetText();
        var lineIndex = line - 1;
        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            return null;
        }

        var position = text.Lines[lineIndex].Start + (column - 1);
        if (position < 0 || position > root.FullSpan.End)
        {
            return null;
        }

        var token = root.FindToken(position, findInsideTrivia: false);
        foreach (var node in token.Parent!.AncestorsAndSelf())
        {
            switch (node)
            {
                case ReturnStatementSyntax statement when statement.SpanStart == position:
                    return statement;
                case AssignmentExpressionSyntax assignment when assignment.Left.SpanStart == position:
                    return assignment;
                case ArgumentSyntax argument when argument.Expression.SpanStart == position:
                    return argument.Expression;
                case TypeSyntax type
                    when type.SpanStart == position && type.Parent is VariableDeclarationSyntax:
                    return type;
            }

            if (node is MemberDeclarationSyntax)
            {
                break;
            }
        }

        return null;
    }

    private static IReadOnlyList<SourceCandidate> ResolveCandidates(
        SyntaxNode anchor,
        SemanticModel model)
    {
        switch (anchor)
        {
            case ExpressionSyntax expression
                when expression.Parent is ArgumentSyntax argument && argument.Expression == expression:
                return new[] { new SourceCandidate(expression, SitePosition.Argument, null) };
            case AssignmentExpressionSyntax assignment:
                return new[]
                {
                    new SourceCandidate(
                        assignment.Right,
                        SitePosition.Assignment,
                        model.GetSymbolInfo(assignment.Left).Symbol),
                };
            case ReturnStatementSyntax { Expression: { } returned }:
                return new[] { new SourceCandidate(returned, SitePosition.Return, null) };
            case TypeSyntax type when type.Parent is VariableDeclarationSyntax declaration:
                var candidates = new List<SourceCandidate>();
                foreach (var declarator in declaration.Variables)
                {
                    if (declarator.Initializer is not { } initializer)
                    {
                        continue;
                    }

                    var info = model.GetTypeInfo(initializer.Value);
                    if (info.Type == null
                        || info.ConvertedType == null
                        || SymbolEqualityComparer.Default.Equals(info.Type, info.ConvertedType))
                    {
                        continue;
                    }

                    if (!model.GetConversion(initializer.Value).IsImplicit)
                    {
                        continue;
                    }

                    candidates.Add(new SourceCandidate(
                        initializer.Value,
                        SitePosition.Initializer,
                        model.GetDeclaredSymbol(declarator)));
                }

                return candidates;
            default:
                return Array.Empty<SourceCandidate>();
        }
    }

    // ---- pair classification (detail layer over the shared direction) ----

    internal enum VariancePairKind
    {
        None,
        Covariant,
        Contravariant,
        Array,
        Both,
        NestedInvariant,
        ValueType,
    }

    /// <summary>
    /// Detail classification used for VR-03/05/06 selection. The direction itself
    /// stays owned by <see cref="UseSiteSafetyClassifier.ComparePair"/>; this
    /// helper only inspects type-argument kinds/subtyping to pick the precise VR
    /// rule. Exposed internally for direct unit tests of the value-type rule.
    /// </summary>
    internal static VariancePairKind ClassifyPair(ITypeSymbol source, ITypeSymbol target)
    {
        if (target is IArrayTypeSymbol targetArray && source is IArrayTypeSymbol sourceArray)
        {
            if (targetArray.Rank != sourceArray.Rank
                || !targetArray.ElementType.IsReferenceType
                || !sourceArray.ElementType.IsReferenceType)
            {
                return VariancePairKind.ValueType;
            }

            return UseSiteSafetyClassifier.IsDerivedFrom(
                sourceArray.ElementType, targetArray.ElementType)
                ? VariancePairKind.Array
                : VariancePairKind.NestedInvariant;
        }

        if (target is not INamedTypeSymbol targetNamed
            || source is not INamedTypeSymbol sourceNamed)
        {
            return VariancePairKind.None;
        }

        var original = targetNamed.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(sourceNamed.OriginalDefinition, original))
        {
            return VariancePairKind.None;
        }

        var covariant = false;
        var contravariant = false;
        var valueType = false;
        var invariant = false;
        var count = Math.Min(sourceNamed.TypeArguments.Length, original.TypeParameters.Length);
        for (var i = 0; i < count; i++)
        {
            var sourceArgument = sourceNamed.TypeArguments[i];
            var targetArgument = targetNamed.TypeArguments[i];
            if (SymbolEqualityComparer.Default.Equals(sourceArgument, targetArgument))
            {
                continue;
            }

            if (!sourceArgument.IsReferenceType || !targetArgument.IsReferenceType)
            {
                valueType = true;
                continue;
            }

            var parameter = original.TypeParameters[i];
            if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.Out)
            {
                if (UseSiteSafetyClassifier.IsDerivedFrom(sourceArgument, targetArgument))
                {
                    covariant = true;
                }
                else
                {
                    invariant = true;
                }
            }
            else if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.In)
            {
                if (UseSiteSafetyClassifier.IsDerivedFrom(targetArgument, sourceArgument))
                {
                    contravariant = true;
                }
                else
                {
                    invariant = true;
                }
            }
            else
            {
                invariant = true;
            }
        }

        if (valueType)
        {
            return VariancePairKind.ValueType;
        }

        if (invariant)
        {
            return VariancePairKind.NestedInvariant;
        }

        if (covariant && contravariant)
        {
            return VariancePairKind.Both;
        }

        if (covariant)
        {
            return VariancePairKind.Covariant;
        }

        return contravariant ? VariancePairKind.Contravariant : VariancePairKind.None;
    }

    // ---- emission, validation, rollback ----

    private List<VarianceUseSiteRecord> Emit(
        IReadOnlyList<PendingSite> pending,
        List<VarianceUseSiteDiagnostic> diagnostics,
        string path)
    {
        var records = new List<VarianceUseSiteRecord>(pending.Count);
        foreach (var site in pending)
        {
            var record = site.Record with { FilePath = path };
            records.Add(record);
            Statistics.Add(record);
            _sites.Add(record);

            if (record.Action == VarianceUseSiteActions.DeclaredUnsupported)
            {
                diagnostics.Add(new VarianceUseSiteDiagnostic(
                    _options.Strict ? VarianceUseSiteSeverity.Error : VarianceUseSiteSeverity.Warning,
                    VarianceUseSiteDiagnosticCode.CS2J1404,
                    $"{record.Rule} declared unsupported: {record.SourceType} -> {record.TargetType}: {record.Reason}",
                    path,
                    record.Line,
                    record.Column));
            }
        }

        return records;
    }

    private string? ValidateRewrittenFile(
        SemanticModel originalModel,
        CompilationUnitSyntax newRoot,
        string outputCode)
    {
        // 1. Parse validation: the rewritten file must still parse.
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                outputCode, new CSharpParseOptions(LanguageVersion.Latest));
            var parseErrors = reparsed.GetDiagnostics()
                .Where(d => d.Severity == RoslynDiagnosticSeverity.Error)
                .ToList();
            if (parseErrors.Count > 0)
            {
                return $"parse validation failed ({parseErrors[0].Id}: {parseErrors[0].GetMessage()})";
            }
        }
        catch (Exception exception)
        {
            return "parse validation threw: " + exception.Message;
        }

        // 2. Semantic revalidation: re-bind the changed tree inside the same
        //    whole-tree compilation and refuse the file if the rewrite introduced
        //    new binding errors.
        if (_compilation == null)
        {
            return "compilation is not bound";
        }

        try
        {
            var originalTree = originalModel.SyntaxTree;
            var parseOptions = originalTree.Options as CSharpParseOptions
                               ?? new CSharpParseOptions(LanguageVersion.Latest);
            var newTree = CSharpSyntaxTree.Create(
                newRoot, parseOptions, originalTree.FilePath, originalTree.Encoding);
            var newCompilation = _compilation.ReplaceSyntaxTree(originalTree, newTree);
            var newModel = newCompilation.GetSemanticModel(newTree);

            var before = originalModel.GetDiagnostics()
                .Count(d => d.Severity == RoslynDiagnosticSeverity.Error);
            var after = newModel.GetDiagnostics()
                .Count(d => d.Severity == RoslynDiagnosticSeverity.Error);
            if (after > before)
            {
                return $"semantic validation found {after - before} new binding error(s)";
            }
        }
        catch (Exception exception)
        {
            return "semantic validation threw: " + exception.Message;
        }

        return null;
    }

    private static VarianceUseSiteNormalizeResult Unchanged(
        string sourceCode,
        IReadOnlyList<VarianceUseSiteDiagnostic> diagnostics,
        IReadOnlyList<VarianceUseSiteRecord> records)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Diagnostics = diagnostics,
            Sites = records,
        };

    private static PendingSite Unsupported(
        string rule,
        string sourceType,
        string targetType,
        VarianceConversionDirection direction,
        string reason,
        string path,
        int line,
        int column,
        ExpressionSyntax? expression = null)
        => new(
            new VarianceUseSiteRecord(
                Rule: rule,
                SourceType: sourceType,
                TargetType: targetType,
                Direction: direction.ToString(),
                Safety: reason,
                Action: VarianceUseSiteActions.DeclaredUnsupported,
                Rewritten: false,
                Code: nameof(VarianceUseSiteDiagnosticCode.CS2J1404),
                Reason: reason,
                FilePath: path,
                Line: line,
                Column: column,
                NormCode: PassConstructIds.GenericVarianceUse),
            expression,
            null);

    private static PendingSite PreservedArraySite(
        VarianceConversionFinding finding,
        string path)
        => Preserved(
            VarianceUseSiteRules.ArrayCovariance,
            finding.SourceType,
            finding.TargetType,
            VarianceConversionDirection.Array,
            "Java reference arrays are covariant; array edge preserved without rewrite (VR-04)",
            path,
            finding.Line,
            finding.Column);

    private static PendingSite Preserved(
        string rule,
        string sourceType,
        string targetType,
        VarianceConversionDirection direction,
        string reason,
        string path,
        int line,
        int column,
        ExpressionSyntax? expression = null)
        => new(
            new VarianceUseSiteRecord(
                Rule: rule,
                SourceType: sourceType,
                TargetType: targetType,
                Direction: direction.ToString(),
                Safety: reason,
                Action: VarianceUseSiteActions.Preserved,
                Rewritten: false,
                Code: null,
                Reason: null,
                FilePath: path,
                Line: line,
                Column: column),
            expression,
            null);

    private sealed record PendingSite(
        VarianceUseSiteRecord Record,
        ExpressionSyntax? Expression,
        ITypeSymbol? TargetType);

    private enum SitePosition
    {
        Argument,
        Return,
        Initializer,
        Assignment,
    }

    private sealed record SourceCandidate(
        ExpressionSyntax Expression,
        SitePosition Position,
        ISymbol? TargetSymbol);

    /// <summary>
    /// Wrap one source expression in <c>(T)(object)expr</c>. Operands whose grammar
    /// precedence would otherwise bind the <c>(object)</c> cast to a sub-expression
    /// (binary/conditional/assignment/…) are parenthesized.
    /// </summary>
    private static ExpressionSyntax BuildCast(
        ExpressionSyntax original,
        ITypeSymbol target)
    {
        var targetType = ParseTypeName(
            target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        var objectType = PredefinedType(Token(SyntaxKind.ObjectKeyword));
        var operand = NeedsOperandParentheses(original)
            ? ParenthesizedExpression(original.WithoutTrivia())
            : original.WithoutTrivia();
        var cast = CastExpression(targetType, CastExpression(objectType, operand));
        return cast.WithTriviaFrom(original);
    }

    private static bool NeedsOperandParentheses(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax
            or GenericNameSyntax
            or QualifiedNameSyntax
            or AliasQualifiedNameSyntax
            or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax
            or ParenthesizedExpressionSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax
            or LiteralExpressionSyntax
            or TypeOfExpressionSyntax
            or DefaultExpressionSyntax
            or PostfixUnaryExpressionSyntax
            or PrefixUnaryExpressionSyntax
            or CastExpressionSyntax
            or CheckedExpressionSyntax
            or AwaitExpressionSyntax => false,
        _ => true,
    };
}

internal sealed record VarianceUseSiteReport(
    int SchemaVersion,
    VarianceUseSiteReportOptions Options,
    IReadOnlyList<VarianceUseSiteRecord> Sites,
    VarianceUseSiteReportTotals Totals);

internal sealed record VarianceUseSiteReportOptions(
    bool Rewrite,
    bool Strict,
    bool CheckMappedMutability);

internal sealed record VarianceUseSiteReportTotals(
    int FilesScanned,
    int FilesTransformed,
    int FilesSkippedClean,
    int FilesRolledBack,
    int SitesTotal,
    int SitesRewritten,
    int SitesPreserved,
    int SitesDeclaredUnsupported,
    IReadOnlyDictionary<string, int> RewrittenByRule,
    IReadOnlyDictionary<string, int> PreservedByRule,
    IReadOnlyDictionary<string, int> UnsupportedByRule);
