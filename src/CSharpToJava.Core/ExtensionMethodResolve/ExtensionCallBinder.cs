using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Single-file analysis (EM-BIND): enumerates invocation nodes, consumes
/// <see cref="SemanticModel.GetSymbolInfo"/> and classifies the ones bound as
/// <c>MethodKind.ReducedExtension</c>; no candidate resolution is rebuilt here
/// (SemanticModel is the single source of truth, design §2 EM-BIND).
///
/// M1 coverage:
/// - ordinary member-access reduced calls (<c>r.M(a)</c>) are selected and
///   rewritten; LINQ hosts (Enumerable/Queryable) are counted and left alone;
/// - <c>CandidateReason.Ambiguous</c> → CS2J1201 (candidate hosts + namespaces);
/// - <c>None</c>/<c>OverloadResolutionFailure</c> receiver-style calls → CS2J1202
///   (Warning when an extension method is among the candidates, Info trace
///   otherwise; strict promotes the Warning to Error);
/// - C# 14 <c>extension</c> blocks are a SYNTAX gate (EM-EXT14, CS2J1206 Error,
///   never graded): under Roslyn 5 / LanguageVersion.Latest the block members
///   are MethodKind.Ordinary inside a TypeKind.Extension container and call
///   sites bind directly to them (ReducedFrom == null), so the
///   ReducedExtension key below would silently miss every block site — the
///   syntax scan is the mandatory correctness door (M0 probe conclusion ②).
///
/// Deferred to later milestones (traced, never silent):
/// - null-conditional reduced calls (<c>r?.M()</c>, MemberBinding shape; M2);
/// - <c>this ref</c>/<c>this in</c> receivers (EM-REF; M2).
/// </summary>
internal sealed class ExtensionCallBinder
{
    public const string NullConditionalDeferredReason = "null-conditional-deferred-m2";
    public const string RefReceiverDeferredReason = "ref-receiver-deferred-m2";
    public const string ExternalRewriteDisabledReason = "external-metadata-rewrite-disabled";

    private readonly List<ExtensionCallBinding> _bindings = new();
    private readonly List<ExtensionDiagnostic> _diagnostics = new();
    private readonly ExtensionMethodResolveStatistics _statistics = new();

    public ExtensionCallBinder(SemanticModel model, ExtensionMethodResolveOptions options)
    {
        Model = model;
        Options = options;
    }

    public SemanticModel Model { get; }

    public ExtensionMethodResolveOptions Options { get; }

    public IReadOnlyList<ExtensionCallBinding> Bindings => _bindings;

    public IReadOnlyList<ExtensionDiagnostic> Diagnostics => _diagnostics;

    public ExtensionMethodResolveStatistics Statistics => _statistics;

    /// <summary>Analyze one syntax tree (bindings/diagnostics sorted by span).</summary>
    public void Analyze()
    {
        var root = Model.SyntaxTree.GetCompilationUnitRoot();

        // EM-EXT14: syntax-driven. LanguageVersion.Latest ships C#14 extension
        // blocks with zero compiler diagnostics on the block itself, so binding
        // diagnostics cannot detect them. Every block is an unconditional Error
        // (classic this-parameter form is the only v1-supported shape; C49 owns
        // block lowering).
        foreach (var block in root.DescendantNodes()
                     .Where(n => n.IsKind(SyntaxKind.ExtensionBlockDeclaration)))
        {
            AddDiagnostic(
                ExtensionSeverity.Error,
                ExtensionRule.Ext14,
                ExtensionMethodCodes.CSharp14ExtensionBlock,
                "C# 14 'extension' block (extension property/operator/static extension member) is not supported "
                + "by the classic static-class + 'this' parameter form; it will be handled by C49. "
                + "The file is left unchanged.",
                block.GetLocation());
            AddSkipReason(ExtensionMethodCodes.CSharp14ExtensionBlock);
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var info = Model.GetSymbolInfo(invocation);
            if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } reduced
                && reduced.ReducedFrom is { } unreduced)
            {
                _statistics.CallsitesBoundReduced++;
                ClassifyBound(invocation, reduced, unreduced);
                continue;
            }

            if (info.Symbol is null && IsReceiverStyleCall(invocation))
            {
                ClassifyUnbound(invocation, info);
            }
        }

        _bindings.Sort((a, b) => a.Invocation.SpanStart.CompareTo(b.Invocation.SpanStart));
        _diagnostics.Sort((a, b) => (a.Line ?? 0).CompareTo(b.Line ?? 0));
    }

    private void ClassifyBound(
        InvocationExpressionSyntax invocation,
        IMethodSymbol reduced,
        IMethodSymbol unreduced)
    {
        // Host always comes from the UNREDUCED symbol (constructed, so nested
        // generic host type arguments are included; M0 probe conclusion ③).
        var host = unreduced.ContainingType;
        var hostDisplayName = host.ToDisplayString();

        if (Options.LinqHostSet.Contains(hostDisplayName))
        {
            // EM-LINQ: Enumerable/Queryable sugar stays reduced for E16/C26.
            _statistics.LinqOwnedSkipped++;
            return;
        }

        // M0 probe conclusion ①: r?.M() is legal and still binds
        // ReducedExtension, but the tree is ConditionalAccess/MemberBinding.
        // M1 does not select the site; M2 owns the temp-lift rewrite.
        if (IsNullConditionalReducedSite(invocation))
        {
            AddSkipReason(NullConditionalDeferredReason);
            AddDiagnostic(
                ExtensionSeverity.Info,
                ExtensionRule.Cond,
                ExtensionMethodCodes.Info,
                $"Null-conditional extension call '{invocation.ToString()}' is deferred to M2 (temporary-variable lifting); site unchanged.",
                invocation.GetLocation());
            return;
        }

        // EM-REF lands in M2: without the ref/in argument modifier a rewrite
        // would not compile, so leave the site untouched but trace it.
        var receiverRefKind = unreduced.Parameters.Length > 0 ? unreduced.Parameters[0].RefKind : RefKind.None;
        if (receiverRefKind != RefKind.None)
        {
            _statistics.RefReceiverSites++;
            AddSkipReason(RefReceiverDeferredReason);
            AddDiagnostic(
                ExtensionSeverity.Info,
                ExtensionRule.Ref,
                ExtensionMethodCodes.Info,
                $"Extension call '{invocation.ToString()}' has a '{receiverRefKind.ToString().ToLowerInvariant()}' receiver parameter; ref/in emission lands in M2.",
                invocation.GetLocation());
            return;
        }

        var classification = host.Locations.Length > 0 && host.Locations[0].IsInSource
            ? ReceiverClassification.InCompilationUser
            : ReceiverClassification.ExternalMetadata;

        if (classification == ReceiverClassification.ExternalMetadata)
        {
            _statistics.ExternalHostSites++;
            if (!Options.RewriteExternalMetadataHosts)
            {
                AddSkipReason(ExternalRewriteDisabledReason);
                AddDiagnostic(
                    ExtensionSeverity.Info,
                    ExtensionRule.External,
                    ExtensionMethodCodes.Info,
                    $"Metadata-host extension call '{invocation.ToString()}' (host {hostDisplayName}) skipped because RewriteExternalMetadataHosts=false.",
                    invocation.GetLocation());
                return;
            }
        }
        else
        {
            // M1 folds InCompilationSupport into the in-compilation bucket;
            // support-library split lands in M3.
            _statistics.InCompilationHostSites++;
        }

        var explicitTypeArguments = invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName }
            && genericName.TypeArgumentList.Arguments.Count > 0;

        _bindings.Add(new ExtensionCallBinding(
            invocation,
            reduced,
            unreduced,
            classification,
            receiverRefKind,
            IsInExpressionTree: false,
            explicitTypeArguments));
    }

    private void ClassifyUnbound(InvocationExpressionSyntax invocation, SymbolInfo info)
    {
        // Dynamic dispatch never binds extension methods (design §2): no
        // diagnostic at all — the call is not extension-sugar.
        if (TryGetReceiverExpression(invocation) is { } receiverExpression
            && Model.GetTypeInfo(receiverExpression).Type?.TypeKind == TypeKind.Dynamic)
        {
            return;
        }

        // Empirical Roslyn shape (M1 probe): a CS0121 ambiguity between two
        // reduced extension candidates (e.g. equally applicable methods in two
        // imported namespaces) is reported as CandidateReason.Ambiguous for
        // member/instance ties but as OverloadResolutionFailure for
        // extension-vs-extension ties, with both reduced methods present in
        // CandidateSymbols. Treat either shape with >=2 distinct extension
        // candidates as ambiguity (CS2J1201).
        var extensionCandidates = info.CandidateSymbols
            .OfType<IMethodSymbol>()
            .Where(m => m.IsExtensionMethod || m.MethodKind == MethodKind.ReducedExtension)
            .ToList();

        if (info.CandidateReason == CandidateReason.Ambiguous
            || (info.CandidateReason == CandidateReason.OverloadResolutionFailure
                && extensionCandidates.Select(DescribeCandidateHost).Distinct(StringComparer.Ordinal).Count() >= 2))
        {
            var candidates = extensionCandidates
                .Select(DescribeCandidateHost)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var severity = Options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;
            AddDiagnostic(
                severity,
                ExtensionRule.Bind,
                ExtensionMethodCodes.Ambiguous,
                $"Ambiguous extension call '{invocation.ToString()}': candidates [{string.Join("; ", candidates)}]; site left unchanged.",
                invocation.GetLocation());
            AddSkipReason(ExtensionMethodCodes.Ambiguous);
            return;
        }

        // None / OverloadResolutionFailure. Warning when an extension method is
        // among the candidates (genuine extension sugar we could not bind —
        // incomplete references etc.); plain unbound ordinary calls get an Info
        // trace (verbose) and strict never promotes them.
        if (extensionCandidates.Count > 0)
        {
            var severity = Options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;
            AddDiagnostic(
                severity,
                ExtensionRule.Bind,
                ExtensionMethodCodes.Unbound,
                $"Extension-like call '{invocation.ToString()}' failed to bind ({info.CandidateReason}); references may be incomplete. Site left unchanged.",
                invocation.GetLocation());
            AddSkipReason(ExtensionMethodCodes.Unbound);
        }
        else if (Options.ReportInfo)
        {
            AddDiagnostic(
                ExtensionSeverity.Info,
                ExtensionRule.Bind,
                ExtensionMethodCodes.Info,
                $"Receiver-style call '{invocation.ToString()}' unbound ({info.CandidateReason}); not selected.",
                invocation.GetLocation());
        }
    }

    private string DescribeCandidateHost(IMethodSymbol candidate)
    {
        var host = (candidate.ReducedFrom ?? candidate).ContainingType;
        var namespaceName = host.ContainingNamespace?.ToDisplayString();
        return string.IsNullOrEmpty(namespaceName)
            ? host.ToDisplayString()
            : $"{host.ToDisplayString()} (imported from namespace {namespaceName})";
    }

    private static bool IsReceiverStyleCall(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax;

    private static ExpressionSyntax? TryGetReceiverExpression(InvocationExpressionSyntax invocation)
        => (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;

    /// <summary>
    /// True when the invocation belongs to a null-conditional chain
    /// (<c>r?.M()</c> / <c>r?.M().N()</c>): its span is inside some
    /// ConditionalAccess' WhenNotNull branch. <c>(r?.M()).N()</c> is NOT inside
    /// the branch and stays a normal M1 site.
    /// </summary>
    private static bool IsNullConditionalReducedSite(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberBindingExpressionSyntax)
        {
            return true;
        }

        return invocation.Ancestors()
            .OfType<ConditionalAccessExpressionSyntax>()
            .Any(conditional => conditional.WhenNotNull.Span.Contains(invocation.Span));
    }

    private void AddSkipReason(string reason)
    {
        _statistics.SitesSkippedByReason.TryGetValue(reason, out var current);
        _statistics.SitesSkippedByReason[reason] = current + 1;
    }

    private void AddDiagnostic(
        ExtensionSeverity severity,
        ExtensionRule rule,
        string code,
        string message,
        Location location)
    {
        var lineSpan = location.GetLineSpan();
        _diagnostics.Add(new ExtensionDiagnostic(
            severity,
            rule,
            code,
            message,
            Model.SyntaxTree.FilePath,
            lineSpan.Span.Start.Line + 1));
    }
}
