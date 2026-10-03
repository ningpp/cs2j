using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// D13 recursive delegate gate (design M3 / risk note 8). The ordinary
/// capture lowering already gives a self-referential lambda a display-class
/// field (`fact` → `_cs2jCap_fact`); that shape is correct ONLY when every
/// delegate of the recursion group is stored before the group can be
/// entered. This handler
/// <list type="number">
/// <item>binds each liftable statement lambda to the local/parameter it is
/// stored into (`v = () => …`, `T v = () => …`),</item>
/// <item>builds the reference graph (a lambda whose body names another bound
/// delegate) and finds recursive groups (self loops and mutual SCCs),</item>
/// <item>admits a group only when v1 can prove the ordering: every storage
/// statement and every external reference statement is a direct statement of
/// the routine body block, each delegate is stored exactly once, no external
/// reference hides inside another lambda or under control flow, and the last
/// storage precedes the first external reference.</item>
/// </list>
/// Anything not provable trips CS2J4508 (strict Error, Warning when
/// non-strict): in strict mode the facade keeps the whole file verbatim
/// rather than emitting a recursion whose delegate cell might be read while
/// still null.
/// </summary>
internal static class RecursiveLambdaHandler
{
    public static bool Validate(
        CompilationUnitSyntax analysisRoot,
        SemanticModel model,
        ConsolidationPlan plan,
        ClosureLiftOptions options,
        List<ClosureLiftDiagnostic> diagnostics,
        ClosureLiftStatistics statistics)
    {
        // Nodes the consolidation actually migrates (model span coordinates).
        var planned = new HashSet<LambdaExpressionSyntax>(
            analysisRoot.DescendantNodes().OfType<LambdaExpressionSyntax>()
                .Where(l => plan.Lambdas.Any(p => p.SpanStart == l.SpanStart && p.RawKind == l.RawKind)),
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        if (planned.Count == 0)
            return true;

        var bindings = BuildBindings(analysisRoot, model);
        if (bindings.Targets.Count == 0)
            return true;

        var nodes = bindings.ByLambda.Keys.Where(planned.Contains).ToList();
        if (nodes.Count == 0)
            return true;

        var idByNode = new Dictionary<LambdaExpressionSyntax, int>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        for (var i = 0; i < nodes.Count; i++)
            idByNode[nodes[i]] = i;

        var edges = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            var targets = new List<int>();
            foreach (var id in nodes[i].DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(id).Symbol;
                if (symbol == null || !bindings.Targets.TryGetValue(symbol, out var boundList))
                    continue;

                foreach (var bound in boundList)
                {
                    if (planned.Contains(bound.Lambda) && idByNode.TryGetValue(bound.Lambda, out var j))
                        targets.Add(j);
                }
            }

            edges[i] = targets.Distinct().ToList();
        }

        var admitted = 0;
        foreach (var member in StronglyConnectedComponents(edges))
        {
            var isRecursive = member.Count > 1 || edges[member[0]].Contains(member[0]);
            if (!isRecursive)
                continue;

            var memberLambdas = member.Select(i => nodes[i]).ToHashSet<LambdaExpressionSyntax>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance);

            if (!TryProveOrdering(model, options, memberLambdas, bindings, diagnostics))
                return false;

            admitted += member.Count;
        }

        statistics.RecursiveLambdas += admitted;
        return true;
    }

    /// <summary>v1 ordering proof (see class doc). Emits one CS2J4508 row on failure.</summary>
    private static bool TryProveOrdering(
        SemanticModel model,
        ClosureLiftOptions options,
        HashSet<LambdaExpressionSyntax> memberLambdas,
        Bindings bindings,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        var storages = new List<StorageSite>();
        var targetSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var lambda in memberLambdas)
        {
            foreach (var bound in bindings.ByLambda[lambda])
            {
                targetSymbols.Add(bound.Symbol);
                storages.AddRange(bound.Storages);
            }
        }

        var first = memberLambdas.First();
        var routine = EnclosingRoutine(first);
        var bodyBlock = routine switch
        {
            BaseMethodDeclarationSyntax method => method.Body,
            LocalFunctionStatementSyntax local => local.Body,
            _ => null,
        };
        bool Reject(SyntaxNode at, string detail)
        {
            var lineSpan = at.SyntaxTree?.GetLineSpan(at.Span);
            diagnostics.Add(new ClosureLiftDiagnostic(
                options.Strict ? ClosureLiftSeverity.Error : ClosureLiftSeverity.Warning,
                ClosureLiftCodes.RecursiveLambdaUnprovable,
                "Recursive delegate group ("
                    + string.Join(", ", targetSymbols.Select(static s => s.Name).OrderBy(static n => n))
                    + $") cannot be proven assigned before it can be invoked: {detail}. The member is kept verbatim (D13).",
                lineSpan?.Path,
                (lineSpan?.StartLinePosition.Line ?? 0) + 1,
                RoutineName(routine)));
            return false;
        }

        if (routine == null)
            return Reject(first, "the lambda has no resolvable enclosing routine");

        if (bodyBlock == null)
            return Reject(first, "the enclosing routine has no block body where assignment order can be proven");

        foreach (var storage in storages)
        {
            // A delegate target assigned more than once defeats the
            // single-storage timing argument.
            var storageCount = bindings.Targets[storage.OwnerSymbol]
                .SelectMany(static b => b.Storages).Count();
            if (storageCount > 1)
                return Reject(storage.Statement, "a delegate of the group is assigned more than once");

            if (storage.Statement.Parent != bodyBlock)
            {
                return Reject(storage.Statement,
                    "a delegate assignment is nested under control flow rather than a direct routine statement");
            }
        }

        StatementSyntax? firstUse = null;
        foreach (var id in routine.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(id).Symbol;
            if (symbol == null || !targetSymbols.Contains(symbol))
                continue;

            // The LHS of a storage assignment is not an entry use.
            if (id.Parent is AssignmentExpressionSyntax assignment && assignment.Left == id)
                continue;

            var enclosingLambda = id.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
            if (enclosingLambda != null && memberLambdas.Contains(enclosingLambda))
                continue; // the recursive edge itself

            var useStatement = id.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
            if (useStatement == null)
                return Reject(id, "a delegate reference has no enclosing statement");

            if (enclosingLambda != null)
            {
                return Reject(id,
                    "a delegate is referenced from another lambda, whose invocation timing is not provable");
            }

            if (useStatement.Parent != bodyBlock)
            {
                return Reject(useStatement,
                    "an invocation site is nested under control flow rather than a direct routine statement");
            }

            if (firstUse == null || useStatement.SpanStart < firstUse.SpanStart)
                firstUse = useStatement;
        }

        if (firstUse != null && storages.Count > 0
            && firstUse.SpanStart <= storages.Max(static s => s.Statement.SpanStart))
        {
            return Reject(firstUse,
                "an invocation is reachable before every delegate of the group has been assigned");
        }

        return true;
    }

    private static SyntaxNode? EnclosingRoutine(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax)
                return ancestor;
        }

        return null;
    }

    private static string? RoutineName(SyntaxNode? routine) => routine switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        LocalFunctionStatementSyntax l => l.Identifier.Text,
        _ => null,
    };

    private static Bindings BuildBindings(CompilationUnitSyntax root, SemanticModel model)
    {
        var bindings = new Bindings();
        foreach (var lambda in root.DescendantNodes().OfType<LambdaExpressionSyntax>())
        {
            if (ClosureLiftAnnotations.IsAnnotated(lambda))
                continue;
            if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
                continue;

            ISymbol? target = null;
            StatementSyntax? storageStatement = null;

            if (lambda.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } equals
                && equals.Value == lambda)
            {
                target = model.GetDeclaredSymbol(declarator);
                storageStatement = declarator.Parent?.Parent as LocalDeclarationStatementSyntax;
            }
            else if (lambda.Parent is AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assign
                && assign.Right == lambda)
            {
                target = model.GetSymbolInfo(left).Symbol;
                storageStatement = assign.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
            }

            if (target is not (ILocalSymbol or IParameterSymbol) || storageStatement == null)
                continue;

            bindings.Add(target, lambda, storageStatement);
        }

        return bindings;
    }

    /// <summary>Iterative Tarjan SCC (avoids deep recursion on large files).</summary>
    private static List<List<int>> StronglyConnectedComponents(IReadOnlyList<List<int>> edges)
    {
        var n = edges.Count;
        var index = new int[n];
        var low = new int[n];
        var onStack = new bool[n];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        var result = new List<List<int>>();
        var nextIndex = 0;

        for (var start = 0; start < n; start++)
        {
            if (index[start] != -1)
                continue;

            var work = new Stack<(int Node, int NextEdge)>();
            work.Push((start, 0));
            index[start] = low[start] = nextIndex++;
            stack.Push(start);
            onStack[start] = true;

            while (work.Count > 0)
            {
                var (v, edgePos) = work.Peek();
                if (edgePos < edges[v].Count)
                {
                    var w = edges[v][edgePos];
                    work.Pop();
                    work.Push((v, edgePos + 1));
                    if (index[w] == -1)
                    {
                        index[w] = low[w] = nextIndex++;
                        stack.Push(w);
                        onStack[w] = true;
                        work.Push((w, 0));
                    }
                    else if (onStack[w])
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }
                }
                else
                {
                    if (low[v] == index[v])
                    {
                        var component = new List<int>();
                        int w;
                        do
                        {
                            w = stack.Pop();
                            onStack[w] = false;
                            component.Add(w);
                        } while (w != v);

                        result.Add(component);
                    }

                    work.Pop();
                    if (work.Count > 0)
                    {
                        var parent = work.Peek().Node;
                        low[parent] = Math.Min(low[parent], low[v]);
                    }
                }
            }
        }

        return result;
    }

    private sealed record StorageSite(ISymbol OwnerSymbol, StatementSyntax Statement);

    private sealed record BoundTarget(ISymbol Symbol, LambdaExpressionSyntax Lambda, List<StorageSite> Storages);

    private sealed class Bindings
    {
        public Dictionary<ISymbol, List<BoundTarget>> Targets { get; }
            = new(SymbolEqualityComparer.Default);

        public Dictionary<LambdaExpressionSyntax, List<BoundTarget>> ByLambda { get; }
            = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        public void Add(ISymbol symbol, LambdaExpressionSyntax lambda, StatementSyntax storage)
        {
            if (!Targets.TryGetValue(symbol, out var list))
            {
                list = new List<BoundTarget>();
                Targets[symbol] = list;
            }

            var bound = list.FirstOrDefault(b => ReferenceEquals(b.Lambda, lambda));
            if (bound == null)
            {
                bound = new BoundTarget(symbol, lambda, new List<StorageSite>());
                list.Add(bound);
                if (!ByLambda.TryGetValue(lambda, out var perLambda))
                {
                    perLambda = new List<BoundTarget>();
                    ByLambda[lambda] = perLambda;
                }

                perLambda.Add(bound);
            }

            bound.Storages.Add(new StorageSite(symbol, storage));
        }
    }
}
