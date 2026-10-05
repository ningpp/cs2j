using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// M3 §7.10 AfterFiles residual gates, evaluated per file against TWO
/// semantic models: the stage-2 INPUT model (post stage-1 desugar) and a
/// freshly rebuilt model over the materialized channel output.
/// <para>
/// Gate ② — a call bound to <c>System.Linq.Queryable</c> must never be
/// rewritten: the ordered sequence of Queryable-bound LINQ calls is compared
/// between input and output.
/// </para>
/// <para>
/// Gate ③ — every invocation lexically inside an
/// <c>System.Linq.Expressions.Expression&lt;&gt;</c>-quoted lambda (any host,
/// Enumerable chains and user/BCL calls included) must survive node for node
/// with the same bound host: the ordered signature sequence inside quotes is
/// compared between input and output.
/// </para>
/// <para>
/// Gates ② and ③ are evaluated as ONE ordered signature stream (a preserved
/// node is frozen verbatim, so its position and signature are invariant).
/// </para>
/// <para>
/// Gate ④ — an extracted <c>*_ProceduralLinq*</c> helper may only be added to
/// a type whose input tree contained an Expandable Enumerable chain outside
/// quoted regions.
/// </para>
/// <para>
/// Gates ① and ⑤ (no in-support-matrix Enumerable chain left behind; a second
/// channel pass moves nothing) are driven by the adapter, which reruns
/// <see cref="LinqChannelRewriter.ProcessFile"/> over the output tree; this
/// scanner intentionally stays syntax/symbol-only.
/// </para>
/// </summary>
public static class LinqChannelResidualScanner
{
    /// <summary>
    /// Identifier fragment of every E16-generated procedural LINQ helper
    /// (method names look like <c>&lt;Method&gt;_ProceduralLinq1</c>).
    /// </summary>
    public const string ProceduralHelperFragment = "_ProceduralLinq";

    /// <summary>
    /// Scans one file pair. Returns zero or more LC0040
    /// (<see cref="LinqChannelDiagnosticCodes.ResidualMismatch"/>) diagnostics;
    /// severity is Warning normally and Error under <paramref name="strict"/>.
    /// </summary>
    public static List<LinqChannelDiagnostic> ScanFile(
        SemanticModel inputModel,
        SyntaxNode inputRoot,
        SemanticModel outputModel,
        SyntaxNode outputRoot,
        string? filePath,
        bool strict)
    {
        var diagnostics = new List<LinqChannelDiagnostic>();
        var severity = strict
            ? LinqChannelDiagnosticSeverity.Error
            : LinqChannelDiagnosticSeverity.Warning;

        var inputSignatures = PreservedSignatureStream(inputModel, inputRoot);
        var outputSignatures = PreservedSignatureStream(outputModel, outputRoot);
        if (!inputSignatures.SequenceEqual(outputSignatures))
        {
            diagnostics.Add(new LinqChannelDiagnostic(
                severity,
                LinqChannelDiagnosticCodes.ResidualMismatch,
                "Residual route mismatch (LC0040): the preserved-call stream "
                + $"(Queryable-bound calls and calls inside Expression<>-quoted lambdas) changed: "
                + $"{inputSignatures.Count} site(s) before the channel, {outputSignatures.Count} after "
                + "— a Queryable/quoted node must never be rewritten or rebound (design §7.10②③).",
                filePath,
                FirstDivergenceLine(inputRoot, outputRoot)));
        }

        var expandedInputTypes = ExpandedTypeKeys(inputModel, inputRoot);
        foreach (var type in outputRoot.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (!TypeDeclaresProceduralHelper(type))
            {
                continue;
            }

            var key = TypeKey(type);
            if (expandedInputTypes.Contains(key))
            {
                continue;
            }

            diagnostics.Add(new LinqChannelDiagnostic(
                severity,
                LinqChannelDiagnosticCodes.ResidualMismatch,
                $"Residual helper placement mismatch (LC0040): type '{key}' carries a "
                + ProceduralHelperFragment + " helper in the output but contained no Expandable "
                + "Enumerable chain in the input — extracted helpers may only land in the type "
                + "that owned the expanded chain (design §7.10④).",
                filePath,
                type.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }

        return diagnostics;
    }

    // ---- gates ②③: preserved signature stream --------------------------------

    private static List<string> PreservedSignatureStream(SemanticModel model, SyntaxNode root)
    {
        var classifier = new LinqRouteClassifier(model);
        var signatures = new List<string>();
        foreach (var invocation in root.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>()
                     .Where(i => !LinqRouteClassifier.IsNameofPseudoCall(i)))
        {
            var insideQuote = ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, invocation);
            var decision = classifier.ClassifyInvocation(invocation);
            if (decision.Kind != LinqRouteKind.Queryable && !insideQuote)
            {
                continue;
            }

            var name = (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
                ?? invocation.Expression.ToString();
            var host = decision.BoundMethod is not null
                ? (decision.BoundMethod.ReducedFrom?.ContainingType
                    ?? decision.BoundMethod.ContainingType)?.OriginalDefinition.ToDisplayString()
                : decision.HostDisplay;
            signatures.Add(string.Concat(
                insideQuote ? "Q:" : "R:",
                host ?? "<unbound>",
                ":",
                name,
                ":",
                invocation.ArgumentList.Arguments.Count.ToString()));
        }

        return signatures;
    }

    private static int? FirstDivergenceLine(SyntaxNode inputRoot, SyntaxNode outputRoot)
    {
        // Diagnostic anchoring only: prefer the first Queryable/quoted call in
        // the input; the message already carries the before/after counts.
        var anchor = inputRoot.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        return anchor?.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    // ---- gate ④: helper placement --------------------------------------------

    private static HashSet<string> ExpandedTypeKeys(SemanticModel model, SyntaxNode root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var plan = LinqChannelPlan.Build(model, root, filePath: null);
        foreach (var chain in plan.Chains)
        {
            if (chain.Decision.Route != LinqChainRoute.Expandable || chain.InsideQuotedLambda)
            {
                continue;
            }

            var enclosingType = chain.Nodes[0].Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (enclosingType is not null)
            {
                keys.Add(TypeKey(enclosingType));
            }
        }

        return keys;
    }

    private static bool TypeDeclaresProceduralHelper(TypeDeclarationSyntax type)
    {
        // Helper LANDING: a method or local function whose name carries the
        // generated fragment and whose NEAREST enclosing type is THIS type (so
        // a helper inside a nested class is attributed to the nested class,
        // never the outer type). E16 always lifts into the chain-owning type,
        // so declaration scanning is sufficient and precise.
        foreach (var method in type.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Identifier.Text.Contains(ProceduralHelperFragment, StringComparison.Ordinal))
            {
                continue;
            }

            if (method.Parent!.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault() == type)
            {
                return true;
            }
        }

        foreach (var function in type.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (!function.Identifier.Text.Contains(ProceduralHelperFragment, StringComparison.Ordinal))
            {
                continue;
            }

            if (function.Parent!.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault() == type)
            {
                return true;
            }
        }

        return false;
    }

    private static string TypeKey(TypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        SyntaxNode? current = type;
        while (current is TypeDeclarationSyntax nested)
        {
            parts.Add(nested.Identifier.Text);
            current = nested.Parent;
        }

        parts.Reverse();
        var @namespace = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var prefix = @namespace?.Name.ToString() ?? string.Empty;
        return prefix.Length == 0
            ? string.Join(".", parts)
            : prefix + "." + string.Join(".", parts);
    }
}
