using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Design §4.3 frozen planner. Pure (never edits trees): turns the read-only
/// ClosureAnalysisResult into the ordered HolderGroupPlan set, freezing group
/// keys, class/instance/field/storage names and the Task 10 rewriter
/// precondition boundaries. CS2J3207 prospective rows run through the SAME
/// key/naming calls, so audit names equal generated names.
/// </summary>
internal static class HolderPlanner
{
    public static IReadOnlyList<HolderGroupPlan> BuildPlans(
        ClosureAnalysisResult analysis,
        SemanticModel model,
        ClosureLiftOptions options,
        ClosureLiftNaming naming,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        var liftableByMember = analysis.Members
            .Select(m => (Member: m, Liftable: m.Symbols.Where(s => !s.Skipped).ToList()))
            .Where(t => t.Liftable.Count > 0)
            .ToList();

        // Pass 1: validate EVERY member first so non-strict runs report all
        // boundary rows in one pass; any boundary freezes the whole file.
        var blocked = false;
        foreach (var (member, liftable) in liftableByMember)
        {
            if (HasBlockingBoundary(member, liftable, options, diagnostics))
                blocked = true;
        }
        if (blocked)
            return Array.Empty<HolderGroupPlan>();

        // Pass 2: group + freeze names.
        var plans = new List<HolderGroupPlan>();
        var arrayForm = options.HolderForm == HolderForm.SingleElementArray;

        foreach (var (member, liftable) in liftableByMember)
        {
            var hostKey = HostTypeKey(model, member.HostType);
            var routineNames = CollectRoutineLocalNames(member.Routine);

            var grouped = liftable
                .GroupBy(BuildGroupKey)
                .OrderBy(g => g.Min(s => s.DeclaringReference.Span.Start))
                .ToList();

            var groupIndex = 0;
            foreach (var group in grouped)
            {
                var className = naming.ReserveClassName(hostKey, member.RoutineName);
                var instanceName = ClosureLiftNaming.ReserveInstanceName(groupIndex, routineNames);
                groupIndex++;

                var plan = new HolderGroupPlan
                {
                    HostType = member.HostType,
                    HostMember = member.Routine,
                    HostTypeKey = hostKey,
                    ClassName = className,
                    InstanceName = instanceName,
                    PerIteration = group.Any(s => s.IsForeachIteration),
                    ArrayForm = arrayForm,
                    PerIterationLoop = group
                        .Select(s => s.ForeachLoop)
                        .FirstOrDefault(loop => loop != null),
                };

                var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var symbol in group.OrderBy(s => s.DeclaringReference.Span.Start))
                {
                    var fieldName = ClosureLiftNaming.GetFieldName(symbol.Name, usedFieldNames);
                    var storageName = arrayForm
                        ? ReserveArrayStorageName(symbol.Name, routineNames)
                        : fieldName;

                    plan.Symbols.Add(new CapturedSymbolPlan(
                        symbol.Symbol,
                        symbol.Name,
                        fieldName,
                        storageName,
                        symbol.TypeDisplay,
                        symbol.DeclaringReference.GetSyntax(),
                        symbol.Reason,
                        symbol.IsForeachIteration,
                        symbol.ForeachLoop,
                        symbol.ForLoop,
                        symbol.Declarator,
                        symbol.Parameter,
                        symbol.Sites));
                }

                plans.Add(plan);
            }
        }

        return plans;
    }

    /// <summary>
    /// Group key (design §4.3): routine + declaring scope block + foreach
    /// per-iteration identity. for-induction symbols carry the loop's
    /// enclosing block (the M1 analyzer mapping) — do not re-scope them to
    /// the method body; the audit preview froze names against this mapping.
    /// Syntax nodes are compared by REFERENCE inside the key (never by
    /// SyntaxNode virtual equality).
    /// </summary>
    private sealed class GroupKey : IEquatable<GroupKey>
    {
        private readonly object _scope;
        private readonly bool _perIteration;
        private readonly ForEachStatementSyntax? _loop;

        public GroupKey(object scope, bool perIteration, ForEachStatementSyntax? loop)
        {
            _scope = scope;
            _perIteration = perIteration;
            _loop = loop;
        }

        public bool Equals(GroupKey? other)
            => other is not null
               && ReferenceEquals(_scope, other._scope)
               && _perIteration == other._perIteration
               && ReferenceEquals(_loop, other._loop);

        public override bool Equals(object? obj) => Equals(obj as GroupKey);

        public override int GetHashCode()
            => HashCode.Combine(
                RuntimeHelpers.GetHashCode(_scope),
                _perIteration,
                RuntimeHelpers.GetHashCode(_loop!));
    }

    private static GroupKey BuildGroupKey(CapturedSymbolAnalysis symbol)
        => new(
            (object?)symbol.ScopeBlock ?? symbol.EnclosingMember,
            symbol.IsForeachIteration,
            symbol.ForeachLoop);

    /// <summary>Task 10 rewriter preconditions (CS2J3204 v1 boundaries).</summary>
    private static bool HasBlockingBoundary(
        MemberCaptureAnalysis member,
        IReadOnlyList<CapturedSymbolAnalysis> liftable,
        ClosureLiftOptions options,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        var blocked = false;

        foreach (var symbol in liftable)
        {
            // Contract #1: for anchor must splice into a statement list.
            if (symbol.ForLoop is { } loop
                && loop.Parent is not (BlockSyntax or SwitchSectionSyntax))
            {
                EmitBoundary(options, diagnostics,
                    $"for-loop with a captured induction variable in '{member.RoutineName}' is an embedded statement; v1 boundary.",
                    loop.GetLocation(), member.RoutineName, symbol.Name);
                blocked = true;
            }

            // Contract #3: deconstruction foreach iteration variables are not
            // recognized as foreach variables by the M1 analyzer.
            var declaring = symbol.DeclaringReference.GetSyntax();
            if (declaring.Ancestors().OfType<ForEachVariableStatementSyntax>().Any())
            {
                EmitBoundary(options, diagnostics,
                    $"Deconstruction foreach variable '{symbol.Name}' mutably captured in '{member.RoutineName}' is unsupported in v1.",
                    symbol.Symbol.Locations.FirstOrDefault(), member.RoutineName, symbol.Name);
                blocked = true;
            }
        }

        // Contract #2 (defensive; the analyzer already rejects body-less
        // routines before candidates exist): parameter capture needs a block.
        var body = (member.Routine as BaseMethodDeclarationSyntax)?.Body
            ?? (member.Routine as AccessorDeclarationSyntax)?.Body;
        if (body is null && liftable.Any(s => s.Parameter != null))
        {
            EmitBoundary(options, diagnostics,
                $"Routine '{member.RoutineName}' has no block body to host a parameter holder instance.",
                member.Routine.GetLocation(), member.RoutineName, null);
            blocked = true;
        }

        return blocked;
    }

    private static void EmitBoundary(
        ClosureLiftOptions options,
        List<ClosureLiftDiagnostic> diagnostics,
        string message,
        Location? location,
        string? memberName,
        string? symbolName)
    {
        var severity = options.Strict
            ? ClosureLiftSeverity.Error
            : ClosureLiftSeverity.Warning;
        var line = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
        diagnostics.Add(new ClosureLiftDiagnostic(
            severity, ClosureLiftCodes.UnsupportedBoundary, message,
            null, line, memberName, symbolName));
    }

    private static string ReserveArrayStorageName(string originalName, ISet<string> routineNames)
    {
        var candidate = ClosureLiftNaming.GetArrayLocalName(originalName);
        if (routineNames.Add(candidate))
            return candidate;

        for (var suffix = 2; ; suffix++)
        {
            var suffixed = $"{candidate}_{suffix}";
            if (routineNames.Add(suffixed))
                return suffixed;
        }
    }

    internal static string HostTypeKey(SemanticModel model, TypeDeclarationSyntax hostType)
        => model.GetDeclaredSymbol(hostType) is { } hostSymbol
            ? hostSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : hostType.Identifier.Text;

    // ── Audit tail (migrated verbatim from the M1 preprocessor except the
    //    shared BuildGroupKey; CS2J3207 rows only ever run in AuditOnly). ──

    public static void EmitAuditRows(
        ClosureAnalysisResult analysis,
        SemanticModel model,
        ClosureLiftNaming naming,
        ClosureLiftStatistics stats,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        foreach (var member in analysis.Members)
        {
            var hostKey = HostTypeKey(model, member.HostType);
            var liftable = member.Symbols.Where(s => !s.Skipped).ToList();
            var groups = liftable
                .GroupBy(BuildGroupKey)
                .OrderBy(g => g.Min(s => s.DeclaringReference.Span.Start))
                .ToList();

            var routineNames = CollectRoutineLocalNames(member.Routine);
            var groupIndex = 0;
            foreach (var group in groups)
            {
                var className = naming.ReserveClassName(hostKey, member.RoutineName);
                var instanceName = ClosureLiftNaming.ReserveInstanceName(groupIndex, routineNames);
                groupIndex++;

                var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
                if (group.Any(s => s.IsForeachIteration))
                    stats.ForeachPerIterationGroups += 1;

                foreach (var symbol in group)
                {
                    var fieldName = ClosureLiftNaming.GetFieldName(symbol.Name, usedFieldNames);
                    stats.CapturesByReason[symbol.Reason] =
                        stats.CapturesByReason.TryGetValue(symbol.Reason, out var n) ? n + 1 : 1;

                    var lineSpan = symbol.DeclaringReference.SyntaxTree
                        .GetLineSpan(symbol.DeclaringReference.Span);
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var shape = symbol.IsForeachIteration ? "foreach-per-iteration" : "shared";

                    diagnostics.Add(new ClosureLiftDiagnostic(
                        ClosureLiftSeverity.Info,
                        ClosureLiftCodes.AuditRow,
                        $"[audit] {member.RoutineName}.{symbol.Name}: {symbol.Reason} ({shape}), " +
                        $"sites={symbol.Sites.Count}, quoted={symbol.QuotedSites.Count}, " +
                        $"holder={className}, instance={instanceName}, field={fieldName}, type={symbol.TypeDisplay}",
                        null, line, member.RoutineName, symbol.Name, symbol.Reason));
                }
            }
        }
    }

    /// <summary>
    /// Seed set for per-routine instance reservation (M1 shape, unchanged):
    /// parameters + every declarator / classic foreach iteration variable /
    /// local function name inside the routine. Seeding nested-scope names is
    /// conservative; the audit emitter and the transform planner must call
    /// this identical helper. SyntaxNode (not MemberDeclarationSyntax) because
    /// routine-typed accessors are analyzed as AccessorDeclarationSyntax.
    /// </summary>
    internal static HashSet<string> CollectRoutineLocalNames(SyntaxNode routine)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (routine is BaseMethodDeclarationSyntax method)
        {
            foreach (var parameter in method.ParameterList.Parameters)
            {
                var text = parameter.Identifier.Text;
                if (text.Length > 0)
                    names.Add(text);
            }
        }

        foreach (var declarator in routine.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            names.Add(declarator.Identifier.Text);
        foreach (var foreachLoop in routine.DescendantNodes().OfType<ForEachStatementSyntax>())
            names.Add(foreachLoop.Identifier.Text);
        foreach (var localFunction in routine.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            names.Add(localFunction.Identifier.Text);

        return names;
    }
}
