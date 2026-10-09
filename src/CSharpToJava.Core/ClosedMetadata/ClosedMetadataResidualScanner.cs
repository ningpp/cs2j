using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Post-rewrite residual gate over the recompiled intermediate tree
/// (design §5.4, M4c rules 1/3/4/6; duplicate/conflict key rules 2/5 stay a
/// content scan of the generated registration files):
/// <list type="bullet">
/// <item><term>erased-type-consumption</term><description>
/// rule 1 — D8 serializer entries invoked through the non-generic erased
/// <c>Type</c> overloads with a bare <c>typeof(闭型)</c> argument, without an
/// adjacent guard proving registration reachability;</description></item>
/// <item><term>bare-typeof-passthrough</term><description>
/// rule 3 — bare <c>typeof(closed construction)</c> nodes outside the generated
/// registration files (CM-SRCGEN AOT zones excluded, CS2J1204 owns them),
/// i.e. shapes that would silently flow into Java erasure;</description></item>
/// <item><term>unguarded-consumer-entry</term><description>
/// rule 4 — generic D8 entries the linker could serve but that carry no
/// adjacent bootstrap guard, including the shapes M4b deliberately defers
/// (expression-bodied members, initializers, unreachable framework holders);</description></item>
/// <item><term>rewrite-forbidden-zone</term><description>
/// rule 6 — structural self-check: a bootstrap guard appearing inside an
/// expression tree (<c>Expression&lt;...&gt;</c> lambda) or a
/// JsonSerializerContext-derived type; must always be zero.</description></item>
/// </list>
/// Findings carry CS2J1220 (Warning; strict Error). Explicit skip/force
/// overrides suppress a finding and leave a CLO001 Info trace.
/// </summary>
public static class ClosedMetadataResidualScanner
{
    /// <summary>Rule 1: erased-Type reflection consumption without a reachable registration.</summary>
    public const string ErasedTypeConsumptionRule = "erased-type-consumption";

    /// <summary>Rule 3: bare typeof(closed) passthrough outside registration files.</summary>
    public const string BareTypeOfPassthroughRule = "bare-typeof-passthrough";

    /// <summary>Rule 4: servable consumer entry without an adjacent bootstrap guard.</summary>
    public const string UnguardedEntryRule = "unguarded-consumer-entry";

    /// <summary>Rule 6: bootstrap guard inside an expression tree / source-gen context.</summary>
    public const string ForbiddenZoneRewriteRule = "rewrite-forbidden-zone";

    private const int MaxSiteHints = 3;

    public static ClosedMetadataResidualResult Scan(
        Compilation rewritten,
        ClosedMetadataAnalysis analysis,
        Func<string, string?> holderNameByPath,
        ClosedMetadataOptions options,
        ClosedMetadataOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(rewritten);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(holderNameByPath);
        ArgumentNullException.ThrowIfNull(options);

        var holderByRecordKey = ClosedMetadataGuardResolver.BuildHolderByRecordKey(
            analysis, holderNameByPath, out var holderNames);

        var findings = new List<Finding>();
        var infos = new List<ClosedMetadataDiagnostic>();
        // One statement may carry several serializer invocations (nested
        // Serialize(Deserialize(...))): dedupe per rule, never across rules.
        var reportedStatements = new HashSet<(string Rule, StatementSyntax Statement)>();

        foreach (var tree in rewritten.SyntaxTrees)
        {
            if (IsGeneratedRegistrationFile(tree.FilePath))
            {
                continue;
            }

            var root = tree.GetRoot();
            var model = rewritten.GetSemanticModel(tree);
            var selfHolder = holderNameByPath(tree.FilePath);

            ScanTypeOfNodes(root, model, findings, infos, overrides);
            ScanSerializerEntries(root, model, selfHolder, holderNameByPath,
                holderByRecordKey, holderNames, findings, infos, overrides, reportedStatements);
            ScanGuardLeaks(root, model, findings);
        }

        var counts = findings
            .GroupBy(finding => finding.Rule, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var diagnostics = new List<ClosedMetadataDiagnostic>(infos);
        foreach (var group in findings.GroupBy(finding => finding.Rule, StringComparer.Ordinal))
        {
            var hints = group.Take(MaxSiteHints)
                .Select(finding => $"{finding.Location.SourceTree?.FilePath ?? "<memory>"}:" +
                    $"{finding.Location.GetLineSpan().StartLinePosition.Line + 1}");
            diagnostics.Add(new ClosedMetadataDiagnostic(
                options.Strict ? ClosedMetadataSeverity.Error : ClosedMetadataSeverity.Warning,
                ClosedMetadataRule.Residual,
                ClosedMetadataCodes.ResidualFound,
                $"{group.Count()} residual site(s) of rule '{group.Key}' remain after the closed-metadata " +
                $"rewrite ({string.Join(", ", hints)}); the post-rewrite gate requires zero (design §5.4)."));
        }

        return new ClosedMetadataResidualResult(counts, diagnostics);
    }

    // ---- rule 3: bare typeof(closed) passthrough ----

    private static void ScanTypeOfNodes(
        SyntaxNode root,
        SemanticModel model,
        List<Finding> findings,
        List<ClosedMetadataDiagnostic> infos,
        ClosedMetadataOverrides? overrides)
    {
        foreach (var typeofExpression in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            // CM-SRCGEN: the AOT [JsonSerializable(typeof(...))] declaration form is
            // already owned by CS2J1204 — not a passthrough leak.
            if (typeofExpression.FirstAncestorOrSelf<AttributeSyntax>() is { } attribute
                && ClosedMetadataCollector.IsJsonSerializableAttributeName(attribute.Name.ToString()))
            {
                continue;
            }

            if (IsInsideSourceGenContext(model, typeofExpression.SpanStart))
            {
                continue;
            }

            var type = model.GetTypeInfo(typeofExpression.Type).Type;
            if (type is null || !ClosedMetadataCollector.ContainsGenericConstruction(type))
            {
                continue;
            }

            var member = model.GetEnclosingSymbol(typeofExpression.SpanStart);
            if (overrides is not null
                && overrides.IsSuppressed(member, new[] { type }, out var entry, out var force))
            {
                AddSuppressionInfo(infos, BareTypeOfPassthroughRule, entry!, force,
                    typeofExpression.GetLocation());
                continue;
            }

            findings.Add(new Finding(
                BareTypeOfPassthroughRule, typeofExpression.GetLocation(),
                $"bare typeof({type.ToDisplayString()}) outside the generated registration files"));
        }
    }

    // ---- rules 1 + 4: serializer entry coverage ----

    private static void ScanSerializerEntries(
        SyntaxNode root,
        SemanticModel model,
        string? selfHolder,
        Func<string, string?> holderNameByPath,
        Dictionary<string, string> holderByRecordKey,
        HashSet<string> holderNames,
        List<Finding> findings,
        List<ClosedMetadataDiagnostic> infos,
        ClosedMetadataOverrides? overrides,
        HashSet<(string Rule, StatementSyntax Statement)> reportedStatements)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || !ClosedMetadataCollector.IsSerializerEntry(method))
            {
                continue;
            }

            if (method.IsGenericMethod)
            {
                ScanGenericEntry(invocation, method, model, selfHolder, holderNameByPath,
                    holderByRecordKey, holderNames, findings, infos, overrides, reportedStatements);
            }
            else
            {
                ScanErasedTypeEntry(invocation, method, model, findings, infos, overrides, reportedStatements);
            }
        }
    }

    private static void ScanGenericEntry(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        string? selfHolder,
        Func<string, string?> holderNameByPath,
        Dictionary<string, string> holderByRecordKey,
        HashSet<string> holderNames,
        List<Finding> findings,
        List<ClosedMetadataDiagnostic> infos,
        ClosedMetadataOverrides? overrides,
        HashSet<(string Rule, StatementSyntax Statement)> reportedStatements)
    {
        // Dynamic sites are CM-DYN territory (CS2J1210), and sites without a
        // reachable holder are unservable by construction.
        if (method.TypeArguments.Any(type => type is ITypeParameterSymbol))
        {
            return;
        }

        // The gate deliberately ignores linker reachability: an entry that
        // consumes a registered closure without a guard is a finding even when
        // the guard could not compile here (cross-project framework holder) —
        // those sites get triaged via skip/force overrides rather than hidden.
        var requiredHolders = ClosedMetadataGuardResolver.ResolveHolderNames(
            method.TypeArguments, holderByRecordKey, holderNames, selfHolder, holderNameByPath,
            requireReachable: false);
        if (requiredHolders.Count == 0)
        {
            return;
        }

        var statement = invocation.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is not null && !reportedStatements.Add((UnguardedEntryRule, statement)))
        {
            return;
        }

        var member = model.GetEnclosingSymbol(invocation.SpanStart);
        if (overrides is not null
            && overrides.IsSuppressed(member, method.TypeArguments, out var entry, out var force))
        {
            AddSuppressionInfo(infos, UnguardedEntryRule, entry!, force, invocation.GetLocation());
            return;
        }

        if (statement is null)
        {
            findings.Add(new Finding(
                UnguardedEntryRule, invocation.GetLocation(),
                $"{method.ContainingType.Name}.{method.Name} entry has no statement anchor " +
                "(expression-bodied member/initializer); CM-LINK cannot insert a guard"));
            return;
        }

        if (!AdjacentGuardsCover(statement, requiredHolders))
        {
            findings.Add(new Finding(
                UnguardedEntryRule, invocation.GetLocation(),
                $"{method.ContainingType.Name}.{method.Name} entry is not preceded by the bootstrap " +
                $"guard(s) {string.Join(", ", requiredHolders.Select(h => "'" + h + "'"))}"));
        }
    }

    private static void ScanErasedTypeEntry(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        List<Finding> findings,
        List<ClosedMetadataDiagnostic> infos,
        ClosedMetadataOverrides? overrides,
        HashSet<(string Rule, StatementSyntax Statement)> reportedStatements)
    {
        if (invocation.ArgumentList is not { } argumentList)
        {
            return;
        }

        foreach (var argument in argumentList.Arguments)
        {
            if (argument.Expression is not TypeOfExpressionSyntax typeofExpression)
            {
                continue;
            }

            var type = model.GetTypeInfo(typeofExpression.Type).Type;
            if (type is null || !ClosedMetadataCollector.ContainsGenericConstruction(type))
            {
                continue;
            }

            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>();
            if (statement is not null && !reportedStatements.Add((ErasedTypeConsumptionRule, statement)))
            {
                return;
            }

            var member = model.GetEnclosingSymbol(invocation.SpanStart);
            if (overrides is not null
                && overrides.IsSuppressed(member, new[] { type }, out var entry, out var force))
            {
                AddSuppressionInfo(infos, ErasedTypeConsumptionRule, entry!, force,
                    invocation.GetLocation());
                return;
            }

            // An immediately preceding guard run proves a bootstrap (any holder)
            // reaches the site along execution; M4b never guards erased overloads,
            // so this only holds for hand-written/forced guards.
            if (statement is not null && HasAdjacentGuard(statement))
            {
                continue;
            }

            findings.Add(new Finding(
                ErasedTypeConsumptionRule, invocation.GetLocation(),
                $"{method.ContainingType.Name} entry consumes '{type.ToDisplayString()}' through the erased " +
                "Type overload without a reachable registration guard"));
        }
    }

    // ---- rule 6: guard leaks into expression trees / source-gen contexts ----

    private static void ScanGuardLeaks(SyntaxNode root, SemanticModel model, List<Finding> findings)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsGuardInvocation(invocation))
            {
                continue;
            }

            if (IsInsideSourceGenContext(model, invocation.SpanStart))
            {
                findings.Add(new Finding(
                    ForbiddenZoneRewriteRule, invocation.GetLocation(),
                    "bootstrap guard emitted inside a JsonSerializerContext-derived (AOT) type"));
                continue;
            }

            foreach (var ancestor in invocation.Ancestors())
            {
                if (ancestor is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
                {
                    continue;
                }

                var convertedType = model.GetTypeInfo(ancestor).ConvertedType;
                if (IsExpressionTreeType(convertedType))
                {
                    findings.Add(new Finding(
                        ForbiddenZoneRewriteRule, invocation.GetLocation(),
                        "bootstrap guard emitted inside an expression-tree lambda (Expression<TDelegate>)"));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The M4b linker emits every required holder guard as its own statement in a
    /// consecutive run immediately before the consumer statement; the gate accepts
    /// the site only when that run covers every serving holder.
    /// </summary>
    private static bool AdjacentGuardsCover(StatementSyntax statement, IReadOnlySet<string> requiredHolders)
    {
        if (!TryGetSiblingStatements(statement, out var siblings, out var selfIndex))
        {
            return false;
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        for (var i = selfIndex - 1; i >= 0; i--)
        {
            if (!TryGetGuardHolder(siblings[i], out var holder))
            {
                break;
            }

            covered.Add(holder!);
        }

        return requiredHolders.All(covered.Contains);
    }

    private static bool HasAdjacentGuard(StatementSyntax statement)
    {
        if (!TryGetSiblingStatements(statement, out var siblings, out var selfIndex))
        {
            return false;
        }

        return selfIndex > 0 && TryGetGuardHolder(siblings[selfIndex - 1], out _);
    }

    private static bool TryGetSiblingStatements(
        StatementSyntax statement,
        out IReadOnlyList<StatementSyntax> siblings,
        out int selfIndex)
    {
        switch (statement.Parent)
        {
            case BlockSyntax block:
                return MapIndex(block.Statements, statement, out siblings, out selfIndex);
            case SwitchSectionSyntax section:
                return MapIndex(section.Statements, statement, out siblings, out selfIndex);
            case GlobalStatementSyntax { Parent: CompilationUnitSyntax unit }:
                var globals = unit.Members
                    .OfType<GlobalStatementSyntax>()
                    .Select(global => global.Statement)
                    .ToList();
                return MapIndex(globals, statement, out siblings, out selfIndex);
            default:
                siblings = Array.Empty<StatementSyntax>();
                selfIndex = -1;
                return false;
        }
    }

    private static bool MapIndex(
        IReadOnlyList<StatementSyntax> list,
        StatementSyntax statement,
        out IReadOnlyList<StatementSyntax> siblings,
        out int selfIndex)
    {
        siblings = list;
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], statement))
            {
                selfIndex = i;
                return true;
            }
        }

        selfIndex = -1;
        return false;
    }

    private static bool TryGetGuardHolder(StatementSyntax statement, out string? holder)
    {
        holder = null;
        if (statement is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.Text: ClosedMetadataRegistrationGenerator.BootstrapMethodName,
                    } memberAccess,
                },
            })
        {
            return false;
        }

        var text = memberAccess.Expression.ToString();
        const string globalPrefix = "global::";
        holder = text.StartsWith(globalPrefix, StringComparison.Ordinal)
            ? text[globalPrefix.Length..]
            : text;
        return true;
    }

    private static bool IsGuardInvocation(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name.Identifier.Text == ClosedMetadataRegistrationGenerator.BootstrapMethodName;

    private static bool IsInsideSourceGenContext(SemanticModel model, int position)
    {
        var current = model.GetEnclosingSymbol(position)?.ContainingType;
        while (current is not null)
        {
            if (ClosedMetadataCollector.IsSourceGenContext(current))
            {
                return true;
            }

            current = current.ContainingType;
        }

        return false;
    }

    private static bool IsExpressionTreeType(ITypeSymbol? type)
        => type is INamedTypeSymbol named
            && named is { Name: "Expression", IsGenericType: true }
            && named.ContainingNamespace.ToDisplayString() == "System.Linq.Expressions";

    private static bool IsGeneratedRegistrationFile(string? filePath)
        => filePath is not null
            && Path.GetFileName(filePath).StartsWith(
                ClosedMetadataRegistrationGenerator.GeneratedFileStem + ".", StringComparison.Ordinal);

    private static void AddSuppressionInfo(
        List<ClosedMetadataDiagnostic> infos,
        string rule,
        string entry,
        bool force,
        Location location)
    {
        var lineSpan = location.GetLineSpan();
        infos.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Info,
            ClosedMetadataRule.Residual,
            ClosedMetadataCodes.Info,
            $"residual rule '{rule}' {(force ? "covered by force override (out-of-band registration asserted)" : "skipped by override")} " +
            $"'{entry}' (design §6.3).",
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1));
    }

    private sealed record Finding(string Rule, Location Location, string Detail);
}
