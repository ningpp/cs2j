using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>
/// Core entry for the thread-static pass (C09): bind the whole-tree compilation once,
/// analyze symbol-level field plans, rewrite one file at a time, and run the residual gate.
/// </summary>
public sealed class ThreadStaticPreprocessor
{
    private Compilation? _compilation;
    private ThreadStaticOptions _options = new();
    private List<ThreadStaticFieldPlan> _plans = new();
    private List<ThreadStaticDiagnostic> _analysisDiagnostics = new();

    /// <summary>Field plans produced by the last <see cref="Analyze"/> call.</summary>
    public IReadOnlyList<ThreadStaticFieldPlan> Plans => _plans;

    public ThreadStaticOptions Options => _options;

    public void BindCompilation(Compilation compilation) => _compilation = compilation;

    public IReadOnlyList<ThreadStaticFieldPlan> Analyze(ThreadStaticOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = options ?? new ThreadStaticOptions();
        var analysis = new ThreadStaticAnalyzer(_compilation, _options).Analyze();
        _plans = analysis.Plans.ToList();
        _analysisDiagnostics = analysis.Diagnostics.ToList();
        return _plans;
    }

    /// <summary>
    /// Deactivates plans by field id (overrides or rollback ownership). Their
    /// plan-scoped analysis diagnostics are withdrawn (the field is replayed to
    /// its original form). Returns the number excluded.
    /// </summary>
    public int ExcludePlans(IReadOnlySet<string> fieldIds)
    {
        var excluded = 0;
        foreach (var plan in _plans)
        {
            if (plan.Active && fieldIds.Contains(plan.Id))
            {
                plan.Active = false;
                plan.FailCode = ThreadStaticCodes.PlanNotSelfContained;
                plan.FailReason = "Plan excluded by configuration or rollback ownership.";
                _analysisDiagnostics.RemoveAll(d => d.PlanId == plan.Id);
                excluded++;
            }
        }

        return excluded;
    }

    /// <summary>Rewrite the syntax tree backing <paramref name="model"/> (the same tree instance the plans were built from).</summary>
    public ThreadStaticFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        var stats = new ThreadStaticStatistics { FilesScanned = 1 };
        var diagnostics = new List<ThreadStaticDiagnostic>();
        var tree = model.SyntaxTree;

        var sitesByNode = new Dictionary<SyntaxNode, ReferenceSite>();
        var planBySiteNode = new Dictionary<SyntaxNode, ThreadStaticFieldPlan>();
        var plansByDeclaration = new Dictionary<FieldDeclarationSyntax, IReadOnlyList<ThreadStaticFieldPlan>>();

        foreach (var plan in _plans)
        {
            if (plan.Declaration.SyntaxTree == tree)
            {
                stats.FieldsDetected++;
                if (!plan.Active)
                {
                    stats.FieldsSkipped++;
                    if (plan.FailCode != null)
                    {
                        stats.SkippedByReason[plan.FailCode] =
                            stats.SkippedByReason.GetValueOrDefault(plan.FailCode) + 1;
                    }

                    stats.InfeasibleSites += plan.Sites.Count(s =>
                        s.Kind is ThreadStaticSiteKind.RefOut or ThreadStaticSiteKind.StructMutation);
                }
            }

            if (!plan.Active) continue;

            foreach (var site in plan.Sites)
            {
                if (site.Node.SyntaxTree == tree)
                {
                    sitesByNode[site.Node] = site;
                    planBySiteNode[site.Node] = plan;
                }
            }
        }

        // Register declarations that carry at least one active variable (may split with skipped siblings).
        foreach (var declaration in _plans.Where(p => p.Active && p.Declaration.SyntaxTree == tree)
                     .Select(p => p.Declaration).Distinct())
        {
            plansByDeclaration[declaration] = _plans
                .Where(p => p.Declaration == declaration)
                .OrderBy(p => p.Declarator.SpanStart)
                .ToList();
        }

        var cctorInjections = BuildCctorInjections(tree);

        AppendAnalysisDiagnostics(diagnostics, tree.FilePath);

        if (sitesByNode.Count == 0 && plansByDeclaration.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            return new ThreadStaticFileResult(tree.ToString(), false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)tree.GetRoot();
        var rewriter = new ThreadStaticRewriter(
            sitesByNode, planBySiteNode, plansByDeclaration, cctorInjections, _options, stats, root);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        if (newRoot == root)
        {
            stats.FilesSkippedClean = 1;
            return new ThreadStaticFileResult(tree.ToString(), false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new ThreadStaticFileResult(FormatAndValidate(newRoot), true, diagnostics, stats);
    }

    /// <summary>
    /// Builds the TS-10 / FirstThreadFaithful cctor injection map for one tree.
    /// All initialized fields of a type are injected together in global
    /// declaration order: into an existing explicit static constructor (its
    /// part wins), otherwise a synthesized cctor in the part that declares the
    /// earliest initialized field.
    /// </summary>
    private IReadOnlyDictionary<TypeDeclarationSyntax, ThreadStaticCctorInjection> BuildCctorInjections(
        SyntaxTree tree)
    {
        var result = new Dictionary<TypeDeclarationSyntax, ThreadStaticCctorInjection>();
        if (_options.InitializerPolicy != ThreadStaticInitializerPolicy.FirstThreadFaithful)
        {
            return result;
        }

        var initialized = _plans
            .Where(p => p.Active
                && p.InitializerKind == ThreadStaticInitializerKind.Inline
                && p.Declarator.Initializer != null)
            .OrderBy(p => p, Comparer<ThreadStaticFieldPlan>.Create((a, b) => a.CompareTo(b)))
            .ToList();
        if (initialized.Count == 0) return result;

        var root = (CompilationUnitSyntax)tree.GetRoot();

        foreach (var typeGroup in initialized
                     .Where(p => p.Field.ContainingType != null)
                     .GroupBy(p => p.Field.ContainingType, SymbolEqualityComparer.Default)
                     .OrderBy(g => g.Min(p => p.Declarator.SpanStart)))
        {
            var ordered = typeGroup
                .OrderBy(p => p, Comparer<ThreadStaticFieldPlan>.Create((a, b) => a.CompareTo(b)))
                .ToList();

            var assignments = ordered
                .Select(p => new ThreadStaticCctorAssignment
                {
                    Plan = p,
                    FieldName = p.Declarator.Identifier.ValueText,
                    InitializerText = p.Declarator.Initializer!.Value.ToString(),
                    InitializerIsLocal = p.Declaration.SyntaxTree == tree,
                })
                .ToList();

            var explicitCctor = FindExplicitStaticConstructor((INamedTypeSymbol)typeGroup.Key!);
            if (explicitCctor != null)
            {
                if (explicitCctor.SyntaxTree != tree) continue;
                var cctorNode = (ConstructorDeclarationSyntax)explicitCctor;
                var typeNode = cctorNode.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (typeNode == null || !root.DescendantNodes().Contains(typeNode)) continue;
                result[typeNode] = new ThreadStaticCctorInjection
                {
                    ExistingCctor = cctorNode,
                    Assignments = assignments,
                };
                continue;
            }

            // No explicit cctor: the part holding the earliest initialized field hosts the synthesized one.
            var first = ordered[0];
            if (first.Declaration.SyntaxTree != tree) continue;
            var targetType = first.Declaration.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (targetType == null) continue;

            var localFieldMemberIndices = ordered
                .Where(p => p.Declaration.SyntaxTree == tree)
                .Select(p => targetType.Members.IndexOf(p.Declaration))
                .Where(i => i >= 0)
                .ToList();
            var insertAfter = localFieldMemberIndices.Count > 0 ? localFieldMemberIndices.Max() : -1;

            result[targetType] = new ThreadStaticCctorInjection
            {
                ExistingCctor = null,
                Assignments = assignments,
                InsertAfterMemberIndex = insertAfter,
            };
        }

        return result;
    }

    private ConstructorDeclarationSyntax? FindExplicitStaticConstructor(INamedTypeSymbol type)
    {
        var cctor = type.Constructors.FirstOrDefault(c =>
            c.IsStatic && c is { IsImplicitlyDeclared: false } && c.DeclaringSyntaxReferences.Length > 0);
        var reference = cctor?.DeclaringSyntaxReferences.FirstOrDefault();
        return reference?.GetSyntax() as ConstructorDeclarationSyntax
            ?? reference?.GetSyntax().FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
    }

    /// <summary>Post-rewrite residual gate reconciled against the current plan set.</summary>
    public ThreadStaticResidualReport ResidualScan(Compilation rewrittenCompilation)
        => ThreadStaticResidualScanner.Scan(rewrittenCompilation, _plans);

    private void AppendAnalysisDiagnostics(List<ThreadStaticDiagnostic> diagnostics, string filePath)
    {
        diagnostics.AddRange(_analysisDiagnostics.Where(d => d.FilePath == filePath));
    }

    /// <summary>
    /// Normalizes the rewritten unit to a canonical layout; falls back to the raw tree text
    /// if normalization fails for any reason (the node set is small, so full-file formatting is sufficient).
    /// </summary>
    internal static string FormatAndValidate(SyntaxNode root)
    {
        try
        {
            return root.NormalizeWhitespace().ToFullString();
        }
        catch
        {
            return root.ToFullString();
        }
    }
}

/// <summary>One input source for the rollback loop (path + text).</summary>
internal sealed record ThreadStaticSourceFile(string Path, string Content);

/// <summary>A post-rewrite compile error surfaced by the validator, located in the candidate output.</summary>
internal sealed record ThreadStaticCompileError(
    string FilePath,
    TextSpan SourceSpan,
    string Id,
    string Message,
    int Line,
    int Column);

/// <summary>One file as it enters and leaves a rewrite round.</summary>
internal sealed class ThreadStaticRewrittenFile
{
    public required string Path { get; init; }
    public required string Source { get; init; }
    public string Output { get; set; } = "";
    public bool Changed { get; set; }
}

/// <summary>Outcome of the Core rollback loop (the CLI adapter drives the file system around it).</summary>
internal sealed class ThreadStaticRollbackResult
{
    /// <summary>True when the final candidate introduced no new compile errors.</summary>
    public required bool Success { get; init; }

    /// <summary>Final per-file outputs; on whole-run rollback every Output equals the original Source.</summary>
    public required IReadOnlyList<ThreadStaticRewrittenFile> Files { get; init; }

    public required IReadOnlyList<ThreadStaticDiagnostic> Diagnostics { get; init; }
    public required ThreadStaticStatistics Statistics { get; init; }
    public required int Rounds { get; init; }

    /// <summary>Plan set of the final accepted round (for post-rewrite residual reconciliation).</summary>
    public required IReadOnlyList<ThreadStaticFieldPlan> FinalPlans { get; init; }

    /// <summary>New errors observed in round one (the unmitigated rewrite).</summary>
    public required IReadOnlyList<ThreadStaticCompileError> FirstRoundErrors { get; init; }
}

/// <summary>
/// Validates a candidate: compiles baseline and candidate and returns the
/// candidate's NEW errors (absent from the baseline), with spans inside the
/// candidate output trees.
/// </summary>
internal delegate IReadOnlyList<ThreadStaticCompileError> ThreadStaticCompileValidator(
    IReadOnlyList<ThreadStaticRewrittenFile> baseline,
    IReadOnlyList<ThreadStaticRewrittenFile> candidate);

/// <summary>
/// TS rollback loop (design §4.3 / §7.4). Every round replays from the ORIGINAL
/// sources with the cumulative excluded-plan set, rewrites, and recompiles.
/// New errors attributed to exactly one active plan exclude that plan
/// (CS2J1206); anything unattributable (zero or multiple owners) rolls back
/// the whole run to the original bytes (CS2J1200).
/// </summary>
internal static class ThreadStaticRollbackRunner
{
    public const int DefaultMaxRounds = 1024;

    /// <summary>
    /// A [ThreadStatic] field can only exist when the attribute is spelled out in
    /// the same file it decorates (attribute names and their <c>using</c> aliases
    /// are file-scoped), so a forest with no textual occurrence of the attribute
    /// name cannot contain a single candidate. That is a cheap necessary
    /// precondition that lets the whole rollback loop be skipped: with no
    /// candidates there are no plans, no rewrites, and necessarily an empty
    /// residual report (both the analyzer and the residual scanner reject every
    /// field whose bound attribute is absent).
    /// </summary>
    private static bool MayContainCandidates(IReadOnlyList<ThreadStaticSourceFile> files)
    {
        foreach (var file in files)
        {
            if (file.Content.Contains("ThreadStatic", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static ThreadStaticRollbackResult Run(
        IReadOnlyList<ThreadStaticSourceFile> files,
        ThreadStaticOptions options,
        Func<IReadOnlyList<ThreadStaticSourceFile>, CSharpCompilation> compilationFactory,
        ThreadStaticCompileValidator validator,
        int maxRounds = DefaultMaxRounds)
    {
        var baseline = files
            .Select(f => new ThreadStaticRewrittenFile { Path = f.Path, Source = f.Content, Output = f.Content })
            .ToList();

        if (!MayContainCandidates(files))
        {
            // Nothing to analyze, rewrite, validate or reconcile: every file is
            // already its own (unchanged) output.
            return new ThreadStaticRollbackResult
            {
                Success = true,
                Files = baseline,
                Diagnostics = Array.Empty<ThreadStaticDiagnostic>(),
                Statistics = new ThreadStaticStatistics
                {
                    FilesScanned = files.Count,
                    FilesSkippedClean = files.Count,
                    RollbackRounds = 1,
                },
                Rounds = 1,
                FinalPlans = Array.Empty<ThreadStaticFieldPlan>(),
                FirstRoundErrors = Array.Empty<ThreadStaticCompileError>(),
            };
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<ThreadStaticCompileError> firstRoundErrors = Array.Empty<ThreadStaticCompileError>();
        IReadOnlyList<ThreadStaticFieldPlan> finalPlans = Array.Empty<ThreadStaticFieldPlan>();

        for (var round = 1; round <= maxRounds; round++)
        {
            var preprocessor = new ThreadStaticPreprocessor();
            var baselineSources = baseline
                .Select(f => new ThreadStaticSourceFile(f.Path, f.Source))
                .ToList();
            var compilation = compilationFactory(baselineSources);
            preprocessor.BindCompilation(compilation);
            preprocessor.Analyze(options);
            if (excluded.Count > 0)
            {
                preprocessor.ExcludePlans(excluded);
            }

            var roundStats = new ThreadStaticStatistics();
            var roundDiagnostics = new List<ThreadStaticDiagnostic>();
            var candidate = RewriteAll(baseline, compilation, preprocessor, roundStats, roundDiagnostics);
            finalPlans = preprocessor.Plans;

            // A candidate in which nothing changed is byte-identical to the baseline
            // sources, so the diff against the baseline error set is necessarily empty.
            // Skipping the (full-forest) recompile is therefore equivalent.
            var newErrors = candidate.Any(f => f.Changed)
                ? validator(baseline, candidate)
                : (IReadOnlyList<ThreadStaticCompileError>)Array.Empty<ThreadStaticCompileError>();
            if (round == 1)
            {
                firstRoundErrors = newErrors;
            }

            if (newErrors.Count == 0)
            {
                roundStats.PlansRolledBack = excluded.Count;
                roundStats.RollbackRounds = round;
                if (excluded.Count > 0)
                {
                    foreach (var planId in excluded.OrderBy(id => id, StringComparer.Ordinal))
                    {
                        var plan = preprocessor.Plans.FirstOrDefault(p => p.Id == planId);
                        roundDiagnostics.Add(new ThreadStaticDiagnostic(
                            ThreadStaticSeverity.Warning,
                            ThreadStaticRule.Rollback,
                            ThreadStaticCodes.PlanRolledBack,
                            $"Field plan '{planId}' was excluded after a recompiled error was attributed to it; the field was replayed in its original form.",
                            plan?.Declaration.SyntaxTree.FilePath,
                            plan != null
                                ? plan.Declaration.SyntaxTree.GetLineSpan(plan.Declarator.Span).StartLinePosition.Line + 1
                                : null,
                            planId));
                    }
                }

                return new ThreadStaticRollbackResult
                {
                    Success = true,
                    Files = candidate,
                    Diagnostics = roundDiagnostics,
                    Statistics = roundStats,
                    Rounds = round,
                    FinalPlans = finalPlans,
                    FirstRoundErrors = firstRoundErrors,
                };
            }

            var owners = AttributeNewErrors(newErrors, candidate, baseline, preprocessor.Plans);
            var unowned = owners.Where(o => o.Count != 1).ToList();
            if (unowned.Count > 0)
            {
                var rollbackStats = new ThreadStaticStatistics
                {
                    RollbackRounds = round,
                    WholeRunRolledBack = 1,
                };
                var first = newErrors
                    .OrderBy(e => e.FilePath, StringComparer.Ordinal)
                    .ThenBy(e => e.Line)
                    .ThenBy(e => e.Column)
                    .First();
                var anchor = MapErrorToBaseline(first, candidate, baseline);
                var rollbackDiagnostics = new List<ThreadStaticDiagnostic>
                {
                    new(
                        ThreadStaticSeverity.Error,
                        ThreadStaticRule.Rollback,
                        ThreadStaticCodes.WholeTreeRollback,
                        $"The rewritten code introduced compile error {first.Id} that could not be attributed to a single [ThreadStatic] field plan; the whole run was rolled back to the original sources. {first.Message}",
                        anchor.FilePath ?? first.FilePath,
                        anchor.Line ?? first.Line,
                        null),
                };

                foreach (var file in baseline)
                {
                    file.Output = file.Source;
                    file.Changed = false;
                }

                return new ThreadStaticRollbackResult
                {
                    Success = false,
                    Files = baseline,
                    Diagnostics = rollbackDiagnostics,
                    Statistics = rollbackStats,
                    Rounds = round,
                    FinalPlans = finalPlans,
                    FirstRoundErrors = firstRoundErrors,
                };
            }

            foreach (var ownerSet in owners)
            {
                excluded.Add(ownerSet.Single());
            }
        }

        // Exhausted rounds without convergence: whole-run rollback.
        var exhaustedStats = new ThreadStaticStatistics
        {
            RollbackRounds = maxRounds,
            WholeRunRolledBack = 1,
        };
        foreach (var file in baseline)
        {
            file.Output = file.Source;
            file.Changed = false;
        }

        return new ThreadStaticRollbackResult
        {
            Success = false,
            Files = baseline,
            Diagnostics = new[]
            {
                new ThreadStaticDiagnostic(
                    ThreadStaticSeverity.Error,
                    ThreadStaticRule.Rollback,
                    ThreadStaticCodes.WholeTreeRollback,
                    $"Rollback loop did not converge within {maxRounds} rounds; the whole run was rolled back to the original sources.",
                    null, null, null),
            },
            Statistics = exhaustedStats,
            Rounds = maxRounds,
            FinalPlans = finalPlans,
            FirstRoundErrors = firstRoundErrors,
        };
    }

    private static IReadOnlyList<ThreadStaticRewrittenFile> RewriteAll(
        IReadOnlyList<ThreadStaticRewrittenFile> baseline,
        CSharpCompilation compilation,
        ThreadStaticPreprocessor preprocessor,
        ThreadStaticStatistics roundStats,
        List<ThreadStaticDiagnostic> roundDiagnostics)
    {
        var result = new List<ThreadStaticRewrittenFile>(baseline.Count);
        foreach (var file in baseline)
        {
            var tree = compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == file.Path);
            if (tree == null)
            {
                file.Output = file.Source;
                file.Changed = false;
                result.Add(file);
                continue;
            }

            var rewrite = preprocessor.RewriteFile(compilation.GetSemanticModel(tree));
            file.Output = rewrite.OutputCode;
            file.Changed = rewrite.Changed;
            roundStats.Merge(rewrite.Statistics);
            roundDiagnostics.AddRange(rewrite.Diagnostics);
            result.Add(file);
        }

        return result;
    }

    private static List<HashSet<string>> AttributeNewErrors(
        IReadOnlyList<ThreadStaticCompileError> errors,
        IReadOnlyList<ThreadStaticRewrittenFile> candidate,
        IReadOnlyList<ThreadStaticRewrittenFile> baseline,
        IReadOnlyList<ThreadStaticFieldPlan> plans)
    {
        var result = new List<HashSet<string>>();
        foreach (var error in errors)
        {
            var owners = new HashSet<string>(StringComparer.Ordinal);

            // 1) Direct annotation on a synthesized/rewritten node names its plan.
            if (TryFindAnnotatedOwner(error, candidate, plans, out var annotatedOwner))
            {
                owners.Add(annotatedOwner);
                result.Add(owners);
                continue;
            }

            // 2) Map the error member back to the corresponding original member and let plans claim spans.
            if (TryMapToOriginalSpan(error, candidate, baseline, out var filePath, out var originalSpan))
            {
                foreach (var plan in plans.Where(p => p.Active))
                {
                    if (plan.OwnsTouch(filePath, originalSpan))
                    {
                        owners.Add(plan.Id);
                    }
                }
            }

            result.Add(owners);
        }

        return result;
    }

    private static bool TryFindAnnotatedOwner(
        ThreadStaticCompileError error,
        IReadOnlyList<ThreadStaticRewrittenFile> candidate,
        IReadOnlyList<ThreadStaticFieldPlan> plans,
        out string owner)
    {
        owner = "";
        var file = candidate.FirstOrDefault(f => f.Path == error.FilePath);
        if (file == null || !file.Changed) return false;

        var root = CSharpSyntaxTree.ParseText(file.Output, path: file.Path).GetRoot();
        var node = root.FindNode(error.SourceSpan, getInnermostNodeForTie: true);
        var activeIds = new HashSet<string>(
            plans.Where(p => p.Active).Select(p => p.Id), StringComparer.Ordinal);

        foreach (var ancestor in node.AncestorsAndSelf())
        {
            foreach (var annotation in ancestor.GetAnnotations(ThreadStaticAnnotations.Kind))
            {
                if (!string.IsNullOrEmpty(annotation.Data) && activeIds.Contains(annotation.Data))
                {
                    owner = annotation.Data;
                    return true;
                }
            }
        }

        return false;
    }

    private static (string? FilePath, int? Line) MapErrorToBaseline(
        ThreadStaticCompileError error,
        IReadOnlyList<ThreadStaticRewrittenFile> candidate,
        IReadOnlyList<ThreadStaticRewrittenFile> baseline)
    {
        if (!TryMapToOriginalSpan(error, candidate, baseline, out var path, out var span))
        {
            return (null, null);
        }

        var source = baseline.First(f => f.Path == path).Source;
        var line = CSharpSyntaxTree.ParseText(source, path: path)
            .GetLineSpan(span).StartLinePosition.Line + 1;
        return (path, line);
    }

    private static bool TryMapToOriginalSpan(
        ThreadStaticCompileError error,
        IReadOnlyList<ThreadStaticRewrittenFile> candidate,
        IReadOnlyList<ThreadStaticRewrittenFile> baseline,
        out string filePath,
        out TextSpan originalSpan)
    {
        filePath = error.FilePath;
        originalSpan = default;

        var candidateFile = candidate.FirstOrDefault(f => f.Path == error.FilePath);
        var baselineFile = baseline.FirstOrDefault(f => f.Path == error.FilePath);
        if (candidateFile == null || baselineFile == null) return false;

        var candidateRoot = (CompilationUnitSyntax)CSharpSyntaxTree
            .ParseText(candidateFile.Output, path: candidateFile.Path).GetRoot();
        var baselineRoot = (CompilationUnitSyntax)CSharpSyntaxTree
            .ParseText(baselineFile.Source, path: baselineFile.Path).GetRoot();
        var errorNode = candidateRoot.FindNode(error.SourceSpan, getInnermostNodeForTie: true);

        SyntaxNode? originalTarget;

        // Top-level statements: positional correspondence.
        var globalStatement = errorNode.AncestorsAndSelf().OfType<GlobalStatementSyntax>().FirstOrDefault();
        if (globalStatement != null)
        {
            var index = candidateRoot.Members
                .TakeWhile(m => m != globalStatement)
                .Count(m => m is GlobalStatementSyntax);
            originalTarget = baselineRoot.Members.OfType<GlobalStatementSyntax>().ElementAtOrDefault(index);
            if (originalTarget == null) return false;
            originalSpan = originalTarget.Span;
            return true;
        }

        // Walk namespace + type chain to the declaring type in the baseline.
        var typeChain = errorNode.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().ToList();
        SyntaxNode? container = baselineRoot;

        var namespaceNode = errorNode.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceNode != null)
        {
            container = FindNamespace(baselineRoot, namespaceNode);
            if (container == null) return false;
        }

        for (var t = typeChain.Count - 1; t >= 0; t--)
        {
            var typeName = typeChain[t].Identifier.ValueText;
            container = container.ChildNodes().OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(n => n.Identifier.ValueText == typeName);
            if (container == null) return false;
        }

        var typeDeclaration = (TypeDeclarationSyntax)container;
        originalTarget = FindCorrespondingMember(errorNode, typeDeclaration);
        if (originalTarget == null) return false;
        originalSpan = originalTarget.Span;
        return true;
    }

    private static SyntaxNode? FindNamespace(SyntaxNode container, BaseNamespaceDeclarationSyntax target)
    {
        foreach (var candidate in container.ChildNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            if (candidate.Name.ToString() == target.Name.ToString())
            {
                var inner = target.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                return inner == null ? candidate : FindNamespace(candidate, inner) ?? candidate;
            }

            var nested = FindNamespace(candidate, target);
            if (nested != null) return nested;
        }

        return null;
    }

    private static SyntaxNode? FindCorrespondingMember(SyntaxNode errorNode, TypeDeclarationSyntax type)
    {
        // A field declarator pins the exact variable inside a (possibly multi-variable) declaration.
        var declarator = errorNode.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        if (declarator == null
            && errorNode is BaseFieldDeclarationSyntax errorField
            && errorField.Declaration.Variables.Count == 1)
        {
            // The error span covers the whole single-variable field declaration.
            declarator = errorField.Declaration.Variables[0];
        }

        if (declarator != null
            && declarator.Parent?.Parent is MemberDeclarationSyntax declaringMember
            && declaringMember is FieldDeclarationSyntax or EventFieldDeclarationSyntax)
        {
            var fieldName = declarator.Identifier.ValueText;
            var field = type.Members.OfType<BaseFieldDeclarationSyntax>()
                .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName));
            return field?.Declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == fieldName)
                ?? (SyntaxNode?)field;
        }

        var member = errorNode.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member == null) return null;

        var (kind, name, parameters) = Describe(member);
        return type.Members.FirstOrDefault(m => IsSameMember(Describe(m), kind, name, parameters));
    }

    private static (string Kind, string Name, string Parameters) Describe(MemberDeclarationSyntax member)
    {
        var parameters = member switch
        {
            BaseMethodDeclarationSyntax method => ParameterSignature(method.ParameterList),
            IndexerDeclarationSyntax indexer => ParameterSignature(indexer.ParameterList),
            _ => "",
        };

        return member switch
        {
            ConstructorDeclarationSyntax c => ("ctor", c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static" : "instance", parameters),
            MethodDeclarationSyntax m => ("method", m.Identifier.ValueText, parameters),
            PropertyDeclarationSyntax p => ("property", p.Identifier.ValueText, ""),
            EventDeclarationSyntax e => ("event", e.Identifier.ValueText, ""),
            IndexerDeclarationSyntax => ("indexer", "this", parameters),
            OperatorDeclarationSyntax o => ("operator", o.OperatorToken.ValueText, parameters),
            ConversionOperatorDeclarationSyntax o => ("conversion", o.ImplicitOrExplicitKeyword.ValueText + " " + o.Type, parameters),
            DestructorDeclarationSyntax d => ("destructor", d.Identifier.ValueText, ""),
            _ => (member.Kind().ToString(), "", ""),
        };
    }

    private static bool IsSameMember(
        (string Kind, string Name, string Parameters) actual,
        string kind, string name, string parameters)
        => actual.Kind == kind && actual.Name == name && actual.Parameters == parameters;

    private static string ParameterSignature(BaseParameterListSyntax? parameterList)
        => parameterList == null
            ? ""
            : string.Join(",", parameterList.Parameters
                .Select(p => p.Type?.ToString().Trim() ?? "")
                .Where(t => t.Length > 0));
}
