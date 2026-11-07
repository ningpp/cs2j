using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>One construct found in the FINAL preprocessed tree (R13).</summary>
/// <param name="Id">Stable construct family id (see <see cref="SemanticClosureScanner"/> constants).</param>
/// <param name="Capability">The <c>"JCS.&lt;ID&gt;"</c> capability the construct belongs to.</param>
/// <param name="File">Tree-relative path with <c>/</c> separators.</param>
/// <param name="Line">1-based start line.</param>
/// <param name="Column">1-based start column.</param>
/// <param name="Owner">Enabled+successful owner pass id, or null when the construct is unowned.</param>
public sealed record SemanticClosureConstruct(
    string Id,
    string Capability,
    string File,
    int Line,
    int Column,
    string? Owner);

/// <summary>
/// One unowned construct (or invalid finalizer declaration) registered as
/// <c>NORM-SEMANTIC-CLOSURE</c>. <see cref="CandidateOwner"/> is the family's
/// intended owner even when that pass is disabled/failed (null when no owner
/// pass exists yet).
/// </summary>
public sealed record SemanticClosureResidual(
    string Code,
    string Construct,
    string Capability,
    string File,
    int Line,
    int Column,
    string? CandidateOwner,
    string Reason);

/// <summary>Execution record for one <see cref="PassPhase.Finalize"/> contract.</summary>
public sealed record SemanticFinalizerExecution(
    string PassId,
    string Capability,
    bool Executed,
    string? Reason);

/// <summary>Read-only context handed to a bounded finalizer (no tree rewriting in this first cut).</summary>
public sealed record SemanticClosureContext(
    string TreeRoot,
    IReadOnlyList<SemanticClosureConstruct> Constructs,
    IReadOnlyList<SemanticClosureResidual> UnownedConstructs);

/// <summary>
/// Optional bounded finalizer implementation. The first R13 cut registers no
/// implementation; the interface exists so VS-1/VS-2 can attach concrete
/// <c>condition-effect</c>/<c>value-copy</c> finalizers without changing the
/// scanner or the chain contract.
/// </summary>
public interface ISemanticFinalizer
{
    string PassId { get; }

    string Capability { get; }

    void Execute(SemanticClosureContext context);
}

/// <summary>Deterministic result of one final-tree semantic closure scan.</summary>
public sealed record SemanticClosureScanResult(
    IReadOnlyList<SemanticClosureConstruct> Constructs,
    IReadOnlyList<SemanticClosureResidual> UnownedConstructs,
    IReadOnlyList<SemanticFinalizerExecution> Finalizers,
    bool IsClosed);

/// <summary>
/// R13 / VS-0 step 4 first cut: re-enumerate the final tree after all
/// structure/naming passes, map each construct family to its candidate owner
/// pass, and register everything not taken over by an enabled+successful owner
/// as <c>NORM-SEMANTIC-CLOSURE</c>. <see cref="PassPhase.Finalize"/> contracts
/// are validated and executed at most once each; without a registered
/// <see cref="ISemanticFinalizer"/> the execution is recorded as a bounded
/// no-op. The scanner never rewrites the tree, never grants a capability and
/// never changes the pass chain.
/// </summary>
public static class SemanticClosureScanner
{
    public const string GenericTypeofTypeParameter = "generic-typeof-type-parameter";
    public const string GenericDefaultTypeParameter = "generic-default-type-parameter";
    public const string GenericNewTypeParameter = "generic-new-type-parameter";
    public const string CollectionExpression = "collection-expression";
    public const string ExplicitInvoke = "explicit-invoke";
    public const string ConditionEffect = "condition-effect";

    /// <summary>Construct family id used by invalid/finalizer-declaration residuals.</summary>
    public const string FinalizerDeclaration = "semantic-finalizer";

    private const string GenericOwner = "generic";
    private const string GenericDeepenOwner = "generic-deepen";
    private const string ExpressionDesugarOwner = "expression-desugar";

    private static readonly string GenericCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Generic);
    private static readonly string ConversionCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Conversion);
    private static readonly string CallCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Call);
    private static readonly string EvalCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Eval);

    public static SemanticClosureScanResult Scan(
        string treeRoot,
        IReadOnlyList<JcsCapabilityScanPass> enabledPasses,
        IReadOnlyList<ISemanticFinalizer>? finalizers = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(treeRoot);
        ArgumentNullException.ThrowIfNull(enabledPasses);

        var root = Path.GetFullPath(treeRoot);
        var passById = BuildPassMap(enabledPasses);

        var constructs = new List<SemanticClosureConstruct>();
        var constructResiduals = new List<SemanticClosureResidual>();

        foreach (var detected in EnumerateConstructs(root))
        {
            var owner = ResolveOwner(detected.CandidateOwner, passById);
            constructs.Add(new SemanticClosureConstruct(
                detected.Id,
                detected.Capability,
                detected.File,
                detected.Line,
                detected.Column,
                owner));

            if (owner is null)
            {
                constructResiduals.Add(new SemanticClosureResidual(
                    PassConstructIds.SemanticClosure,
                    detected.Id,
                    detected.Capability,
                    detected.File,
                    detected.Line,
                    detected.Column,
                    detected.CandidateOwner,
                    UnownedReason(detected.CandidateOwner, passById)));
            }
        }

        constructs = SortConstructs(constructs);
        constructResiduals = SortResiduals(constructResiduals);

        var context = new SemanticClosureContext(root, constructs, constructResiduals);

        var executions = new List<SemanticFinalizerExecution>();
        var finalizerResiduals = new List<SemanticClosureResidual>();
        var finalizerValid = true;

        foreach (var pass in enabledPasses)
        {
            var contract = pass.Contract;
            if (contract is null || contract.Phase != PassPhase.Finalize)
            {
                continue;
            }

            var target = contract.FinalizerFor;
            if (string.IsNullOrWhiteSpace(target) || !JcsCapabilityIds.TryParse(target, out _))
            {
                finalizerValid = false;
                var text = target ?? string.Empty;
                executions.Add(new SemanticFinalizerExecution(
                    pass.Id,
                    text,
                    Executed: false,
                    Reason: "FinalizerFor is not a valid JCS.<ID> capability"));
                finalizerResiduals.Add(FinalizerResidual(
                    pass.Id,
                    text,
                    $"{pass.Id} declares an invalid FinalizerFor '{text}'"));
                continue;
            }

            var declaringOwner = enabledPasses.Any(candidate =>
                candidate.Contract is not null
                && candidate.Contract.EffectiveCapabilityIds.Contains(
                    target, StringComparer.OrdinalIgnoreCase));
            if (!declaringOwner)
            {
                finalizerValid = false;
                executions.Add(new SemanticFinalizerExecution(
                    pass.Id,
                    target,
                    Executed: false,
                    Reason: "no enabled pass declares the finalized capability"));
                finalizerResiduals.Add(FinalizerResidual(
                    pass.Id,
                    target,
                    $"{pass.Id} finalizes '{target}' but no enabled pass declares that capability"));
                continue;
            }

            var implementation = finalizers?.FirstOrDefault(finalizer =>
                string.Equals(finalizer.PassId, pass.Id, StringComparison.Ordinal));
            if (implementation is null)
            {
                executions.Add(new SemanticFinalizerExecution(
                    pass.Id,
                    target,
                    Executed: false,
                    Reason: "no ISemanticFinalizer implementation registered (bounded no-op)"));
                continue;
            }

            implementation.Execute(context);
            executions.Add(new SemanticFinalizerExecution(pass.Id, target, Executed: true, Reason: null));
        }

        var unowned = SortResiduals(constructResiduals.Concat(finalizerResiduals).ToList());

        return new SemanticClosureScanResult(
            constructs,
            unowned,
            executions,
            IsClosed: unowned.Count == 0 && finalizerValid);
    }

    private static Dictionary<string, JcsCapabilityScanPass> BuildPassMap(
        IReadOnlyList<JcsCapabilityScanPass> passes)
    {
        var map = new Dictionary<string, JcsCapabilityScanPass>(StringComparer.Ordinal);
        foreach (var pass in passes)
        {
            map[pass.Id] = pass;
        }

        return map;
    }

    private static string? ResolveOwner(
        string? candidateOwner,
        IReadOnlyDictionary<string, JcsCapabilityScanPass> passById)
    {
        if (candidateOwner is null)
        {
            return null;
        }

        return passById.TryGetValue(candidateOwner, out var pass) && pass.Succeeded
            ? candidateOwner
            : null;
    }

    private static string UnownedReason(
        string? candidateOwner,
        IReadOnlyDictionary<string, JcsCapabilityScanPass> passById)
    {
        if (candidateOwner is null)
        {
            return "no candidate owner pass is implemented for this construct family";
        }

        if (!passById.ContainsKey(candidateOwner))
        {
            return $"candidate owner '{candidateOwner}' is not enabled in the chain";
        }

        return $"candidate owner '{candidateOwner}' did not succeed";
    }

    private static SemanticClosureResidual FinalizerResidual(
        string passId,
        string capability,
        string reason)
        => new(
            PassConstructIds.SemanticClosure,
            FinalizerDeclaration,
            capability,
            string.Empty,
            0,
            0,
            passId,
            reason);

    private static List<SemanticClosureConstruct> SortConstructs(
        List<SemanticClosureConstruct> constructs)
        => constructs
            .OrderBy(construct => construct.File, StringComparer.Ordinal)
            .ThenBy(construct => construct.Line)
            .ThenBy(construct => construct.Column)
            .ThenBy(construct => construct.Id, StringComparer.Ordinal)
            .ToList();

    private static List<SemanticClosureResidual> SortResiduals(
        List<SemanticClosureResidual> residuals)
        => residuals
            .OrderBy(residual => residual.File, StringComparer.Ordinal)
            .ThenBy(residual => residual.Line)
            .ThenBy(residual => residual.Column)
            .ThenBy(residual => residual.Code, StringComparer.Ordinal)
            .ThenBy(residual => residual.Construct, StringComparer.Ordinal)
            .ThenBy(residual => residual.CandidateOwner ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(residual => residual.Reason, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<DetectedConstruct> EnumerateConstructs(string treeRoot)
    {
        var detected = new List<DetectedConstruct>();

        foreach (var file in ManifestInputHash.EnumerateSourceFiles(treeRoot))
        {
            var relative = ManifestInputHash.ToRelativePath(treeRoot, file);
            var text = File.ReadAllText(file);
            var syntaxRoot = CSharpSyntaxTree.ParseText(text).GetRoot();
            var typeParameters = CollectTypeParameters(syntaxRoot);

            foreach (var node in syntaxRoot.DescendantNodes())
            {
                switch (node)
                {
                    case TypeOfExpressionSyntax typeOf
                        when IsTypeParameter(typeOf.Type, typeParameters):
                        Add(detected, relative, GenericTypeofTypeParameter,
                            GenericCapability, GenericDeepenOwner, typeOf);
                        break;

                    case DefaultExpressionSyntax defaultExpression
                        when IsTypeParameter(defaultExpression.Type, typeParameters):
                        Add(detected, relative, GenericDefaultTypeParameter,
                            GenericCapability, GenericOwner, defaultExpression);
                        break;

                    case ObjectCreationExpressionSyntax creation
                        when IsTypeParameter(creation.Type, typeParameters):
                        Add(detected, relative, GenericNewTypeParameter,
                            GenericCapability, GenericOwner, creation);
                        break;

                    case CollectionExpressionSyntax collection:
                        Add(detected, relative, CollectionExpression,
                            ConversionCapability, ExpressionDesugarOwner, collection);
                        break;

                    case InvocationExpressionSyntax invocation when IsExplicitInvoke(invocation):
                        Add(detected, relative, ExplicitInvoke,
                            CallCapability, null, invocation);
                        break;
                }

                var condition = ConditionOf(node);
                if (condition is not null
                    && JcsConditionFacts.TryFindViolation(condition, out var site, out _))
                {
                    Add(detected, relative, ConditionEffect,
                        EvalCapability, null, site ?? condition);
                }
            }
        }

        return detected;
    }

    private static void Add(
        List<DetectedConstruct> detected,
        string file,
        string id,
        string capability,
        string? candidateOwner,
        SyntaxNode node)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        detected.Add(new DetectedConstruct(
            id,
            capability,
            file,
            position.Line + 1,
            position.Character + 1,
            candidateOwner));
    }

    private static HashSet<string> CollectTypeParameters(SyntaxNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in root.DescendantNodes().OfType<TypeParameterListSyntax>())
        {
            foreach (var parameter in list.Parameters)
            {
                names.Add(parameter.Identifier.ValueText);
            }
        }

        return names;
    }

    private static bool IsTypeParameter(TypeSyntax type, HashSet<string> typeParameters)
        => type is IdentifierNameSyntax identifier
        && typeParameters.Contains(identifier.Identifier.ValueText);

    private static bool IsExplicitInvoke(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax member
        && string.Equals(member.Name.Identifier.ValueText, "Invoke", StringComparison.Ordinal);

    private static ExpressionSyntax? ConditionOf(SyntaxNode node)
        => node switch
        {
            IfStatementSyntax statement => statement.Condition,
            WhileStatementSyntax statement => statement.Condition,
            DoStatementSyntax statement => statement.Condition,
            ForStatementSyntax statement => statement.Condition,
            ConditionalExpressionSyntax expression => expression.Condition,
            _ => null,
        };

    private sealed record DetectedConstruct(
        string Id,
        string Capability,
        string File,
        int Line,
        int Column,
        string? CandidateOwner);
}
