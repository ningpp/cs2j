using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using CSharpToJava.Core.EqualityConcat.Rules;

namespace CSharpToJava.Core.EqualityConcat;

/// <summary>
/// Public entry point of the C47 equality/concat call-site preprocessor.
/// Rewrites C# equality and concatenation sites that would silently change
/// semantics after C#→Java conversion into explicit BCL call sites:
/// <list type="bullet">
/// <item>EC-1 string ==/!= → string.Equals;</item>
/// <item>EC-2 object identity → object.ReferenceEquals;</item>
/// <item>EC-3 left-add chains → single string.Concat;</item>
/// <item>EC-4 string += with single-evaluation temps;</item>
/// <item>EC-6 delegate == → Cs2jPrelude.DelegateOps.Equals;</item>
/// <item>EC-7 record synthesized == → object.Equals.</item>
/// </list>
/// Clean files are byte-level preserved; rewritten output is valid C#.
/// </summary>
public sealed class EqualityConcatPreprocessor
{
    public EqualityConcatResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        EqualityConcatOptions? options = null)
    {
        options ??= new EqualityConcatOptions();
        var stats = new EqualityConcatStatistics { FilesScanned = 1 };
        var diagnostics = new List<EqualityConcatDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!IsPotentiallyDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new EqualityConcatResult(
                sourceCode, false, Array.Empty<EqualityConcatDiagnostic>(), stats);
        }

        var rewriter = new EqualityConcatSyntaxRewriter(
            semanticModel, options, stats, diagnostics, root);
        var pass1Root = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (rewriter.RewriteCount == 0)
        {
            // Dirty syntax but every site is diagnostic-only or intentionally
            // untouched: keep the source bytes. Errors fail the run regardless.
            stats.FilesDirtyNoRewrite = 1;
            return new EqualityConcatResult(
                sourceCode, false, diagnostics, stats);
        }

        var pass2Root = TempLifter.Apply(
            pass1Root, rewriter.Pending, rewriter.AnchorAnnotations);

        var residualCount = CountResidual(pass2Root);
        stats.ResidualCount = residualCount;

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(pass2Root, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        if (residualCount > 0)
        {
            diagnostics.Add(new EqualityConcatDiagnostic(
                EqualityConcatSeverity.Error,
                EqualityConcatDiagnosticCode.CS2J0470,
                $"{residualCount} unaccounted equality/concat site(s) remaining " +
                "after equality-concat preprocessing."));
        }

        return new EqualityConcatResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Cheap textual-shape dirty gate: the file may contain an equality, +
    /// or += candidate. Semantics are applied later by the single classifier;
    /// this only prevents byte-level changes to obviously clean files.
    /// </summary>
    private static bool IsPotentiallyDirty(SyntaxNode root)
        => root.DescendantNodes().Any(static node => node switch
        {
            BinaryExpressionSyntax binary => binary.OperatorToken.Kind()
                is SyntaxKind.EqualsEqualsToken
                    or SyntaxKind.ExclamationEqualsToken
                    or SyntaxKind.PlusToken,
            AssignmentExpressionSyntax assignment => assignment.OperatorToken
                .IsKind(SyntaxKind.PlusEqualsToken),
            _ => false,
        });

    /// <summary>
    /// Residual gate: every candidate binary/+= site left in the output must
    /// either carry the explained annotation (intentionally untouched) or be
    /// nested inside an explained subtree (hoisted temp initializer).
    /// </summary>
    internal static int CountResidual(SyntaxNode root)
    {
        var residual = 0;
        foreach (var node in root.DescendantNodes())
        {
            var isCandidate = node switch
            {
                BinaryExpressionSyntax binary => binary.OperatorToken.Kind()
                    is SyntaxKind.EqualsEqualsToken
                        or SyntaxKind.ExclamationEqualsToken
                        or SyntaxKind.PlusToken,
                AssignmentExpressionSyntax assignment => assignment.OperatorToken
                    .IsKind(SyntaxKind.PlusEqualsToken),
                _ => false,
            };
            if (!isCandidate)
                continue;

            if (node.HasAnnotation(EqualityConcatSyntaxFacts.ExplainedAnnotation))
                continue;

            if (node.Ancestors().Any(a =>
                    a.HasAnnotation(EqualityConcatSyntaxFacts.ExplainedSubtreeAnnotation)))
                continue;

            residual++;
        }

        return residual;
    }

    /// <summary>
    /// Pass 1: bottom-up rewriter for equality/concat sites. Classification
    /// is always performed on the original node (semantic model maps original
    /// trees); replacement rendering uses the already-visited children.
    /// </summary>
    private sealed class EqualityConcatSyntaxRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel? _model;
        private readonly EqualityConcatOptions _options;
        private readonly EqualityConcatStatistics _stats;
        private readonly List<EqualityConcatDiagnostic> _diagnostics;
        private readonly EqualityConcatTempNameAllocator _names;
        private readonly Dictionary<SyntaxNode, EqualityConcatClassification> _classificationCache = new();
        private readonly Dictionary<SyntaxNode, SyntaxNode> _visitedMap = new();
        private int _rewriteCount;
        private int _tempOrder;

        internal List<TempLifter.PendingTemp> Pending { get; } = new();

        private readonly Dictionary<SyntaxNode, SyntaxAnnotation> _anchorAnnotations = new();

        internal IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => _anchorAnnotations;

        internal int RewriteCount => _rewriteCount;

        public EqualityConcatSyntaxRewriter(
            SemanticModel? model,
            EqualityConcatOptions options,
            EqualityConcatStatistics stats,
            List<EqualityConcatDiagnostic> diagnostics,
            CompilationUnitSyntax root)
        {
            _model = model;
            _options = options;
            _stats = stats;
            _diagnostics = diagnostics;
            _names = new EqualityConcatTempNameAllocator(root);
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            RememberVisited(node, visited);

            if (_model == null)
            {
                if (!IsCandidateToken(node.OperatorToken.Kind()))
                    return visited;
                EmitSite(node, new EqualityConcatDiagnostic(
                    EqualityConcatSeverity.Error,
                    EqualityConcatDiagnosticCode.CS2J0470,
                    "A semantic model is required to classify equality/concat sites."));
                return MarkExplained(visited);
            }

            var classification = Classify(node);
            if (classification.Operation == null)
                _stats.FallbackClassifications++;

            var left = visited.Left;
            var right = visited.Right;

            switch (classification.Kind)
            {
                case EqualityConcatKind.StringEquality:
                    _stats.StringEqualsRewritten++;
                    _rewriteCount++;
                    return BinaryEqualityRewriter.RewriteStringEquality(
                        left, right, classification.IsNotEquals);

                case EqualityConcatKind.ObjectIdentity:
                    _stats.ObjectReferenceRewritten++;
                    _rewriteCount++;
                    return BinaryEqualityRewriter.RewriteObjectIdentity(
                        UnwrapRedundantObjectCast(left, _model!),
                        UnwrapRedundantObjectCast(right, _model!),
                        classification.IsNotEquals);

                case EqualityConcatKind.RecordValueEquality:
                    _stats.RecordEqualsRewritten++;
                    _rewriteCount++;
                    return BinaryEqualityRewriter.RewriteRecordValue(
                        left, right, classification.IsNotEquals);

                case EqualityConcatKind.DelegateEquality:
                    return RewriteDelegate(node, visited, classification);

                case EqualityConcatKind.StringConcat:
                    return RewriteConcat(node, visited);

                case EqualityConcatKind.LiftedNullableEquality:
                    _stats.LiftedEqualityPendingC22++;
                    _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0474));
                    EmitSite(node, new EqualityConcatDiagnostic(
                        StrictSeverity(EqualityConcatDiagnosticCode.CS2J0474),
                        EqualityConcatDiagnosticCode.CS2J0474,
                        "Lifted (Nullable<T>) ==/!= site left unchanged; the C22 " +
                        "nullable pass handles it."));
                    return MarkExplained(visited);

                case EqualityConcatKind.TupleEqualityResidual:
                    _stats.TupleNullEdgeSites++;
                    _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0476));
                    EmitSite(node, new EqualityConcatDiagnostic(
                        EqualityConcatSeverity.Warning,
                        EqualityConcatDiagnosticCode.CS2J0476,
                        "Tuple comparison (incl. tuple/null edge after E06) left " +
                        "unchanged."));
                    return MarkExplained(visited);

                case EqualityConcatKind.ExpressionTree:
                    _stats.ExpressionTreeSitesSkipped++;
                    _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0472));
                    EmitSite(node, new EqualityConcatDiagnostic(
                        EqualityConcatSeverity.Info,
                        EqualityConcatDiagnosticCode.CS2J0472,
                        "Site inside an Expression<T> tree was skipped."));
                    return MarkExplained(visited);

                case EqualityConcatKind.Dynamic:
                    _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0471));
                    EmitSite(node, new EqualityConcatDiagnostic(
                        EqualityConcatSeverity.Error,
                        EqualityConcatDiagnosticCode.CS2J0471,
                        "Dynamically bound equality/concatenation site cannot be " +
                        "rewritten; add static typing."));
                    return MarkExplained(visited);

                case EqualityConcatKind.Unknown:
                    _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0470));
                    EmitSite(node, new EqualityConcatDiagnostic(
                        EqualityConcatSeverity.Error,
                        EqualityConcatDiagnosticCode.CS2J0470,
                        "Neither the operation nor the bound symbol can classify " +
                        "this site."));
                    return MarkExplained(visited);

                // Event null checks, predefined primitive operators and source
                // user-defined operators are intentionally untouched.
                default:
                    return MarkExplained(visited);
            }
        }

        public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
            RememberVisited(node, visited);

            if (!node.OperatorToken.IsKind(SyntaxKind.PlusEqualsToken))
                return visited;

            if (_model == null)
            {
                EmitSite(node, new EqualityConcatDiagnostic(
                    EqualityConcatSeverity.Error,
                    EqualityConcatDiagnosticCode.CS2J0470,
                    "A semantic model is required to classify equality/concat sites."));
                return MarkExplained(visited);
            }

            var classification = Classify(node);
            if (classification.Kind != EqualityConcatKind.StringConcatCompound)
            {
                if (classification.Kind is EqualityConcatKind.Dynamic)
                {
                    _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0471));
                    EmitSite(node, new EqualityConcatDiagnostic(
                        EqualityConcatSeverity.Error,
                        EqualityConcatDiagnosticCode.CS2J0471,
                        "Dynamically bound += site cannot be rewritten."));
                    return MarkExplained(visited);
                }

                return MarkExplained(visited);
            }

            var anchor = TempLifter.FindAnchor(node);
            if (anchor == null)
            {
                EmitUnsupportedCompound(node,
                    "No statement anchor for temp declarations at this += site.");
                return MarkExplained(visited);
            }

            var (recipe, buildTarget) = CompoundTargetRecipe.Analyze(
                visited.Left, _names);
            if (recipe == null || buildTarget == null)
            {
                EmitUnsupportedCompound(node,
                    "This += target shape cannot guarantee single evaluation.");
                return MarkExplained(visited);
            }

            foreach (var (name, initializer) in recipe.Temps)
            {
                _tempOrder++;
                Pending.Add(new TempLifter.PendingTemp(
                    TempLifter.DeclareVar(name, initializer)
                        .WithAdditionalAnnotations(
                            EqualityConcatSyntaxFacts.ExplainedSubtreeAnnotation),
                    anchor,
                    _tempOrder));
                RegisterAnchor(anchor);
                _stats.TemporariesLifted++;
            }

            var replacement = StringConcatRewriter.RewriteCompound(
                buildTarget, visited.Right);
            _stats.ConcatCompoundRewritten++;
            _rewriteCount++;
            return replacement;
        }

        private SyntaxNode RewriteDelegate(
            BinaryExpressionSyntax node,
            BinaryExpressionSyntax visited,
            EqualityConcatClassification classification)
        {
            if (!_options.EnableDelegateRule)
            {
                _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0473));
                EmitSite(node, new EqualityConcatDiagnostic(
                    EqualityConcatSeverity.Warning,
                    EqualityConcatDiagnosticCode.CS2J0473,
                    "Delegate equality materialization is disabled; the site is " +
                    "left unchanged (controlled degradation)."));
                return MarkExplained(visited);
            }

            _stats.DelegateEqualsRewritten++;
            _rewriteCount++;
            return DelegateEqualRewriter.Rewrite(
                visited.Left, visited.Right, classification.IsNotEquals);
        }

        /// <summary>
        /// EC-3: fold only at the topmost node of the pure left-add chain.
        /// Inner left-concat nodes remain binary until this fold; operands are
        /// collected from the original spine and mapped to their visited form.
        /// </summary>
        private SyntaxNode RewriteConcat(
            BinaryExpressionSyntax node,
            BinaryExpressionSyntax visited)
        {
            if (node.Parent is BinaryExpressionSyntax parentBinary
                && Classify(parentBinary).Kind == EqualityConcatKind.StringConcat)
            {
                // Right-associative and mid-chain nodes stay put; the topmost
                // fold or the enclosing chain accounts for them.
                return MarkExplained(visited);
            }

            var originalOperands = new List<ExpressionSyntax>();
            var current = node;
            while (true)
            {
                originalOperands.Add(current.Right);
                var left = current.Left;
                if (left is BinaryExpressionSyntax leftBinary
                    && leftBinary.OperatorToken.IsKind(SyntaxKind.PlusToken)
                    && Classify(leftBinary).Kind == EqualityConcatKind.StringConcat)
                {
                    current = leftBinary;
                }
                else
                {
                    originalOperands.Add(left);
                    break;
                }
            }

            originalOperands.Reverse();
            var visitedOperands = originalOperands
                .Select(MapToVisited)
                .ToList();

            var arguments = SyntaxFactory.SeparatedList(
                visitedOperands.Select(SyntaxFactory.Argument));
            var invocation = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.PredefinedType(
                        SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                    SyntaxFactory.IdentifierName("Concat")),
                SyntaxFactory.ArgumentList(arguments));

            _stats.ConcatChainsFolded++;
            _stats.ConcatOperandsTotal += visitedOperands.Count;
            _rewriteCount++;
            return invocation;
        }

        private void EmitUnsupportedCompound(SyntaxNode node, string message)
        {
            _stats.AddSkip(nameof(EqualityConcatDiagnosticCode.CS2J0475));
            EmitSite(node, new EqualityConcatDiagnostic(
                StrictSeverity(EqualityConcatDiagnosticCode.CS2J0475),
                EqualityConcatDiagnosticCode.CS2J0475,
                message));
        }

        private EqualityConcatClassification Classify(SyntaxNode node)
        {
            if (_classificationCache.TryGetValue(node, out var cached))
                return cached;

            var classification = SemanticKindClassifier.Classify(node, _model!);
            _classificationCache[node] = classification;
            return classification;
        }

        private void EmitSite(
            SyntaxNode node,
            EqualityConcatDiagnostic diagnostic)
        {
            var tree = node.SyntaxTree;
            var span = node.GetLocation().GetMappedLineSpan();
            var filePath = tree?.FilePath;
            _diagnostics.Add(diagnostic with
            {
                FilePath = filePath,
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
            });
        }

        private EqualityConcatSeverity StrictSeverity(
            EqualityConcatDiagnosticCode code)
            => _options.Strict
                ? EqualityConcatSeverity.Error
                : EqualityConcatSeverity.Warning;

        private void RegisterAnchor(SyntaxNode anchor)
        {
            if (!_anchorAnnotations.ContainsKey(anchor))
                _anchorAnnotations[anchor] = new SyntaxAnnotation("ec-anchor");
        }

        private void RememberVisited(SyntaxNode original, SyntaxNode visited)
        {
            if (original != visited)
                _visitedMap[original] = visited;
        }

        private ExpressionSyntax MapToVisited(ExpressionSyntax expression)
            => _visitedMap.TryGetValue(expression, out var visited)
                ? (ExpressionSyntax)visited
                : expression;

        /// <summary>
        /// Unwraps redundant top-level <c>(object)</c> casts and parentheses
        /// around reference-type operands. Value-type casts are boxing and are
        /// preserved; generic/unconstrained inner expressions are preserved.
        /// </summary>
        private static ExpressionSyntax UnwrapRedundantObjectCast(
            ExpressionSyntax expression,
            SemanticModel model)
        {
            while (true)
            {
                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        expression = parenthesized.Expression;
                        continue;

                    case CastExpressionSyntax cast
                        when model.GetTypeInfo(cast.Type).Type
                                 ?.SpecialType == SpecialType.System_Object
                             && model.GetTypeInfo(cast.Expression).Type
                                 is { IsReferenceType: true }:
                        expression = cast.Expression;
                        continue;

                    default:
                        return expression;
                }
            }
        }

        private static T MarkExplained<T>(T node) where T : ExpressionSyntax
            => node.WithAdditionalAnnotations(
                EqualityConcatSyntaxFacts.ExplainedAnnotation);

        private static bool IsCandidateToken(SyntaxKind kind)
            => kind is SyntaxKind.EqualsEqualsToken
                or SyntaxKind.ExclamationEqualsToken
                or SyntaxKind.PlusToken;

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            var result = base.Visit(node);
            if (node != null
                && result != null
                && _anchorAnnotations.TryGetValue(node, out var annotation))
            {
                result = result.WithAdditionalAnnotations(annotation);
            }

            return result;
        }
    }
}

/// <summary>
/// Shared annotations for the residual gate. <see cref="ExplainedAnnotation"/>
/// marks a candidate node that is intentionally untouched;
/// <see cref="ExplainedSubtreeAnnotation"/> marks a subtree (hoisted temp
/// initializer) whose nested candidate nodes are all explained.
/// </summary>
internal static class EqualityConcatSyntaxFacts
{
    public static readonly SyntaxAnnotation ExplainedAnnotation =
        new("ec-explained");

    public static readonly SyntaxAnnotation ExplainedSubtreeAnnotation =
        new("ec-explained-subtree");
}
