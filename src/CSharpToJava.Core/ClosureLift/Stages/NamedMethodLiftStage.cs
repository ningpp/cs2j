using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45 M1 stage (design §4.1/§9): lifts zero-capture (incl. C# 9 `static`)
/// statement lambdas to host-level <c>private static</c> named methods
/// (D10) and freezes the site plans for <see cref="DelegateSiteRewriteStage"/>.
/// No display class, no instance, no holder interaction (C32 stage is
/// untouched and runs first).
///
/// Analysis/rewrite split (design §4.1) across two trees when HolderLiftStage
/// already edited the file: the semantic classification runs on the MODEL
/// tree (always model-valid); the rewrite consumes the RECEIVED tree. The
/// two trees pair deterministically because C32's edit vocabulary preserves
/// both sequences — lambda nodes are never added, removed or reordered
/// (initializers are relocated in place), and type/namespace member lists
/// only gain holder classes at their end. Method bodies are therefore built
/// from the RECEIVED lambda (C32 field-access rewrites baked in), and plans
/// anchor on received-tree nodes, which stay instance-identical through the
/// method-append edit for the site stage.
/// Any blocking row (pre-existing C32 rows or own CS2J4501/4509) keeps the
/// file verbatim (C32 plan-level rollback discipline).
/// </summary>
internal sealed class NamedMethodLiftStage : IClosureLiftStage
{
    public string Id => "named-method-lift";

    public ClosureStageResult Apply(ClosureStageInput input)
    {
        var (root, model, options, naming, statistics, diagnostics) = input;

        if (input.PlanBag is not { } bag
            || options.AuditOnly
            // A frozen file stays verbatim end to end (design §4.1 rollback).
            || diagnostics.Any(d => d.Code != ClosureLiftCodes.AuditRow))
        {
            return new ClosureStageResult(root, false);
        }

        var analysisRoot = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var sites = LambdaSiteIndex.CollectSites(analysisRoot, model);
        if (sites.Count == 0)
            return new ClosureStageResult(root, false);

        // Model-tree → received-tree pairing (see type summary). A count
        // mismatch means an unexpected edit vocabulary: skip M1 for the file
        // instead of guessing (fail-clean, lambda nodes stay).
        if (!TryBuildPairings(analysisRoot, root, sites, out var receivedLambdaBySpanStart, out var receivedTypeByModelType))
            return new ClosureStageResult(root, false);

        // Plan (freezes names + signatures). k is the per-host-member SpanStart
        // sequence (D9); overloads share the member-name anchor and the sequence.
        // Two phases: every plan exists before any method body is built, so a
        // nested planned lambda inside an outer body can be substituted with
        // its own explicit delegate construction at emit time (D8).
        var memberOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        var pending = new List<(StaticLambdaPlan Plan, ResolvedLambdaSignature Signature, LambdaExpressionSyntax ReceivedLambda, TypeDeclarationSyntax ReceivedHost)>();

        foreach (var site in sites)
        {
            var signature = LambdaSignatureResolver.Resolve(
                model, site.Lambda, options, site.RoutineName, diagnostics);
            if (signature == null)
                return new ClosureStageResult(root, false); // CS2J4501/4509: file stays verbatim.

            var receivedLambda = receivedLambdaBySpanStart[site.SpanStart];
            if (!HasUsableParameterNames(receivedLambda, signature))
                continue; // unnamed/discard parameter synthesis is M3 (D12①); lambda stays.

            var hostKey = HolderPlanner.HostTypeKey(model, site.HostType);
            var memberKey = hostKey + "\0" + site.RoutineName;
            var index = memberOrdinal.TryGetValue(memberKey, out var current) ? current : 0;
            memberOrdinal[memberKey] = index + 1;

            var methodName = naming.ReserveMethodName(hostKey, site.RoutineName, index);
            var plan = new StaticLambdaPlan(
                receivedLambda.SpanStart, receivedLambda.RawKind,
                methodName, signature.DelegateTypeDisplay);

            var receivedHost = receivedTypeByModelType[site.HostType];
            pending.Add((plan, signature, receivedLambda, receivedHost));
        }

        if (pending.Count == 0)
            return new ClosureStageResult(root, false);

        // Phase 2: build the methods. The body comes from the received lambda —
        // C32 site rewrites inside it (field accesses for captured symbols)
        // travel with the move — with nested planned sites substituted.
        var planByPosition = pending.ToDictionary(
            t => (t.Plan.SiteSpanStart, t.Plan.SiteRawKind),
            t => t.Plan);
        var plans = new List<StaticLambdaPlan>();
        var methodsByReceivedHost = new Dictionary<TypeDeclarationSyntax, List<MethodDeclarationSyntax>>();

        foreach (var (plan, signature, receivedLambda, receivedHost) in pending)
        {
            var method = StaticLambdaHandler.BuildMethod(plan.MethodName, signature, receivedLambda, planByPosition);
            var built = plan with { Method = method };
            plans.Add(built);
            planByPosition[(built.SiteSpanStart, built.SiteRawKind)] = built;

            if (!methodsByReceivedHost.TryGetValue(receivedHost, out var list))
            {
                list = new List<MethodDeclarationSyntax>();
                methodsByReceivedHost[receivedHost] = list;
            }

            list.Add(method);
        }

        bag.StaticLambdas.AddRange(plans);
        statistics.StaticLambdaMethods += plans.Count;
        statistics.LambdaMethodsLifted += plans.Count;

        // Rewrite: append the methods into the received root's host types. The
        // append walks the type/namespace member spine ONLY and rebuilds just
        // the member lists it touches (WithMembers) — untouched positions keep
        // their values, so DelegateSiteRewriteStage's SpanStart-anchored sites
        // resolve against the plans exactly as frozen.
        var rewritten = root.WithMembers(AppendMethods(root.Members, methodsByReceivedHost));

        return new ClosureStageResult(rewritten, true);
    }

    /// <summary>
    /// Appends planned methods along the type/namespace member spine. Only the
    /// member lists that gain methods are rebuilt (WithMembers) — every other
    /// node instance in the tree is preserved untouched.
    /// </summary>
    private static SyntaxList<MemberDeclarationSyntax> AppendMethods(
        SyntaxList<MemberDeclarationSyntax> members,
        Dictionary<TypeDeclarationSyntax, List<MethodDeclarationSyntax>> methodsByHost)
    {
        List<MemberDeclarationSyntax>? buffer = null;

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            MemberDeclarationSyntax replacement = member;

            if (member is TypeDeclarationSyntax type)
            {
                var newType = type;
                if (methodsByHost.TryGetValue(type, out var methods))
                    newType = type.WithMembers(type.Members.AddRange(methods));

                var nestedMembers = AppendMethods(newType.Members, methodsByHost);
                if (nestedMembers != newType.Members)
                    newType = newType.WithMembers(nestedMembers);

                if (!ReferenceEquals(newType, type))
                    replacement = newType;
            }
            else if (member is BaseNamespaceDeclarationSyntax namespaceDeclaration)
            {
                var namespaceMembers = AppendMethods(namespaceDeclaration.Members, methodsByHost);
                if (namespaceMembers != namespaceDeclaration.Members)
                    replacement = namespaceDeclaration.WithMembers(namespaceMembers);
            }

            if (!ReferenceEquals(replacement, member) && buffer == null)
            {
                buffer = new List<MemberDeclarationSyntax>();
                for (var j = 0; j < i; j++)
                    buffer.Add(members[j]);
            }

            buffer?.Add(replacement);
        }

        return buffer == null ? members : SyntaxFactory.List(buffer);
    }

    /// <summary>
    /// Pairs the analysis (model) tree with the received tree, keyed by the
    /// lambda's SpanStart (unique per source occurrence; C32 edits preserve
    /// lambda count and order). Type declarations pair by their member-list
    /// paths (C32 appends holder classes at member-list ends only).
    /// </summary>
    private static bool TryBuildPairings(
        CompilationUnitSyntax analysisRoot,
        CompilationUnitSyntax receivedRoot,
        IReadOnlyList<StaticLambdaSite> sites,
        out Dictionary<int, LambdaExpressionSyntax> receivedLambdaBySpanStart,
        out Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax> receivedTypeByModelType)
    {
        receivedLambdaBySpanStart = new Dictionary<int, LambdaExpressionSyntax>();
        receivedTypeByModelType = new Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax>();
        var analysisLambdas = analysisRoot.DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
        var receivedLambdas = receivedRoot.DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
        if (analysisLambdas.Count != receivedLambdas.Count)
            return false;

        for (var i = 0; i < analysisLambdas.Count; i++)
            receivedLambdaBySpanStart[analysisLambdas[i].SpanStart] = receivedLambdas[i];

        var modelTypeByAnalysis = new Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax>();
        if (!PairTypeMembers(analysisRoot.Members, receivedRoot.Members, modelTypeByAnalysis))
            return false;

        foreach (var site in sites)
        {
            if (!receivedLambdaBySpanStart.TryGetValue(site.SpanStart, out var receivedLambda)
                || !modelTypeByAnalysis.TryGetValue(site.HostType, out var receivedType))
            {
                return false;
            }

            receivedLambdaBySpanStart[site.SpanStart] = receivedLambda;
            receivedTypeByModelType[site.HostType] = receivedType;
        }

        return true;
    }

    /// <summary>
    /// Zips type declarations (and namespace declarations as transparent
    /// containers) by relative member order. C32's holder classes are appended
    /// at member-list ends and carry the cs2j_closure_lift annotation, so
    /// filtering them out restores exact count parity with the analysis tree.
    /// </summary>
    private static bool PairTypeMembers(
        SyntaxList<MemberDeclarationSyntax> analysisMembers,
        SyntaxList<MemberDeclarationSyntax> receivedMembers,
        Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax> map)
    {
        var analysisTypes = analysisMembers.OfType<TypeDeclarationSyntax>().ToList();
        var receivedTypes = receivedMembers.OfType<TypeDeclarationSyntax>()
            .Where(t => !ClosureLiftAnnotations.IsAnnotated(t))
            .ToList();
        if (analysisTypes.Count != receivedTypes.Count)
            return false;

        for (var i = 0; i < analysisTypes.Count; i++)
        {
            map[analysisTypes[i]] = receivedTypes[i];
            if (!PairTypeMembers(analysisTypes[i].Members, receivedTypes[i].Members, map))
                return false;
        }

        var analysisNamespaces = analysisMembers.OfType<BaseNamespaceDeclarationSyntax>().ToList();
        var receivedNamespaces = receivedMembers.OfType<BaseNamespaceDeclarationSyntax>().ToList();
        if (analysisNamespaces.Count != receivedNamespaces.Count)
            return false;

        for (var i = 0; i < analysisNamespaces.Count; i++)
        {
            if (!PairTypeMembers(analysisNamespaces[i].Members, receivedNamespaces[i].Members, map))
                return false;
        }

        return true;
    }

    /// <summary>
    /// M1 parameter-name gate (D12 full synthesis lands in M3): every lambda
    /// parameter must supply a usable method-parameter name — non-empty and
    /// unique across the parameter list (a single `_` discard is a legal name;
    /// duplicated `_` or unnamed C# 10 parameters are deferred to M3).
    /// Parameter attributes migrate with the parameter in M3 (D12④) — a
    /// lambda carrying them is left in place here.
    /// </summary>
    private static bool HasUsableParameterNames(
        LambdaExpressionSyntax lambda,
        ResolvedLambdaSignature signature)
    {
        IReadOnlyList<ParameterSyntax> lambdaParameters = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters,
            SimpleLambdaExpressionSyntax s => new SyntaxList<ParameterSyntax>(s.Parameter),
            _ => default,
        };

        if (lambdaParameters.Count != signature.Parameters.Count)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in lambdaParameters)
        {
            if (parameter.Identifier.ValueText.Length == 0)
                return false;
            if (parameter.AttributeLists.Count > 0)
                return false;
            if (!seen.Add(parameter.Identifier.ValueText))
                return false;
        }

        return true;
    }
}
