using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Statement-level lifting of internal ref/out call sites (C1/C2/C3).
/// Splices ByRef proxy construction out of arbitrary expression positions while
/// preserving short-circuit evaluation, left-to-right side effects and reverse
/// read-back order. Semantic lookups only ever target ORIGINAL-tree nodes.
///
/// Fragment discipline: generated statements are pre-injected (.Value via
/// InjectSlotValues, annotated for idempotence) and are never re-visited; the
/// statement visitors assemble blocks from fragments + manually-visited original
/// children. Direct-pass slot arguments carry DirectPassAnnotation and are
/// exempt from injection.
/// </summary>
internal sealed class RefOutInStatementLifter
{
    internal static readonly SyntaxAnnotation DirectPassAnnotation = new("Cs2jDirectPass");
    internal static readonly SyntaxAnnotation ValueInjectedAnnotation = new("Cs2jValueInjected");
    /// <summary>Marks statement-level blocks produced by lifting; VisitBlock flattens them into the parent scope.</summary>
    internal static readonly SyntaxAnnotation HoistedBlockAnnotation = new("Cs2jHoistedBlock");
    /// <summary>
    /// Marks a pure variable declaration hoisted from an <c>out var x</c>
    /// designation. It has no condition-evaluation side effects and may be
    /// placed before a do-while body (where x must be in scope); all other
    /// hoisted statements must run where the condition is evaluated.
    /// </summary>
    internal static readonly SyntaxAnnotation RootDeclarationAnnotation = new("Cs2jRootDeclaration");

    private readonly RefOutInSyntaxRewriter _owner;
    private List<StatementSyntax> _out = new();
    private List<StatementSyntax> _rootOut = new();
    private readonly Stack<List<StatementSyntax>> _scope = new();
    private readonly List<StatementSyntax> _pendingReadbacks = new();

    // Invocation liftability is a pure function of the (immutable) original
    // tree node + semantic model. Every statement used to re-resolve its
    // invocations once per enclosing candidate (O(k^2) bindings per
    // expression); cache per file so each invocation binds once.
    private readonly Dictionary<InvocationExpressionSyntax, IReadOnlyList<IMethodSymbol>?> _liftableCache =
        new(ReferenceEqualityComparer.Instance);


    public RefOutInStatementLifter(RefOutInSyntaxRewriter owner) => _owner = owner;

    // ── entry points ────────────────────────────────────────────────────────

    /// <summary>
    /// Splice one expression. Appends hoisted statements and returns the
    /// remainder (unchanged when nothing was lifted). The caller must apply
    /// InjectSlotValues to the remainder before embedding it in a fragment.
    /// </summary>
    public ExpressionSyntax SpliceExpression(ExpressionSyntax expr, out List<StatementSyntax> hoisted)
    {
        _out = _rootOut = new List<StatementSyntax>();
        _scope.Clear();
        _pendingReadbacks.Clear();
        var remainder = Splice(expr);
        hoisted = _out;
        return remainder;
    }

    /// <summary>Read-backs for void calls, to be flushed after the call statement.</summary>
    public List<StatementSyntax> TakePendingReadbacks()
    {
        var pending = new List<StatementSyntax>(_pendingReadbacks);
        _pendingReadbacks.Clear();
        return pending;
    }

    public bool ContainsLiftable(ExpressionSyntax expr)
    {
        if (expr == null) return false;
        foreach (var inv in InvocationsOutsideLambdas(expr))
            if (IsLiftable(inv)) return true;
        return false;
    }

    // ── expression splicing ─────────────────────────────────────────────────

    private ExpressionSyntax Splice(ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax inv && IsLiftable(inv))
            return LiftInvocation(inv);

        switch (expr.Kind())
        {
            case SyntaxKind.LogicalAndExpression:
            case SyntaxKind.LogicalOrExpression:
                return SpliceShortCircuit((BinaryExpressionSyntax)expr);
            case SyntaxKind.CoalesceExpression:
                return SpliceCoalesce((BinaryExpressionSyntax)expr);
            case SyntaxKind.ConditionalExpression:
                return SpliceConditional((ConditionalExpressionSyntax)expr);
            default:
                return SpliceChildren(expr);
        }
    }

    private ExpressionSyntax SpliceShortCircuit(BinaryExpressionSyntax bin)
    {
        var left = Splice(bin.Left);
        if (!ContainsLiftable(bin.Right))
            return bin.WithLeft(left);

        bool isAnd = bin.IsKind(SyntaxKind.LogicalAndExpression);
        var temp = Temp("__sc");
        _out.Add(ParseStmt($"bool {temp} = {(isAnd ? "false" : "true")};"));
        PushScope();
        var rightValue = Splice(bin.Right);
        _out.Add(ParseStmt($"{temp} = {rightValue};"));
        var conditional = PopScope();

        var gate = isAnd ? left : Negate(left);
        _out.Add(SyntaxFactory.IfStatement(gate, conditional));
        return Identifier(temp);
    }

    private ExpressionSyntax SpliceCoalesce(BinaryExpressionSyntax bin)
    {
        var left = Splice(bin.Left);
        if (!ContainsLiftable(bin.Right))
            return bin.WithLeft(left);

        var typeName = TypeNameOf(bin) ?? "object";
        var temp = Temp("__cs");
        _out.Add(ParseStmt($"{typeName} {temp} = {left};"));
        PushScope();
        var rightValue = Splice(bin.Right);
        _out.Add(BranchAssignment(temp, rightValue));
        var conditional = PopScope();

        _out.Add(SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, Identifier(temp),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            conditional));
        return Identifier(temp);
    }

    /// <summary>
    /// Statement for a hoisted conditional/coalesce branch result: a bare
    /// <c>throw</c> expression cannot legally be the RHS of an assignment
    /// (CS8115), so emit a throw statement for that branch.
    /// </summary>
    private static StatementSyntax BranchAssignment(string temp, ExpressionSyntax value)
        => value is ThrowExpressionSyntax throwExpression
            ? SyntaxFactory.ThrowStatement(throwExpression.Expression)
            : ParseStmt($"{temp} = {value};");

    private ExpressionSyntax SpliceConditional(ConditionalExpressionSyntax cond)
    {
        // Mirror SpliceShortCircuit/SpliceCoalesce: statement-splitting a
        // ternary is only legal (and necessary) when at least one liftable
        // ref/out call lives in the condition or a branch. Otherwise the
        // untouched expression must stay in place — hoisting it emits an
        // untyped `object __cond` temp and breaks value-typed consumers
        // (CS0266 object->bool at `bool x = __cond;`). SpliceChildren is a
        // no-op walk for the lift-free case.
        if (!ContainsLiftable(cond))
        {
            return SpliceChildren(cond);
        }

        var condition = Splice(cond.Condition);
        var typeName = TypeNameOf(cond) ?? "object";
        var temp = Temp("__cond");
        var typeSymbol = _owner.GetTypeOf(cond);
        var defaultLit = typeSymbol is { IsValueType: true } ? "default" : "default!";
        _out.Add(ParseStmt($"{typeName} {temp} = {defaultLit};"));

        PushScope();
        _out.Add(BranchAssignment(temp, Splice(cond.WhenTrue)));
        var trueBlock = PopScope();

        PushScope();
        _out.Add(BranchAssignment(temp, Splice(cond.WhenFalse)));
        var falseBlock = PopScope();

        _out.Add(SyntaxFactory.IfStatement(condition, trueBlock, SyntaxFactory.ElseClause(falseBlock)));
        return Identifier(temp);
    }

    private ExpressionSyntax SpliceChildren(ExpressionSyntax expr)
    {
        // Top-most descendants that contain liftables are spliced recursively;
        // their own dispatch handles conditional positions inside them.
        ExpressionSyntax? found = null;
        foreach (var desc in expr.DescendantNodes())
        {
            if (desc is not ExpressionSyntax candidate) continue;
            if (candidate is not (InvocationExpressionSyntax or BinaryExpressionSyntax
                or ConditionalExpressionSyntax)) continue;
            if (!ContainsLiftable(candidate)) continue;
            if (found == null || found.DescendantNodes().Contains(candidate))
                found = candidate;
        }
        if (found == null) return expr;
        var replaced = expr.ReplaceNode(found, Splice(found));
        return Splice(replaced);
    }

    // ── invocation lifting ──────────────────────────────────────────────────

    private ExpressionSyntax LiftInvocation(InvocationExpressionSyntax invocation)
    {
        TryGetLiftMethods(invocation, out var liftMethods);
        // Representative candidate: byref parameter types/kinds and the return
        // kind are identical across the list (validated in RefArgumentShapeAgrees).
        var method = liftMethods[0];
        var readbacks = new List<StatementSyntax>();
        var newArgs = new List<ArgumentSyntax>();
        bool anyLifted = false;

        for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var arg = invocation.ArgumentList.Arguments[i];
            if (arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword))
            {
                // CD8: kept-in methods keep the keyword; stripped methods drop it.
                if (_owner.IsInKept(method, i)) { newArgs.Add(arg); continue; }
                newArgs.Add(SyntaxFactory.Argument(_owner.InjectSlotValues(Splice(arg.Expression))));
                continue;
            }
            if (!arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                && !arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                newArgs.Add(SyntaxFactory.Argument(_owner.InjectSlotValues(Splice(arg.Expression))));
                continue;
            }

            bool isOut = arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);
            var lifted = LiftArgument(method, i, arg, isOut);
            if (lifted.decl != null) _out.Add(lifted.decl);
            if (lifted.readback != null) readbacks.Add(lifted.readback);
            newArgs.Add(lifted.arg);
            anyLifted = true;
        }

        if (!anyLifted)
            return invocation;

        _owner.Stats.CallSitesLifted++;
        _owner.MarkHolderUsed();
        var newInvocation = invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));

        readbacks.Reverse();
        if (method.ReturnsVoid)
        {
            // The call statement itself is assembled by the caller (expression
            // statement / arrow body); read-backs must follow it, so they go to
            // the pending buffer.
            _pendingReadbacks.AddRange(readbacks);
            return newInvocation;
        }

        var temp = Temp("__call");
        _out.Add(ParseStmt($"var {temp} = {newInvocation};"));
        _out.AddRange(readbacks);
        return Identifier(temp);
    }

    private (StatementSyntax? decl, StatementSyntax? readback, ArgumentSyntax arg) LiftArgument(
        IMethodSymbol method, int index, ArgumentSyntax arg, bool isOut)
    {
        var expr = arg.Expression;
        while (expr is ParenthesizedExpressionSyntax p) expr = p.Expression;
        var paramTypeSymbol = method.Parameters[index].Type;
        var paramType = _owner.TypeDisplay(paramTypeSymbol);

        // (1) holder slot direct pass — internal callee now expects ByRef<T>/RefStructByRef<T>
        if (expr is IdentifierNameSyntax slotId && _owner.IsHolderName(slotId.Identifier.Text))
        {
            var id = SyntaxFactory.IdentifierName(slotId.Identifier.Text)
                .WithAdditionalAnnotations(DirectPassAnnotation);
            return (null, null, SyntaxFactory.Argument(id));
        }

        // (2) discard: out _
        if (expr is IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            var slot = Temp("__out");
            return (ParseStmt($"ByRef<{paramType}> {slot} = ByRef<{paramType}>.Create();"),
                null, SyntaxFactory.Argument(Identifier(slot)));
        }

        // (3) declaration expression: out var x / out T x
        if (expr is DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax svd })
        {
            var varName = svd.Identifier.Text;
            var defaultLit = paramTypeSymbol.IsValueType ? "default" : "default!";
            // The declared variable is visible in the enclosing statement scope
            // (e.g. an if body), so the declaration escapes conditional splices.
            _rootOut.Add(ParseStmt($"{paramType} {varName} = {defaultLit};")
                .WithAdditionalAnnotations(RootDeclarationAnnotation));
            var slot = Temp("__out");
            return (ParseStmt($"ByRef<{paramType}> {slot} = ByRef<{paramType}>.Create();"),
                ParseStmt($"{varName} = {slot}.Value;"),
                SyntaxFactory.Argument(Identifier(slot)));
        }

        // (4) ref struct typed argument → RefStructByRef slot (copy-in/copy-out;
        //     closures cannot capture ref structs, CS8175)
        var argType = _owner.GetTypeOf(expr);
        if (argType != null && argType.IsRefLikeType)
        {
            var t = _owner.TypeDisplay(argType);
            var slot = Temp(isOut ? "__out" : "__ref");
            var ctor = isOut
                ? $"RefStructByRef<{t}>.Create()"
                : $"RefStructByRef<{t}>.Create({_owner.InjectSlotValues(Splice(expr))})";
            return (ParseStmt($"Cs2jByRef.RefStructByRef<{t}> {slot} = {ctor};"),
                ParseStmt($"{_owner.InjectSlotValues(expr)} = {slot}.Value;"),
                SyntaxFactory.Argument(Identifier(slot)));
        }

        // (4b) ref-returning property/indexer use site: the expression already
        // evaluates to ByRef<T> after the declaration rewrite — direct pass.
        var useSym = _owner.GetSymbol(expr);
        if (useSym is IPropertySymbol useProp
            && (useProp.RefKind == RefKind.Ref || useProp.RefKind == RefKind.RefReadOnly))
        {
            return (null, null, SyntaxFactory.Argument(
                expr.WithAdditionalAnnotations(DirectPassAnnotation)));
        }

        // (5) element access
        if (expr is ElementAccessExpressionSyntax elem)
        {
            var arrayType = _owner.GetTypeOf(elem.Expression) as IArrayTypeSymbol;
            if (arrayType != null && arrayType.Rank != 1)
            {
                Report3303(arg.Expression,
                    $"multi-dimensional array element ref (rank {arrayType.Rank}); only rank-1 array proxies are supported");
                return (null, null, arg);
            }

            var baseExpr = _owner.InjectSlotValues(Splice(elem.Expression));
            var indexText = string.Join(", ", elem.ArgumentList.Arguments
                .Select(a => _owner.InjectSlotValues(Splice(a.Expression)).ToString()));
            var target = $"{baseExpr}[{indexText}]";
            var slot = Temp(isOut ? "__out" : "__ref");

            if (arrayType == null)
            {
                // non-array indexer (Span etc.) → copy-in/copy-out slot
                var ctor = isOut
                    ? $"ByRef<{paramType}>.Create()"
                    : $"ByRef<{paramType}>.Create({target})";
                return (ParseStmt($"ByRef<{paramType}> {slot} = {ctor};"),
                    ParseStmt($"{target} = {slot}.Value;"),
                    SyntaxFactory.Argument(Identifier(slot)));
            }

            var innerType = _owner.TypeDisplay(arrayType.ElementType);
            var readback = isOut ? ParseStmt($"{target} = {slot}.Value;") : null;
            _owner.Stats.ArrayElementProxies++;
            return (ParseStmt($"ByRef<{innerType}> {slot} = ByRef<{innerType}>.Create({baseExpr}, {indexText});"),
                readback, SyntaxFactory.Argument(Identifier(slot)));
        }

        // (6) plain identifier or member chain
        if (expr is IdentifierNameSyntax || expr is MemberAccessExpressionSyntax)
        {
            var target = _owner.InjectSlotValues(Splice(expr)).ToString();

            if (isOut)
            {
                // Slot + read-back: never captures the variable, so unassigned
                // locals stay legal (no CS0165 through the getter closure).
                var slot = Temp("__out");
                return (ParseStmt($"ByRef<{paramType}> {slot} = ByRef<{paramType}>.Create();"),
                    ParseStmt($"{target} = {slot}.Value;"),
                    SyntaxFactory.Argument(Identifier(slot)));
            }

            if (ContainsRefStructCapture(expr))
            {
                var slot = Temp("__ref");
                return (ParseStmt($"ByRef<{paramType}> {slot} = ByRef<{paramType}>.Create({target});"),
                    ParseStmt($"{target} = {slot}.Value;"),
                    SyntaxFactory.Argument(Identifier(slot)));
            }

            var slot2 = Temp("__ref");
            _owner.Stats.AccessorProxies++;
            return (ParseStmt($"ByRef<{paramType}> {slot2} = ByRef<{paramType}>.Create(() => {target}, v => {target} = v);"),
                null, SyntaxFactory.Argument(Identifier(slot2)));
        }

        // (7) anything else — fail fast with a readable diagnostic
        Report3303(arg.Expression, "unsupported ref/out argument shape");
        return (null, null, arg);
    }

    internal bool ContainsRefStructCapture(ExpressionSyntax expr)
    {
        foreach (var id in expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var sym = _owner.GetSymbol(id);
            ITypeSymbol? t = sym switch
            {
                ILocalSymbol l => l.Type,
                IParameterSymbol p => p.Type,
                IFieldSymbol f => f.Type,
                _ => null,
            };
            if (t != null && t.IsRefLikeType) return true;
        }
        return false;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    internal static IEnumerable<InvocationExpressionSyntax> InvocationsOutsideLambdas(ExpressionSyntax expr)
    {
        foreach (var inv in expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            bool insideLambda = inv.Ancestors()
                .TakeWhile(a => !ReferenceEquals(a, expr))
                .Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
            if (!insideLambda) yield return inv;
        }
    }

    private bool IsLiftable(InvocationExpressionSyntax inv)
        => TryGetLiftMethods(inv, out _);

    /// <summary>
    /// Cached candidate resolution; a non-empty list means the call carries at
    /// least one ref/out argument and every candidate will be rewritten.
    /// </summary>
    private bool TryGetLiftMethods(
        InvocationExpressionSyntax inv,
        out IReadOnlyList<IMethodSymbol> methods)
    {
        if (_liftableCache.TryGetValue(inv, out var cached))
        {
            methods = cached!;
            return cached != null;
        }

        IReadOnlyList<IMethodSymbol>? resolved = null;
        if (_owner.TryGetLiftMethods(inv, out var candidates))
        {
            foreach (var arg in inv.ArgumentList.Arguments)
            {
                if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                {
                    resolved = candidates;
                    break;
                }
            }
        }

        _liftableCache[inv] = resolved;
        methods = resolved!;
        return resolved != null;
    }

    private void PushScope()
    {
        _scope.Push(_out);
        _out = new List<StatementSyntax>();
    }

    private BlockSyntax PopScope()
    {
        var inner = _out;
        _out = _scope.Pop();
        return SyntaxFactory.Block(inner);
    }

    private static ExpressionSyntax Identifier(string name) => SyntaxFactory.IdentifierName(name);

    private static ExpressionSyntax Negate(ExpressionSyntax e) =>
        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
            SyntaxFactory.ParenthesizedExpression(e));

    private static StatementSyntax ParseStmt(string text) => SyntaxFactory.ParseStatement(text);

    private string Temp(string prefix) => $"{prefix}{_owner.NextTemp()}";

    private string? TypeNameOf(ExpressionSyntax expr)
    {
        var t = _owner.GetTypeOf(expr);
        return t == null ? null : _owner.TypeDisplay(t);
    }

    internal void Report3303(ExpressionSyntax node, string reason) =>
        _owner.Report3303(node, reason);
}
