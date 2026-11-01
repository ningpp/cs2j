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
    /// <summary>
    /// Marks the temp identifier returned in place of a lifted NON-void call
    /// whose result was already materialized as <c>var __callN = …;</c> in the
    /// hoisted statements. A void-delegate lambda body that discarded the
    /// result must NOT emit <c>__callN;</c> as a statement (CS0201).
    /// </summary>
    internal static readonly SyntaxAnnotation CallResultTempAnnotation = new("Cs2jLiftedCallTemp");
    /// <summary>
    /// Marks the copy-back statement of a <c>ref</c> (non-<c>out</c>) argument.
    /// Such a write-back must also run when the callee throws: the callee may
    /// have mutated the aliased value before throwing (a ref-struct holder
    /// swap, a struct member write). <c>out</c> read-backs stay on the normal
    /// completion path only — the C# out contract is "assigned iff the call
    /// returns".
    /// </summary>
    internal static readonly SyntaxAnnotation ExceptionSyncReadbackAnnotation = new("Cs2jExceptionSyncReadback");
    /// <summary>
    /// Marks the placeholder expression returned for a void lifted call whose
    /// call statement (plus its try/finally read-backs) was already emitted as
    /// a hoisted statement. Callers must not emit it as a statement.
    /// </summary>
    internal static readonly SyntaxAnnotation StatementOnlyExpressionAnnotation = new("Cs2jStatementOnlyExpression");

    private readonly RefOutInSyntaxRewriter _owner;
    private List<StatementSyntax> _out = new();
    private List<StatementSyntax> _rootOut = new();
    private Stack<List<StatementSyntax>> _scope = new();
    private List<StatementSyntax> _pendingReadbacks = new();

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

    /// <summary>
    /// Saves and resets the per-statement splice scratch state while the owner
    /// rewrites a lambda retained inside a call being lifted. The owner's
    /// statement visitors re-enter THIS lifter for every statement in the
    /// lambda body (each calls <see cref="SpliceExpression"/>, which resets the
    /// scratch lists); without isolation that reset would discard the outer
    /// invocation's already-emitted slot declarations (CS0103 on
    /// <c>__refN</c>). Fragments produced inside the lambda belong to the
    /// lambda's own statement replacements and are discarded here on leave.
    /// </summary>
    // Fragment isolation is properly nested (a lambda reached via the splicer
    // may itself contain the expression-lambda conversion, etc.), so the save
    // slots are stacks rather than single fields.
    private readonly Stack<List<StatementSyntax>> _savedOut = new();
    private readonly Stack<List<StatementSyntax>> _savedRootOut = new();
    private readonly Stack<Stack<List<StatementSyntax>>> _savedScope = new();
    private readonly Stack<List<StatementSyntax>> _savedPendingReadbacks = new();
    private int _fragmentLambdaDepth;

    internal void EnterFragmentLambdaVisit()
    {
        _savedOut.Push(_out);
        _savedRootOut.Push(_rootOut);
        _savedScope.Push(_scope);
        _savedPendingReadbacks.Push(_pendingReadbacks);
        _out = _rootOut = new List<StatementSyntax>();
        _scope = new Stack<List<StatementSyntax>>();
        _pendingReadbacks = new List<StatementSyntax>();
        _fragmentLambdaDepth++;
    }

    internal void LeaveFragmentLambdaVisit()
    {
        _out = _savedOut.Pop();
        _rootOut = _savedRootOut.Pop();
        _scope = _savedScope.Pop();
        _pendingReadbacks = _savedPendingReadbacks.Pop();
        _fragmentLambdaDepth--;
    }

    /// <summary>Read-backs for void calls, to be flushed after the call statement.</summary>
    public List<StatementSyntax> TakePendingReadbacks()
    {
        var pending = new List<StatementSyntax>(_pendingReadbacks);
        _pendingReadbacks.Clear();
        return pending;
    }

    /// <summary>
    /// True when <paramref name="expr"/> is the placeholder of a void call
    /// already emitted into the hoisted statements.
    /// </summary>
    internal static bool IsStatementOnlyExpression(ExpressionSyntax expr)
        => expr.HasAnnotation(StatementOnlyExpressionAnnotation);

    public bool ContainsLiftable(ExpressionSyntax expr)
    {
        if (expr == null) return false;

        // Inside a fragment-lambda visit (a lambda body being rewritten), the
        // owning lambda is the scan boundary itself — only NESTED lambdas/local
        // functions are barriers; InvocationsOutsideLambdas would additionally
        // treat the owner as a barrier and hide the body's own calls.
        IEnumerable<InvocationExpressionSyntax> invocations = _fragmentLambdaDepth > 0
            ? expr.DescendantNodesAndSelf(
                    n => n is not AnonymousFunctionExpressionSyntax
                         and not LocalFunctionStatementSyntax)
                .OfType<InvocationExpressionSyntax>()
            : InvocationsOutsideLambdas(expr);

        foreach (var inv in invocations)
        {
            if (IsLiftable(inv)) return true;
        }

        return false;
    }

    // ── expression splicing ─────────────────────────────────────────────────

    /// <summary>
    /// Splice a by-value (or in-dropped) argument expression and then run the
    /// children splicer over the remainder once more. <see cref="Splice"/>
    /// short-circuits subtrees that contain no liftable calls, so a top-level
    /// delegate argument (e.g. <c>(ref S s, int m) => …</c>) is returned
    /// untouched and its lambdas would bypass the C5 rewrite entirely; this
    /// second pass converts them while they are still bound to the original
    /// model.
    /// </summary>
    private ExpressionSyntax SpliceArgument(ExpressionSyntax expr)
    {
        var spliced = Splice(expr);
        return (ExpressionSyntax)new ChildrenSplicer(this).Visit(spliced)!;
    }

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
        var rightValue = Inject(Splice(bin.Right));
        _out.Add(ParseStmt($"{temp} = {rightValue};"));
        var conditional = PopScope();

        // The gate may be an already-spliced partial expression that reuses
        // original nodes (e.g. `Liftable(...) && plainHolder + c`): it lands in
        // a generated statement and is never re-visited, so its holder reads
        // must be finalized here (CS0019 otherwise).
        var gate = Inject(isAnd ? left : Negate(left));
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
        _out.Add(ParseStmt($"{typeName} {temp} = {Inject(left)};"));
        PushScope();
        var rightValue = Inject(Splice(bin.Right));
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

        var condition = Inject(Splice(cond.Condition));
        var typeName = TypeNameOf(cond) ?? "object";
        var temp = Temp("__cond");
        var typeSymbol = _owner.GetTypeOf(cond);
        var defaultLit = typeSymbol is { IsValueType: true } ? "default" : "default!";
        _out.Add(ParseStmt($"{typeName} {temp} = {defaultLit};"));

        PushScope();
        _out.Add(BranchAssignment(temp, Inject(Splice(cond.WhenTrue))));
        var trueBlock = PopScope();

        PushScope();
        _out.Add(BranchAssignment(temp, Inject(Splice(cond.WhenFalse))));
        var falseBlock = PopScope();

        _out.Add(SyntaxFactory.IfStatement(condition, trueBlock, SyntaxFactory.ElseClause(falseBlock)));
        return Identifier(temp);
    }

    private ExpressionSyntax SpliceChildren(ExpressionSyntax expr)
    {
        // Common case: no liftable call anywhere below (cached bindings make
        // this a single structural walk).
        if (!ContainsLiftable(expr)) return expr;

        // Single top-down linear pass (the old implementation scanned all
        // descendants, replaced one outermost target and re-scanned the whole
        // expression — O(L*n^2) per statement, which never terminated on the
        // generated serializer methods). The walker hands each target node to
        // the same dedicated Splice handlers, so evaluation order, scoped
        // short-circuit hoists and read-backs are identical.
        return (ExpressionSyntax)new ChildrenSplicer(this).Visit(expr)!;
    }

    /// <summary>
    /// Top-down expression splicer: target nodes (liftable invocations and the
    /// short-circuit/coalesce/conditional expressions enclosing one) are
    /// handed to the owning lifter's handlers, which themselves drive their
    /// own subtrees; plain nodes are descended in syntax order. Descent stops
    /// at lambdas/local functions (their calls are never statement-lifted).
    /// </summary>
    private sealed class ChildrenSplicer : CSharpSyntaxRewriter
    {
        private readonly RefOutInStatementLifter _lifter;

        public ChildrenSplicer(RefOutInStatementLifter lifter) => _lifter = lifter;

        // Lambdas are never statement-lifted, but they still must go through
        // the owner rewrite (C5): the enclosing statement is rebuilt from
        // fragments and never traversed again, so a held `ref` delegate
        // parameter would otherwise survive verbatim (CS1677) and captured
        // outer holders would miss their .Value injection.
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => _lifter._owner.VisitLambdaFragment(node);
        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => _lifter._owner.VisitLambdaFragment(node);
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
            => _lifter._owner.VisitLambdaFragment(node);

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            => _lifter.IsLiftable(node)
                ? _lifter.LiftInvocation(node)
                : base.VisitInvocationExpression(node);

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (!_lifter.ContainsLiftable(node))
                return base.VisitBinaryExpression(node);

            return node.Kind() switch
            {
                SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
                    => _lifter.SpliceShortCircuit(node),
                SyntaxKind.CoalesceExpression
                    => _lifter.SpliceCoalesce(node),
                _ => base.VisitBinaryExpression(node),
            };
        }

        public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
            => _lifter.ContainsLiftable(node)
                ? _lifter.SpliceConditional(node)
                : base.VisitConditionalExpression(node);
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
        ExpressionSyntax? staticCallee = null;

        // Reduced extension invocation of a converted `this ref` parameter
        // (`reader.GetSpan()`): the receiver is an IMPLICIT argument 0 (ref),
        // not part of the ArgumentList, so the explicit loop below never sees
        // it and the main rewriter would inject `reader.Value` as the
        // receiver — CS1929 against the rewritten `RefStructByRef<T>`/ByRef<T>
        // signature. Lift the receiver exactly like an explicit ref argument
        // and rewrite the call to its static form `T.M(receiver, ...)`.
        if (IsReducedExtensionOnLiftedReceiver(invocation, method)
            && invocation.Expression is MemberAccessExpressionSyntax receiverAccess)
        {
            var unreduced = method.ReducedFrom!;
            var liftedReceiver = LiftArgument(
                unreduced, 0, null, receiverAccess.Expression, isOut: false);
            if (liftedReceiver.decl != null) _out.Add(liftedReceiver.decl);
            // Read-backs are reversed before emission, so the receiver — the
            // first-evaluated (implicit) argument — is written back last.
            if (liftedReceiver.readback != null)
                readbacks.Insert(0, MarkExceptionSyncReadback(liftedReceiver.readback, isOut: false));
            newArgs.Add(ByRefHolderArgument(liftedReceiver.arg, unreduced, 0));
            anyLifted = true;

            // Static target is generated syntax: it must never be run through
            // holder slot injection.
            staticCallee = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseExpression(unreduced.ContainingType.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat)),
                receiverAccess.Name);
        }

        for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var arg = invocation.ArgumentList.Arguments[i];
            if (arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword))
            {
                // CD8: kept-in methods keep the keyword; stripped methods drop it.
                if (_owner.IsInKept(method, i)) { newArgs.Add(arg); continue; }
                newArgs.Add(SyntaxFactory.Argument(_owner.InjectSlotValues(SpliceArgument(arg.Expression))));
                continue;
            }
            if (!arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                && !arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                newArgs.Add(SyntaxFactory.Argument(_owner.InjectSlotValues(SpliceArgument(arg.Expression))));
                continue;
            }

            bool isOut = arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);

            // Pointer/function-pointer parameters have no ByRef<T> form
            // (CS0306); the callee keeps its native ref/out signature, so the
            // argument — keyword included — is forwarded unchanged.
            if (RefOutInAliasAnnotator.IsPointerType(method.Parameters[i].Type))
            {
                newArgs.Add(arg);
                continue;
            }

            // Ref-like (ref struct) parameters also keep their native form
            // (see RefOutInAliasAnnotator.ClassifyMethod): no holder exists for
            // them, so the argument stays `ref`/`out value` untouched.
            if (method.Parameters[i].Type.IsRefLikeType)
            {
                newArgs.Add(arg);
                continue;
            }

            var lifted = LiftArgument(method, i, arg, isOut);
            if (lifted.decl != null) _out.Add(lifted.decl);
            if (lifted.readback != null)
                readbacks.Add(MarkExceptionSyncReadback(lifted.readback, isOut));
            newArgs.Add(ByRefHolderArgument(lifted.arg, method, i));
            anyLifted = true;
        }

        if (!anyLifted)
            return invocation;

        _owner.Stats.CallSitesLifted++;
        _owner.MarkHolderUsed();
        // The callee expression is retained verbatim, but the whole lifted
        // call is embedded in a generated statement that is never re-visited.
        // A holder read in the target (element-access receiver AND index, or a
        // member chain through a holder) must therefore be substituted here —
        // e.g. `pageNode[idxNode].GetSibling(out page)` (CS0021).
        var newInvocation = invocation
            .WithExpression(staticCallee ?? Inject(invocation.Expression))
            .WithArgumentList(
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));

        readbacks.Reverse();
        if (method.ReturnsVoid)
        {
            // A ref (non-out) copy-back must still run when the callee throws:
            // the callee may have mutated the aliased value before throwing
            // (e.g. MetadataDb.Enlarge swapping _data). The call statement and
            // the out read-backs stay in try, the ref read-backs go to finally;
            // the whole shape is emitted here and the placeholder must not be
            // emitted as a statement by the caller.
            if (ContainsRefReadback(readbacks))
            {
                _out.Add(BuildExceptionSafeCall(
                    SyntaxFactory.ExpressionStatement(_owner.InjectSlotValues(newInvocation)),
                    readbacks));
                return Identifier("__statementOnly")
                    .WithAdditionalAnnotations(StatementOnlyExpressionAnnotation);
            }
            // The call statement itself is assembled by the caller (expression
            // statement / arrow body); read-backs must follow it, so they go to
            // the pending buffer.
            _pendingReadbacks.AddRange(readbacks);
            return newInvocation;
        }

        var temp = Temp("__call");
        // A ref-like (span) result is excluded: escape analysis only grants it
        // caller-context safe-to-escape when the local is declared WITH the
        // call as its initializer, so a "declare then assign in try" shape is
        // CS9077/CS8347. Such calls keep the plain declaration and copy the
        // read-backs after it (the callee's own finally still syncs the shadow).
        if (ContainsRefReadback(readbacks) && !method.ReturnType.IsRefLikeType)
        {
            // Same exception-safety shape for a value-returning call: declare
            // the result temp OUTSIDE the try (definite assignment), assign it
            // and run the out read-backs in try, run the ref read-backs in
            // finally. The original consumer statement follows the temp.
            var tempType = method.RefKind is RefKind.Ref or RefKind.RefReadOnly
                && _owner.IsInternalCandidate(method)
                ? $"ByRef<{_owner.TypeDisplay(method.ReturnType)}>"   // rewritten internal ref return
                : _owner.TypeDisplay(method.ReturnType);              // native/external ref T (copied)
            var defaultLiteral = method.ReturnType.IsValueType ? "default" : "default!";
            _out.Add(ParseStmt($"{tempType} {temp} = {defaultLiteral};"));
            _out.Add(BuildExceptionSafeCall(
                SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    Identifier(temp),
                    newInvocation)),
                readbacks));
            return Identifier(temp).WithAdditionalAnnotations(CallResultTempAnnotation);
        }

        // Build the declaration from syntax nodes instead of interpolating and
        // re-parsing: newInvocation REUSES original argument nodes (including
        // delegate lambdas), and a re-parse would detach them from the original
        // syntax tree so the splicer's C5 lambda rewrite could no longer bind
        // their parameters (the `ref` parameter survives → CS1677).
        _out.Add(SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName(SyntaxFactory.Identifier("var")),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(temp))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(newInvocation))))));
        _out.AddRange(readbacks);
        return Identifier(temp).WithAdditionalAnnotations(CallResultTempAnnotation);
    }

    /// <summary>Tags a <c>ref</c> copy-back so the emission splits it into finally.</summary>
    private static StatementSyntax MarkExceptionSyncReadback(StatementSyntax readback, bool isOut)
        => isOut ? readback : readback.WithAdditionalAnnotations(ExceptionSyncReadbackAnnotation);

    private static bool ContainsRefReadback(IReadOnlyList<StatementSyntax> readbacks)
        => readbacks.Any(r => r.HasAnnotation(ExceptionSyncReadbackAnnotation));

    /// <summary>
    /// <c>try { callStatement; out-readbacks } finally { ref-readbacks }</c>
    /// (no catch: the callee exception still propagates).
    /// </summary>
    private static TryStatementSyntax BuildExceptionSafeCall(
        StatementSyntax callStatement, IReadOnlyList<StatementSyntax> readbacks)
    {
        var tryStatements = new List<StatementSyntax> { callStatement };
        tryStatements.AddRange(readbacks.Where(r => !r.HasAnnotation(ExceptionSyncReadbackAnnotation)));
        return SyntaxFactory.TryStatement(
            SyntaxFactory.Block(tryStatements),
            SyntaxFactory.List<CatchClauseSyntax>(),
            SyntaxFactory.FinallyClause(
                SyntaxFactory.Block(readbacks.Where(r => r.HasAnnotation(ExceptionSyncReadbackAnnotation)))));
    }

    private (StatementSyntax? decl, StatementSyntax? readback, ArgumentSyntax arg) LiftArgument(
        IMethodSymbol method, int index, ArgumentSyntax arg, bool isOut)
        => LiftArgument(method, index, arg, arg.Expression, isOut);

    /// <summary>
    /// Adds the `ref` keyword to a lifted argument whose parameter is a
    /// ref-struct holder. The declaration pass keeps ref-struct holders as
    /// <c>ref Cs2jByRef.RefStructByRef&lt;T&gt;</c> (see
    /// <c>RefOutInSyntaxRewriter.RewriteParameter</c>): the holder is itself a
    /// ref struct, so a by-value parameter would copy its slot and swallow every
    /// write the callee performs. Only INTERNAL (rewritten) declarations qualify
    /// — a native/external callee keeps its own <c>ref</c>/<c>out</c> handling,
    /// which the individual lift branches already emit.
    /// </summary>
    private ArgumentSyntax ByRefHolderArgument(
        ArgumentSyntax argument, IMethodSymbol method, int index)
    {
        if (index >= method.Parameters.Length)
            return argument;
        var parameter = method.Parameters[index];
        if (!parameter.Type.IsRefLikeType || !_owner.IsInternalCandidate(method))
            return argument;
        // The lifted argument is synthesized (no keyword) or the original one
        // (still carrying ref/out); a holder binding is always `ref`.
        return argument.WithRefKindKeyword(
            SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space));
    }

    /// <param name="arg">
    /// The original argument node at this position (its <c>ref</c>/<c>out</c>
    /// keyword is preserved when the parameter keeps its native form); null for
    /// the implicit receiver of a reduced extension invocation.
    /// </param>
    /// <param name="expression">
    /// The lifted expression, passed separately from <paramref name="arg"/>:
    /// wrapping an original node in a synthesized ArgumentSyntax RE-PARENTS it
    /// (its SyntaxTree stops being the semantic tree), which makes every
    /// semantic lookup on it throw — the reduced-extension receiver must stay
    /// bound while the holder/ref-struct shapes are classified.
    /// </param>
    private (StatementSyntax? decl, StatementSyntax? readback, ArgumentSyntax arg) LiftArgument(
        IMethodSymbol method, int index, ArgumentSyntax? arg, ExpressionSyntax expression, bool isOut)
    {
        var expr = expression;
        // 形参侧的 ref/out 站点常带括号或 null-forgiving 后缀（SDK
        // JsonReaderHelper.Unescaping.cs: `out bytes!`）。逐步剥掉这两层纯
        // 静态包装，否则会落到 (7) 的 unsupported shape 并让整个 pass 失败
        // （CS2J3303）。
        while (true)
        {
            if (expr is ParenthesizedExpressionSyntax p)
            {
                expr = p.Expression;
                continue;
            }
            if (expr is PostfixUnaryExpressionSyntax s
                && s.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                expr = s.Operand;
                continue;
            }
            break;
        }
        var paramTypeSymbol = method.Parameters[index].Type;
        var paramType = _owner.TypeDisplay(paramTypeSymbol);

        // (0) native callee (external metadata / locked source): the parameter
        // keeps its `ref`/`out T` signature, so a ByRef holder cannot be
        // forwarded — `holder.Value` is a property (CS0206). Copy the holder
        // into a temp local at the argument position, pass the temp with its
        // native keyword, and write it back after the call (read-backs run in
        // reverse argument order, like every other lifted call).
        if (!_owner.IsInternalCandidate(method)
            && expr is IdentifierNameSyntax nativeHolder
            && _owner.IsHolderName(nativeHolder.Identifier.Text)
            && !_owner.TryGetShadowName(nativeHolder.Identifier.Text, out _))
        {
            var holderType = _owner.GetTypeOf(expr) ?? paramTypeSymbol;
            var holderTypeName = _owner.TypeDisplay(holderType);
            var holderText = nativeHolder.Identifier.Text;
            var temp = Temp(isOut ? "__out" : "__ref");
            return (ParseStmt(isOut
                    ? $"{holderTypeName} {temp};"
                    : $"{holderTypeName} {temp} = {holderText}.Value;"),
                ParseStmt($"{holderText}.Value = {temp};"),
                arg != null
                    ? arg.WithExpression(Identifier(temp))
                    : SyntaxFactory.Argument(Identifier(temp)));
        }

        // (1) holder slot direct pass — internal callee now expects ByRef<T>/RefStructByRef<T>.
        // A value-typed holder inside a shadow copy-in/copy-out frame is NOT
        // forwarded directly: that would alias the outer ByRef and bypass (and
        // get clobbered by) the shadow copy-out. Fall through to the accessor
        // shape, which aliases the shadow local instead.
        if (expr is IdentifierNameSyntax slotId
            && _owner.IsHolderName(slotId.Identifier.Text)
            && !_owner.TryGetShadowName(slotId.Identifier.Text, out _))
        {
            var id = SyntaxFactory.IdentifierName(slotId.Identifier.Text)
                .WithAdditionalAnnotations(DirectPassAnnotation);
            return (null, null, SyntaxFactory.Argument(id));
        }

        // (2) discard: out _
        if (expr is IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            var slot = Temp("__out");
            var (slotType, slotCreate) = OutSlot(paramTypeSymbol, paramType);
            return (ParseStmt($"{slotType} {slot} = {slotCreate};"),
                null, SyntaxFactory.Argument(Identifier(slot)));
        }

        // (2b) typed/var discard declaration: `out T _` / `out var _`. Roslyn
        // represents these as a DeclarationExpression carrying a
        // DiscardDesignation, so case (3)'s SingleVariableDesignation test
        // misses them and the native `out T _` would survive against the
        // rewritten ByRef<T> parameter (CS1615). Nothing is written back.
        if (expr is DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax })
        {
            var discardSlot = Temp("__out");
            var (discardSlotType, discardSlotCreate) = OutSlot(paramTypeSymbol, paramType);
            return (ParseStmt($"{discardSlotType} {discardSlot} = {discardSlotCreate};"),
                null, SyntaxFactory.Argument(Identifier(discardSlot)));
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
            var (slotType, slotCreate) = OutSlot(paramTypeSymbol, paramType);
            // A ref struct target is assigned straight from the slot: the
            // ReadbackX helpers only exist for primitive ByRef<T> slots.
            var readback = ParseStmt(paramTypeSymbol.IsRefLikeType
                ? $"{varName} = {slot}.Value;"
                : ReadbackStatement(varName, slot, paramTypeSymbol));
            return (ParseStmt($"{slotType} {slot} = {slotCreate};"),
                readback, SyntaxFactory.Argument(Identifier(slot)));
        }

        // (4) ref struct typed argument → RefStructByRef slot (copy-in/copy-out;
        //     closures cannot capture ref structs, CS8175)
        var argType = _owner.GetTypeOf(expr);
        if (argType != null && argType.IsRefLikeType)
        {
            // A SHADOWED ref-struct holder parameter takes the direct-pass
            // shape instead of a fresh slot: a `RefStructByRef<T>` local would
            // give every value derived from it method-scope safe-to-escape, and
            // a span-returning callee (JsonHelpers.GetSpan →
            // `return JsonReaderHelper.GetUnescapedSpan(propertyName, idx)`)
            // then fails CS8352/CS8347. Flush the shadow into the holder slot,
            // pass the holder itself by reference (fix A), and re-read it.
            if (expr is IdentifierNameSyntax shadowed
                && _owner.GetSymbol(expr) is IParameterSymbol
                && _owner.TryGetShadowName(shadowed.Identifier.Text, out var shadowName))
            {
                var holderName = shadowed.Identifier.Text;
                var direct = SyntaxFactory.IdentifierName(holderName)
                    .WithAdditionalAnnotations(DirectPassAnnotation);
                return (ParseStmt($"{holderName}.Value = {shadowName};"),
                    ParseStmt($"{shadowName} = {holderName}.Value;"),
                    SyntaxFactory.Argument(direct));
            }

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

        // (4c) ref-returning invocation (SDK json
        // `IndexOfOrLessThan(ref MemoryMarshal.GetReference(span), ...)`):
        // there is no storable local for the byref result, so seed a
        // copy-in/copy-out slot from the call and write the slot value back
        // through the same call. Ref-returning invocations are assignable
        // variables, so the read-back is legal C#; a ref-readonly result only
        // needs the copy-in.
        if (expr is InvocationExpressionSyntax refInv
            && useSym is IMethodSymbol refMethod
            && (refMethod.RefKind == RefKind.Ref || refMethod.RefKind == RefKind.RefReadOnly))
        {
            var target = _owner.InjectSlotValues(Splice(refInv)).ToString();
            var slot = Temp(isOut ? "__out" : "__ref");
            var ctor = isOut
                ? $"ByRef<{paramType}>.Create()"
                : $"ByRef<{paramType}>.Create({target})";
            var readback = refMethod.RefKind == RefKind.RefReadOnly
                ? null
                : ParseStmt(ReadbackStatement(target, slot, paramTypeSymbol));
            return (ParseStmt($"ByRef<{paramType}> {slot} = {ctor};"),
                readback, SyntaxFactory.Argument(Identifier(slot)));
        }

        // (5) element access
        if (expr is ElementAccessExpressionSyntax elem)
        {
            var arrayType = _owner.GetTypeOf(elem.Expression) as IArrayTypeSymbol;
            if (arrayType != null && arrayType.Rank != 1)
            {
                Report3303(expression,
                    $"multi-dimensional array element ref (rank {arrayType.Rank}); only rank-1 array proxies are supported");
                return (null, null, arg ?? SyntaxFactory.Argument(expression));
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
                    ParseStmt(isOut
                        ? ReadbackStatement(target, slot, paramTypeSymbol)
                        : $"{target} = {slot}.Value;"),
                    SyntaxFactory.Argument(Identifier(slot)));
            }

            var innerType = _owner.TypeDisplay(arrayType.ElementType);
            var readback = isOut
                ? ParseStmt(ReadbackStatement(target, slot, arrayType.ElementType))
                : null;
            _owner.Stats.ArrayElementProxies++;
            return (ParseStmt($"ByRef<{innerType}> {slot} = ByRef<{innerType}>.Create({baseExpr}, {indexText});"),
                readback, SyntaxFactory.Argument(Identifier(slot)));
        }

        // (6) plain identifier or member chain
        if (expr is IdentifierNameSyntax || expr is MemberAccessExpressionSyntax)
        {
            var target = _owner.InjectSlotValues(Splice(expr)).ToString();
            var targetType = _owner.GetTypeOf(expr) ?? paramTypeSymbol;

            // Slot + copy-back, never a getter/setter closure. The slot is an
            // ordinary local, so the rewritten tree holds no lambda over a
            // mutable capture and maps 1:1 to Java
            // (`ByRef.create(x)` … `x = readback(slot)`). The old accessor
            // closure `Create(() => x, v => x = v)` aliased the target live,
            // but a Java lambda cannot assign a captured local; that alias only
            // worked when closure-lift rewrote the capture, which it does not
            // do for C01 scaffolding. A synchronous one-shot call reaches the
            // target exclusively through the slot, so the alias is unobservable
            // here (ref locals, ref returns and ref foreach elements keep the
            // accessor shape — they alias across statements and have no
            // copy-back point).
            // `out` omits the seed so unassigned locals stay legal (CS0165).
            var slot = Temp(isOut ? "__out" : "__ref");
            var ctor = isOut
                ? $"ByRef<{paramType}>.Create()"
                : $"ByRef<{paramType}>.Create({target})";
            return (ParseStmt($"ByRef<{paramType}> {slot} = {ctor};"),
                ParseStmt(ReadbackStatement(target, slot, targetType)),
                SyntaxFactory.Argument(Identifier(slot)));
        }

        // (7) anything else — fail fast with a readable diagnostic
        Report3303(expression, "unsupported ref/out argument shape");
        return (null, null, arg ?? SyntaxFactory.Argument(expression));
    }

    /// <summary>
    /// Out-slot type for a declaration/discard argument (<c>out T x</c>,
    /// <c>out var x</c>, <c>out _</c>). A ref-struct parameter type
    /// (<c>Utf8JsonReader</c>) cannot be a <c>ByRef&lt;T&gt;</c> type argument
    /// (CS9244: <c>ByRef&lt;T&gt;</c> has no <c>allows ref struct</c>), and the
    /// declaration pass already converted that parameter to
    /// <c>RefStructByRef&lt;T&gt;</c>, so the slot must use the same holder or the
    /// call fails with CS1503. Non-ref-struct parameters keep <c>ByRef&lt;T&gt;</c>.
    /// </summary>
    private static (string Type, string Create) OutSlot(ITypeSymbol paramTypeSymbol, string paramType)
        => paramTypeSymbol.IsRefLikeType
            ? ($"Cs2jByRef.RefStructByRef<{paramType}>", $"RefStructByRef<{paramType}>.Create()")
            : ($"ByRef<{paramType}>", $"ByRef<{paramType}>.Create()");

    /// <summary>
    /// Java primitive mapped from a C# value type, keyed by the converter's
    /// primitive widening rules (byte→int, uint→int, ushort→short, ulong→long …).
    /// The result type is the helper's declared return type — deliberately the
    /// Java-friendly primitive, so it can differ from the C# target type and
    /// the copy-back needs an explicit conversion (CS0266).
    /// Null when the type has no null→default readback (reference types, enums,
    /// custom structs, decimal, IntPtr, nullable — null is legitimate there).
    /// </summary>
    private static readonly Dictionary<SpecialType, (string Helper, SpecialType Result)> ReadbackHelperBySpecialType = new()
    {
        [SpecialType.System_Int32] = ("ReadbackInt", SpecialType.System_Int32),
        [SpecialType.System_UInt32] = ("ReadbackInt", SpecialType.System_Int32),     // uint → Java int
        [SpecialType.System_Byte] = ("ReadbackInt", SpecialType.System_Int32),       // byte → Java int
        [SpecialType.System_Int16] = ("ReadbackShort", SpecialType.System_Int16),
        [SpecialType.System_UInt16] = ("ReadbackUShort", SpecialType.System_Int32),  // ushort → Java int 载体（ByRef<Integer>）
        [SpecialType.System_SByte] = ("ReadbackByte", SpecialType.System_Byte),
        [SpecialType.System_Int64] = ("ReadbackLong", SpecialType.System_Int64),
        [SpecialType.System_UInt64] = ("ReadbackLong", SpecialType.System_Int64),    // ulong → Java long
        [SpecialType.System_Char] = ("ReadbackChar", SpecialType.System_Char),
        [SpecialType.System_Boolean] = ("ReadbackBool", SpecialType.System_Boolean),
        [SpecialType.System_Single] = ("ReadbackFloat", SpecialType.System_Single),
        [SpecialType.System_Double] = ("ReadbackDouble", SpecialType.System_Double),
    };

    /// <summary>
    /// Copy-back statement for a lifted <c>out</c> argument. Value-type targets
    /// route through <c>ByRef&lt;T&gt;.ReadbackX(slot)</c>: the C# out contract
    /// guarantees the callee assigns before returning, so the slot can only hold
    /// the written value or default(T) — but under Java generic erasure a
    /// generic callee's failure path (<c>value = default</c>, e.g.
    /// HashtableExtensions.TryGetValue) can only write null, and the previous
    /// unconditional <c>target = slot.Value</c> NPE'd on the unbox. The Java
    /// readbackX halves map null back to the primitive default, restoring
    /// default(T) exactly. Reference-type targets keep the plain shape (null is
    /// a legitimate C# value there).
    /// The unsigned/narrow helpers intentionally return the Java-side signed
    /// primitive (uint/byte → int, ushort → short, sbyte → byte, ulong → long);
    /// when that differs from the C# destination an explicit conversion is
    /// emitted so the intermediate stage still compiles (CS0266), while the
    /// helper's Java-friendly return type is preserved for the Java half.
    /// </summary>
    private static string ReadbackStatement(string target, string slot, ITypeSymbol targetType)
    {
        if (targetType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return $"{target} = {slot}.Value;";
        if (ReadbackHelperBySpecialType.TryGetValue(targetType.SpecialType, out var helper))
        {
            var helperCall = $"ByRef<{targetType.ToDisplayString()}>.{helper.Helper}({slot})";
            if (helper.Result != targetType.SpecialType)
                helperCall = $"({targetType.ToDisplayString()}){helperCall}";
            return $"{target} = {helperCall};";
        }
        return $"{target} = {slot}.Value;";
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
    /// True when <paramref name="inv"/> is a reduced extension invocation
    /// (<c>reader.GetSpan()</c>) whose receiver binds to the declared
    /// <c>this ref</c>/<c>this out</c> parameter that the declaration pass
    /// converted to a <c>RefStructByRef&lt;T&gt;</c>/<c>ByRef&lt;T&gt;</c>
    /// holder. Such a receiver is an implicit ref argument and must be lifted
    /// together with the call. The receiver shape is restricted to identifiers
    /// and member/element chains because the copy-out is an assignment back to
    /// that expression.
    /// <para>
    /// The holder decision is taken from the DECLARED receiver parameter's own
    /// ref kind rather than from <see cref="RefOutInAliasAnnotator.HolderSymbols"/>
    /// (which is built per FILE): an extension declared in another file
    /// (<c>JsonHelpers.GetSpan</c> in JsonHelpers.Date.cs vs the call sites in
    /// the converters) is not in this file's holder set, yet its <c>this ref</c>
    /// parameter is rewritten in the declaring file exactly by these same
    /// annotator rules (internal in-source method, non-pointer ref/out).
    /// </para>
    /// </summary>
    private bool IsReducedExtensionOnLiftedReceiver(
        InvocationExpressionSyntax inv,
        IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.ReducedExtension || method.ReducedFrom is not { } unreduced)
            return false;
        var receiverParam = unreduced.Parameters[0];
        if (receiverParam.RefKind != RefKind.Ref && receiverParam.RefKind != RefKind.Out)
            return false;
        if (RefOutInAliasAnnotator.IsPointerType(receiverParam.Type))
            return false;
        // A ref-like `this ref` receiver keeps its native form (the declaration
        // pass never wraps it), so the reduced extension call stays native.
        if (receiverParam.Type.IsRefLikeType)
            return false;
        if (!_owner.IsInternalCandidate(unreduced))
            return false;
        if (inv.Expression is not MemberAccessExpressionSyntax receiverAccess)
            return false;
        return IsLiftableReceiverShape(receiverAccess.Expression);
    }

    private static bool IsLiftableReceiverShape(ExpressionSyntax receiver)
    {
        while (true)
        {
            switch (receiver)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    receiver = parenthesized.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax suppressed
                    when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    receiver = suppressed.Operand;
                    continue;
                default:
                    return receiver is IdentifierNameSyntax
                        or MemberAccessExpressionSyntax
                        or ElementAccessExpressionSyntax;
            }
        }
    }

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
            for (var i = 0; i < inv.ArgumentList.Arguments.Count; i++)
            {
                var arg = inv.ArgumentList.Arguments[i];
                if (!arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    && !arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                {
                    continue;
                }

                // A ref-like (ref struct) ref/out parameter keeps its native
                // form: there is no holder to lift it into (CS8170/CS9050), so
                // this argument never triggers statement lifting.
                if (candidates.Count > 0
                    && i < candidates[0].Parameters.Length
                    && candidates[0].Parameters[i].Type.IsRefLikeType)
                {
                    continue;
                }

                resolved = candidates;
                break;
            }

            // A reduced extension invocation of a converted `this ref`
            // parameter carries its ref receiver outside the ArgumentList;
            // it must be lifted even with zero explicit ref/out arguments,
            // otherwise the receiver keeps the raw value and the rewritten
            // callee rejects it (CS1929).
            if (resolved == null
                && candidates.Count > 0
                && candidates.All(m => IsReducedExtensionOnLiftedReceiver(inv, m)))
            {
                resolved = candidates;
            }
        }
        else if (_owner.TryGetExternalHolderRefMethod(inv, out var nativeCallee))
        {
            // Native callee (external/locked) receiving a ByRef holder by
            // ref/out: `out holder.Value` is CS0206, so the call site is
            // lifted too — the argument becomes a temp local plus a read-back.
            resolved = new[] { nativeCallee };
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

    /// <summary>
    /// Finalizes holder reads inside an expression that is about to be embedded
    /// in a generated fragment (shadow local when the active frame has one,
    /// else <c>.Value</c>). Fragments are assembled from original nodes and are
    /// never re-visited by the main rewriter, so every splice path that copies
    /// an expression into a generated statement — short-circuit gates/branch
    /// values, coalesce operands, conditional operands, and the retained callee
    /// of a lifted invocation (its element-access receiver/index included) —
    /// must route it through here. Idempotent via ValueInjectedAnnotation.
    /// </summary>
    private ExpressionSyntax Inject(ExpressionSyntax expr) => _owner.InjectSlotValues(expr);

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
