using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBOp = Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// Storage-location shape of an increment/decrement target (design §4.2 row 1).
/// The dimension is orthogonal to <see cref="ArithmeticCore"/>.
/// </summary>
internal enum TargetKind
{
    /// <summary>Converter-direct simple target: int/long/sbyte/short/char local/parameter/field/primitive array element.</summary>
    NotOwned,

    /// <summary>Local/parameter/side-effect-free field access on an owned arithmetic core.</summary>
    SimpleDirect,

    /// <summary>
    /// Property/indexer-shaped read-modify-write member: an <see cref="IPropertySymbol"/>
    /// or a field reached through an effectful receiver (e.g. <c>GetObj()._f</c>, <c>a.B._f</c>).
    /// </summary>
    Property,
    Indexer,

    /// <summary>Array element whose element type is enum/decimal/custom/nint (decision 15; M2 owns enum + decimal, the rest error).</summary>
    ArrayElementSpecial,

    /// <summary>Property/indexer/method symbol with RefKind != None (decision 16).</summary>
    RefReturning,

    Event,
    Pointer,

    /// <summary>Null-conditional (?.) chain target (belongs to C35).</summary>
    Conditional,

    Other,
}

/// <summary>
/// Arithmetic core of an increment/decrement (design §4.2 row 2). Target-only
/// classifications (Event/Pointer/Conditional) carry DirectMap as a placeholder:
/// ownership in M1 is decided on <see cref="TargetKind"/>, never on the core.
/// </summary>
internal enum ArithmeticCore
{
    /// <summary>Built-in ±1 on the direct-map integer domains (int/long/sbyte/short/char simple targets).</summary>
    DirectMap,

    /// <summary>Narrow-domain cast core for property/indexer targets (sbyte/byte/short/ushort/char/int/long).</summary>
    BuiltinNarrow,

    /// <summary>uint/ulong/byte/ushort residual: UnsignedMath helper or narrow cast (decision 9).</summary>
    UnsignedDomain,

    EnumCast,
    EnumFromValue,
    Decimal,
    NativeInt,
    CustomOp,
    LiftedNullable,
    Dynamic,
    ExpressionTree,
}

/// <summary>
/// Liftability of a site (decision 14): standalone statements, value-consumed
/// positions inside the unconditional-execution whitelist
/// (ExpressionStatement/LocalDeclaration/Return/Throw/YieldReturn) and
/// anchor-free positions (condition/query/lambda/initializer/attribute/?./…).
/// </summary>
internal enum Liftability
{
    /// <summary>Not owned by this rule (direct map) — no lifting needed.</summary>
    NotNeeded,

    /// <summary>Prefix whose value is consumed: inline assignment expression (IT-10).</summary>
    InlinePrefix,

    /// <summary>Standalone expression statement: old value discarded.</summary>
    Standalone,

    /// <summary>Postfix value consumed inside a whitelisted unconditional statement.</summary>
    LiftablePostfix,

    /// <summary>Condition/query/lambda/initializer/attribute/?./…: v1 has no safe anchor (CS2J5339).</summary>
    NoAnchor,

    /// <summary>
    /// A discarded <c>++/--</c> that IS a for-statement incrementor: the write
    /// moves to the end of the loop body (continue-free bodies only); its value
    /// is never consumed.
    /// </summary>
    ForIncrementor,

    /// <summary>
    /// Postfix site inside a WHILE condition on an unblocked evaluation path:
    /// the loop is restructured to
    /// <c>while (true) { &lt;plans&gt;; if (!(C')) break; body; }</c> so the old
    /// value is read and the write performed once per condition evaluation in
    /// source order (continue naturally re-runs the plans and the test).
    /// </summary>
    WhileConditionPostfix,
}

/// <summary>
/// Classification of one ++/-- site: (UnaryKind, TargetKind, ArithmeticCore,
/// Checkedness, Liftability) plus the accessor/receiver details M1 needs to
/// render a single-evaluation read-modify-write sequence.
/// </summary>
internal sealed record IncrementTargetClassification(
    SyntaxKind UnaryKind,
    TargetKind Target,
    ArithmeticCore Core,
    bool IsChecked,
    Liftability Lift,
    IPropertySymbol? Property,
    /// <summary>Parentheses-unwrapped operand syntax node of the unary expression.</summary>
    ExpressionSyntax Operand,
    /// <summary>Bound member for member-access targets (property or field); null otherwise.</summary>
    ISymbol? Member,
    IMethodSymbol? GetMethod,
    IMethodSymbol? SetMethod,
    bool IsStaticMember,
    /// <summary>RefKind of a ref-returning property/indexer/method target (decision 16); None otherwise.</summary>
    RefKind RefKind = RefKind.None)
{
    public bool IsPrefix => UnaryKind is SyntaxKind.PreIncrementExpression
        or SyntaxKind.PreDecrementExpression;
}

/// <summary>
/// Read-only semantic classifier for C17 (design §4.2). Every decision comes
/// from the <see cref="SemanticModel"/> (GetSymbolInfo/GetTypeInfo/GetOperation);
/// token text is never trusted.
/// </summary>
internal sealed class IncrementTargetAnalyzer
{
    private readonly SemanticModel _model;
    private readonly NumericBoundaryOptions _options;
    private readonly NumericBoundaryAnalyzer _shared;

    public IncrementTargetAnalyzer(
        SemanticModel model,
        NumericBoundaryOptions options,
        NumericBoundaryAnalyzer shared)
    {
        _model = model;
        _options = options;
        _shared = shared;
    }

    private static readonly SyntaxKind[] IncrementKinds =
    {
        SyntaxKind.PreIncrementExpression,
        SyntaxKind.PreDecrementExpression,
        SyntaxKind.PostIncrementExpression,
        SyntaxKind.PostDecrementExpression,
    };

    /// <summary>
    /// Single-point ownership predicate (design decision 17). M1 owns:
    /// <list type="bullet">
    /// <item>Property/Indexer members on a built-in/unsigned/enum core in BOTH
    /// checked and unchecked regions (checked output is bare arithmetic that
    /// the C15 sweep then helperizes);</item>
    /// <item>enum/decimal array elements (decision 15);</item>
    /// <item>unchecked simple residuals (byte/ushort/uint/ulong and simple
    /// enums) and every simple decimal site — decimal is immune to checked
    /// context (decision 8).</item>
    /// </list>
    /// M2 additionally owns, so the planner can emit one loud diagnostic per
    /// site (Error → NB-12 whole-file rollback; ref alias is rewritten with a
    /// CS2J5340 Warning unless strict):
    /// <list type="bullet">
    /// <item>NativeInt (CS2J5333), LiftedNullable (CS2J5335), Dynamic
    /// (CS2J5336), ExpressionTree (CS2J5337) and CustomOp (CS2J5338) on every
    /// target shape;</item>
    /// <item>RefReturning property/indexer/method targets (decision 16);
    /// ref-readonly sites are owned only so planning can refuse them with
    /// CS2J5332.</item>
    /// </list>
    /// EnumFromValue is a C05 fingerprint hook only (decision 7) and is never
    /// owned until C05 lands. Event/Pointer/Conditional stay outside ownership.
    /// </summary>
    public static bool IsOwnedByRule(IncrementTargetClassification classification)
    {
        // Owned-but-unsupported cores: the planner reports the matching
        // CS2J53xx error and NB-12 rolls the file back, so the residual gate
        // never sees them.
        if (classification.Core is ArithmeticCore.NativeInt
            or ArithmeticCore.LiftedNullable
            or ArithmeticCore.Dynamic
            or ArithmeticCore.ExpressionTree
            or ArithmeticCore.CustomOp)
        {
            return true;
        }

        // C05 dual-contract fingerprint: detect-only hook (decision 7); no
        // target shape on that core is rewritten until C05 lands.
        if (classification.Core == ArithmeticCore.EnumFromValue)
        {
            return false;
        }

        switch (classification.Target)
        {
            case TargetKind.Property:
            case TargetKind.Indexer:
            case TargetKind.RefReturning:
                return true;
            case TargetKind.ArrayElementSpecial:
                return classification.Core is ArithmeticCore.EnumCast
                    or ArithmeticCore.Decimal;
            case TargetKind.SimpleDirect:
                if (classification.Core == ArithmeticCore.Decimal)
                {
                    // decimal is unaffected by checked/unchecked context.
                    return true;
                }

                // Checked simple built-in/enum sites stay with C15 (NB-8).
                return !classification.IsChecked
                    && classification.Core is ArithmeticCore.UnsignedDomain
                        or ArithmeticCore.EnumCast;
            default:
                return false;
        }
    }

    /// <summary>
    /// Classify a PrefixUnary/PostfixUnary ++/-- node; returns null for any other
    /// node or operator kind.
    /// </summary>
    public IncrementTargetClassification? Classify(ExpressionSyntax unaryNode)
    {
        var classification = ClassifyInternal(unaryNode);
        if (classification == null)
        {
            return null;
        }

        // Statement linearization (CS2J5339 eradication):
        // ① a statement-free prefix is a purely local `(target = NEW)` rewrite
        //    that splices no statements, so anchor/scope/foreign-effect gates do
        //    not apply — it is exact inside conditions, arguments, lambdas and
        //    other expression positions;
        // ② a discarded ++/-- that IS a whole for-incrementor moves its single
        //    write to the end of the loop body.
        if (classification is { IsPrefix: true, Lift: Liftability.NoAnchor }
            && IsStatementFreePrefixShape(classification))
        {
            return classification with { Lift = Liftability.InlinePrefix };
        }

        if (classification.Lift == Liftability.NoAnchor
            && IsForIncrementorSite(unaryNode))
        {
            return classification with { Lift = Liftability.ForIncrementor };
        }

        // ③ a postfix inside a while condition linearizes the whole loop: the
        //    read/write sequences run at the top of the restructured body and
        //    the site yields its old value to C'. Safe only on an unblocked
        //    evaluation path (no ternary arm, short-circuit/coalesce right side,
        //    lambda, query, initializer, … between the site and the condition).
        if (classification is { IsPrefix: false, Lift: Liftability.NoAnchor }
            && IsWhileConditionSite(unaryNode))
        {
            return classification with { Lift = Liftability.WhileConditionPostfix };
        }

        return classification;
    }

    /// <summary>
    /// True when a prefix site's expansion splices ZERO statements (no
    /// receiver/index/alias temp, no old-value read): a local/parameter/field
    /// target or a property reached through this/base/a bare identifier/static
    /// owner. The inline <c>(target = NEW)</c> reads the getter once and writes
    /// once in place, so it is exact in any expression position without an
    /// anchor.
    /// </summary>
    public static bool IsStatementFreePrefixShape(IncrementTargetClassification classification)
    {
        if (!classification.IsPrefix)
        {
            return false;
        }

        switch (classification.Target)
        {
            case TargetKind.SimpleDirect:
                return true;
            case TargetKind.Property when classification.Operand is IdentifierNameSyntax:
                return true;
            case TargetKind.Property when classification.Operand is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Expression is ThisExpressionSyntax
                    or BaseExpressionSyntax
                    or IdentifierNameSyntax:
                // Static owners and this/base/local/parameter/field receivers
                // need no receiver temp (BuildReceiver emits an Identifier ref).
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// True when the unary node IS (parens allowed) a whole incrementor
    /// expression of a for statement — its value is discarded and its write
    /// can be linearized to the end of the loop body.
    /// </summary>
    private static bool IsForIncrementorSite(ExpressionSyntax unaryNode)
    {
        return unaryNode.Parent is ForStatementSyntax forStatement
            && forStatement.Incrementors.Any(
                incrementor => IncrementTargetSyntaxFacts.UnwrapParens(incrementor) == unaryNode);
    }

    /// <summary>
    /// True when the postfix node sits inside a while statement's condition on
    /// a path that evaluates on every condition test: the nearest statement is
    /// the while, the node span is inside the condition span, and the path to
    /// the anchor crosses no blocked construct (ternary arm, short-circuit/
    /// coalesce right side, lambda/local function, query, initializer,
    /// interpolation) and no function scope.
    /// </summary>
    private static bool IsWhileConditionSite(ExpressionSyntax unaryNode)
    {
        var anchor = IncrementTargetSyntaxFacts.FindAnchorStatement(unaryNode);
        return anchor is WhileStatementSyntax whileStatement
            && whileStatement.Condition.Span.Contains(unaryNode.Span)
            && !IncrementTargetSyntaxFacts.IsBlockedPath(unaryNode, whileStatement)
            && !CrossesFunctionScope(whileStatement);
    }

    private IncrementTargetClassification? ClassifyInternal(ExpressionSyntax unaryNode)
    {
        var kind = unaryNode.Kind();
        if (Array.IndexOf(IncrementKinds, kind) < 0)
        {
            return null;
        }

        if (unaryNode is not (PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax))
        {
            return null;
        }

        var rawOperand = unaryNode is PrefixUnaryExpressionSyntax pre
            ? pre.Operand
            : ((PostfixUnaryExpressionSyntax)unaryNode).Operand;
        var operand = IncrementTargetSyntaxFacts.UnwrapParens(rawOperand);

        var operation = _model.GetOperation(unaryNode) as NBOp.IIncrementOrDecrementOperation;
        var operandType = _model.GetTypeInfo(operand).Type
            ?? _model.GetTypeInfo(operand).ConvertedType;

        var symbolInfo = _model.GetSymbolInfo(operand);
        var symbol = symbolInfo.Symbol;
        var isPrefix = kind is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression;
        var isChecked = ResolveIsChecked(unaryNode);
        var lift = ClassifyLift(unaryNode, isPrefix);

        // Dynamic binds no storage shape at all (d.P, d.M(), d[...] are all
        // dynamic), so classify the core before the symbol-shape branches —
        // every dynamic site is CS2J5336 (decision 18).
        if (operandType?.TypeKind == TypeKind.Dynamic)
        {
            return NewClassification(
                kind, TargetKind.Other, ArithmeticCore.Dynamic, isChecked, lift,
                operand, member: null);
        }

        // ── target-dominated shapes (checked before symbol resolution) ──

        if (IsEventSymbol(symbol) || symbolInfo.CandidateSymbols.OfType<IEventSymbol>().Any())
        {
            return NewClassification(
                kind, TargetKind.Event, ArithmeticCore.DirectMap, isChecked,
                Liftability.NoAnchor, operand, member: null);
        }

        if (operand is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PointerIndirectionExpression }
            or MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.PointerMemberAccessExpression })
        {
            return NewClassification(
                kind, TargetKind.Pointer, ArithmeticCore.DirectMap, isChecked,
                Liftability.NoAnchor, operand, member: null);
        }

        if (operand is ConditionalAccessExpressionSyntax or MemberBindingExpressionSyntax
            || unaryNode.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any())
        {
            return NewClassification(
                kind, TargetKind.Conditional, ArithmeticCore.DirectMap, isChecked,
                Liftability.NoAnchor, operand, member: null);
        }

        // ── element access: indexer vs true array (decision 4 / 15) ──

        if (operand is ElementAccessExpressionSyntax)
        {
            // Candidate symbols cover read-only / ref-readonly indexers the
            // same way the property branch does (++ on a get-only indexer is
            // CS0200/CS0443; ++ on a ref-readonly indexer is CS8331), so the
            // illegal-target/ref-readonly diagnostics still name them.
            var indexerProperty = symbol as IPropertySymbol
                ?? symbolInfo.CandidateSymbols.OfType<IPropertySymbol>()
                    .FirstOrDefault(property => property.IsIndexer);
            if (indexerProperty != null)
            {
                var indexerTarget = indexerProperty.RefKind != RefKind.None
                    ? TargetKind.RefReturning
                    : TargetKind.Indexer;
                return NewClassification(
                    kind, indexerTarget,
                    ClassifyCore(unaryNode, operandType, operation, forComplexTarget: true),
                    isChecked, lift, operand, indexerProperty);
            }

            if (IsTrueArrayElement(operand, out var elementType))
            {
                var elementCore = ClassifyCore(unaryNode, elementType, operation, forComplexTarget: false);
                var special = elementCore is ArithmeticCore.EnumCast
                    or ArithmeticCore.EnumFromValue
                    or ArithmeticCore.Decimal
                    or ArithmeticCore.CustomOp
                    or ArithmeticCore.NativeInt;
                return NewClassification(
                    kind,
                    special ? TargetKind.ArrayElementSpecial : TargetKind.NotOwned,
                    special ? elementCore : ArithmeticCore.DirectMap,
                    isChecked,
                    special ? lift : Liftability.NotNeeded,
                    operand, member: null);
            }

            return NewClassification(
                kind, TargetKind.Other, ArithmeticCore.BuiltinNarrow, isChecked, lift,
                operand, member: null);
        }

        // ── ref-returning method invocation: M()++ is legal only when M
        //    returns by ref (decision 16); a by-value method result is a
        //    baseline CS1059/CS0211 error and stays Other/unowned. ──

        if (operand is InvocationExpressionSyntax)
        {
            var invokedMethod = symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (invokedMethod != null && invokedMethod.RefKind != RefKind.None)
            {
                return NewClassification(
                    kind, TargetKind.RefReturning,
                    ClassifyCore(unaryNode, operandType, operation, forComplexTarget: true),
                    isChecked, lift, operand, invokedMethod, invokedMethod.RefKind);
            }

            return NewClassification(
                kind, TargetKind.Other, ArithmeticCore.BuiltinNarrow, isChecked, lift,
                operand, member: null);
        }

        // ── property (member access / identifier bound to IPropertySymbol) ──
        // Candidate symbols cover read-only targets (P++ on a get-only property
        // is CS0200/CS0443) so the illegal-target diagnostic still names them.

        var boundProperty = symbol as IPropertySymbol
            ?? symbolInfo.CandidateSymbols.OfType<IPropertySymbol>().FirstOrDefault();
        if (boundProperty != null)
        {
            var propertyTarget = boundProperty.RefKind != RefKind.None
                ? TargetKind.RefReturning
                : TargetKind.Property;
            return NewClassification(
                kind, propertyTarget,
                ClassifyCore(unaryNode, operandType, operation, forComplexTarget: true),
                isChecked, lift, operand, boundProperty);
        }

        // ── field reached through an effectful/qualified receiver:
        //    member-shaped RMW target (rendering is identical to a property) ──

        if (symbol is IFieldSymbol field
            && operand is MemberAccessExpressionSyntax fieldAccess
            && !IsSimpleFieldReceiver(fieldAccess.Expression))
        {
            return NewClassification(
                kind, TargetKind.Property,
                ClassifyCore(unaryNode, operandType, operation, forComplexTarget: true),
                isChecked, lift, operand, field);
        }

        // ── simple storage: local / parameter / side-effect-free field ──

        var simpleShape = ClassifySimpleShape(operand, symbol);
        if (simpleShape != null)
        {
            // Half/Int128/UInt128 are not in this rule's code segment and are
            // not owned; nint/nuint/IntPtr/UIntPtr are (CS2J5333, decision 10).
            if (NumericBoundaryAnalyzer.IsUnsupportedNumericType(operandType)
                && operandType.SpecialType is not SpecialType.System_IntPtr
                    and not SpecialType.System_UIntPtr)
            {
                return NewClassification(
                    kind, TargetKind.Other, ArithmeticCore.BuiltinNarrow, isChecked, lift,
                    operand, member: null);
            }

            var core = ClassifyCore(unaryNode, operandType, operation, forComplexTarget: false);
            var target = core == ArithmeticCore.DirectMap
                ? TargetKind.NotOwned
                : simpleShape.Value;
            return NewClassification(
                kind, target, core, isChecked,
                target == TargetKind.NotOwned ? Liftability.NotNeeded : lift,
                operand, member: null);
        }

        return NewClassification(
            kind, TargetKind.Other, ArithmeticCore.BuiltinNarrow, isChecked, lift,
            operand, member: null);
    }

    private static IncrementTargetClassification NewClassification(
        SyntaxKind kind,
        TargetKind target,
        ArithmeticCore core,
        bool isChecked,
        Liftability lift,
        ExpressionSyntax operand,
        ISymbol? member,
        RefKind refKind = RefKind.None)
    {
        var property = member as IPropertySymbol;
        var effectiveRefKind = refKind != RefKind.None
            ? refKind
            : property?.RefKind ?? RefKind.None;
        return new IncrementTargetClassification(
            kind, target, core, isChecked, lift, property, operand, member,
            property?.GetMethod, property?.SetMethod, member?.IsStatic ?? false,
            effectiveRefKind);
    }

    /// <summary>All ++/-- sites in tree order (rules and tests iterate this).</summary>
    public IReadOnlyList<(ExpressionSyntax Node, IncrementTargetClassification C)> FindAllSites(SyntaxNode root)
    {
        var sites = new List<(ExpressionSyntax, IncrementTargetClassification)>();
        foreach (var node in root.DescendantNodes()
                     .OfType<ExpressionSyntax>()
                     .Where(n => Array.IndexOf(IncrementKinds, n.Kind()) >= 0))
        {
            var classification = Classify(node);
            if (classification != null)
            {
                sites.Add((node, classification));
            }
        }

        return sites;
    }

    private static bool IsEventSymbol(ISymbol? symbol) => symbol is IEventSymbol;

    private bool IsTrueArrayElement(ExpressionSyntax operand, out ITypeSymbol? elementType)
    {
        elementType = null;
        if (operand is not ElementAccessExpressionSyntax elementAccess)
        {
            return false;
        }

        var receiverType = _model.GetTypeInfo(elementAccess.Expression).Type;
        if (receiverType is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A field member access whose receiver cannot observe a side effect
    /// (this/base/bare identifier) stays a simple shape; a field through an
    /// invocation, nested property or any other expression is a member RMW
    /// target and needs receiver hoisting.
    /// </summary>
    private static bool IsSimpleFieldReceiver(ExpressionSyntax receiver)
    {
        receiver = IncrementTargetSyntaxFacts.UnwrapParens(receiver);
        return receiver is ThisExpressionSyntax
            or BaseExpressionSyntax
            or IdentifierNameSyntax;
    }

    /// <summary>
    /// Local/parameter or a side-effect-free field access (bare identifier,
    /// this./base. or an identifier-qualified member). Reuses the C15 shape
    /// classifier for bare/this/base identifiers and extends minimally for
    /// simple-qualified fields.
    /// </summary>
    private TargetKind? ClassifySimpleShape(ExpressionSyntax operand, ISymbol? symbol)
    {
        switch (symbol)
        {
            case ILocalSymbol or IParameterSymbol:
                return TargetKind.SimpleDirect;
            case IFieldSymbol when operand is IdentifierNameSyntax:
                return TargetKind.SimpleDirect;
            case IFieldSymbol when operand is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax }:
                return TargetKind.SimpleDirect;
            case IFieldSymbol when operand is MemberAccessExpressionSyntax fieldAccess
                && IsSideEffectFreeReceiver(fieldAccess.Expression):
                return TargetKind.SimpleDirect;
            default:
                // Fall back to the C15 shape view (covers its this./base and
                // bare-name handling exactly).
                return _shared.ClassifyTarget(operand) switch
                {
                    NumericBoundaryAnalyzer.TargetShape.LocalOrParameter => TargetKind.SimpleDirect,
                    NumericBoundaryAnalyzer.TargetShape.Field => TargetKind.SimpleDirect,
                    _ => null,
                };
        }
    }

    private static bool IsSideEffectFreeReceiver(ExpressionSyntax receiver) =>
        IncrementTargetSyntaxFacts.UnwrapParens(receiver)
            is ThisExpressionSyntax
            or BaseExpressionSyntax
            or IdentifierNameSyntax;

    /// <summary>
    /// Select the arithmetic core from the operand static type and the bound
    /// increment operation. <paramref name="forComplexTarget"/> distinguishes
    /// int-family direct map (simple targets, converter-owned) from narrow
    /// cast cores (property/indexer targets, §4.2).
    /// </summary>
    private ArithmeticCore ClassifyCore(
        ExpressionSyntax unaryNode,
        ITypeSymbol? type,
        NBOp.IIncrementOrDecrementOperation? operation,
        bool forComplexTarget)
    {
        if (_shared.IsWithinExpressionTree(unaryNode))
        {
            return ArithmeticCore.ExpressionTree;
        }

        if (type?.TypeKind == TypeKind.Dynamic)
        {
            return ArithmeticCore.Dynamic;
        }

        if (operation?.IsLifted == true || IsNullableNumericOrEnum(type))
        {
            return ArithmeticCore.LiftedNullable;
        }

        if (operation?.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator })
        {
            // IT-8 true site (decision 11): CS2J5338 until the C14
            // Increment/Decrement naming contract freezes. The desugared-call
            // extension point is TryGetDesugaredIncrementContract; a 2026-10-01
            // probe proved a hand-authored `static K Increment(K)` method never
            // binds `k++` (overload resolution always selects op_Increment), so
            // the desugared shape is unreachable in source and no invocation is
            // rendered from source.
            return ArithmeticCore.CustomOp;
        }

        if (type?.TypeKind == TypeKind.Enum)
        {
            return ArithmeticCore.EnumCast;
        }

        // C05 dual-contract fingerprint hook (decision 7): a non-enum value
        // type carrying instance Value + static FromValue(U). Detection only;
        // IsOwnedByRule keeps EnumFromValue unowned and no FromValue call is
        // rendered until C05 lands and M2 wires the dual-form expansion.
        if (HasC05FromValueFingerprint(type))
        {
            return ArithmeticCore.EnumFromValue;
        }

        if (type?.SpecialType == SpecialType.System_Decimal)
        {
            return ArithmeticCore.Decimal;
        }

        if (type?.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
        {
            return ArithmeticCore.NativeInt;
        }

        switch (type?.SpecialType)
        {
            case SpecialType.System_SByte
                or SpecialType.System_Int16
                or SpecialType.System_Int32
                or SpecialType.System_Int64
                or SpecialType.System_Char:
                return forComplexTarget ? ArithmeticCore.BuiltinNarrow : ArithmeticCore.DirectMap;
            case SpecialType.System_Byte
                or SpecialType.System_UInt16:
                return forComplexTarget ? ArithmeticCore.BuiltinNarrow : ArithmeticCore.UnsignedDomain;
            case SpecialType.System_UInt32
                or SpecialType.System_UInt64:
                return ArithmeticCore.UnsignedDomain;
            default:
                return forComplexTarget ? ArithmeticCore.BuiltinNarrow : ArithmeticCore.DirectMap;
        }
    }

    private static bool IsNullableNumericOrEnum(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named
            || named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        {
            return false;
        }

        var argument = named.TypeArguments.FirstOrDefault();
        return argument.TypeKind == TypeKind.Enum
            || NumericBoundarySyntaxFacts.IsIntegralType(argument)
            || argument.SpecialType == SpecialType.System_Decimal;
    }

    /// <summary>
    /// C05 FromValue dual-form fingerprint (decision 7, M2 detection hook):
    /// a non-enum value type exposing an instance <c>Value</c> property and a
    /// static one-parameter <c>FromValue(U)</c> method, as C05 D6/D12 will
    /// generate. C05 is not landed yet, so this is used to classify the core
    /// as <see cref="ArithmeticCore.EnumFromValue"/> while ownership stays off;
    /// the hook must never guess member text beyond the symbol fingerprint and
    /// must never render a FromValue call on its own.
    /// </summary>
    private static bool HasC05FromValueFingerprint(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || type.TypeKind == TypeKind.Enum)
        {
            return false;
        }

        if (!named.IsValueType)
        {
            return false;
        }

        var hasInstanceValue = named.GetMembers("Value")
            .OfType<IPropertySymbol>()
            .Any(property => !property.IsStatic);
        if (!hasInstanceValue)
        {
            return false;
        }

        return named.GetMembers("FromValue")
            .OfType<IMethodSymbol>()
            .Any(method => method.IsStatic
                && method.MethodKind == MethodKind.Ordinary
                && method.Parameters.Length == 1);
    }

    /// <summary>
    /// IT-8 desugaring extension point (decision 11): once C14 freezes the
    /// Increment/Decrement member names in OperatorNames.cs, resolve the bound
    /// desugared static factory here and render <c>Qual.Increment(old)</c> with
    /// the return-conversion chain. The contract is deliberately reported as
    /// unavailable for now: true sites are CS2J5338 and a hand-authored static
    /// Increment/Decrement method can never bind <c>++/--</c> (2026-10-01 probe).
    /// </summary>
    internal static bool TryGetDesugaredIncrementContract(
        IMethodSymbol? operatorMethod, out IMethodSymbol? desugaredMethod)
    {
        desugaredMethod = null;
        return false;
    }

    /// <summary>
    /// Checkedness from syntax (M0): innermost checked/unchecked keyword wins;
    /// anonymous-function boundaries reset to the project default and hide
    /// outer scopes (mirrors the C15 analyzer frame semantics); local
    /// functions inherit, so they do not reset.
    /// </summary>
    private bool ResolveIsChecked(SyntaxNode unaryNode)
    {
        bool? keywordChecked = null;
        for (var current = unaryNode.Parent; current != null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax)
            {
                return keywordChecked ?? _shared.ProjectDefaultChecked;
            }

            if (keywordChecked == null)
            {
                switch (current)
                {
                    case CheckedExpressionSyntax expression:
                        keywordChecked = expression.IsKind(SyntaxKind.CheckedExpression);
                        break;
                    case CheckedStatementSyntax statement:
                        keywordChecked = statement.IsKind(SyntaxKind.CheckedStatement);
                        break;
                }
            }
        }

        return keywordChecked ?? _shared.ProjectDefaultChecked;
    }

    /// <summary>
    /// Lift bucket (decision 14 whitelist). The nearest enclosing statement is
    /// the anchor: crossing an anonymous-function/local-function boundary
    /// (checked from above the anchor), a query/initializer/arrow/conditional
    /// expression, or landing on a non-whitelisted statement (if/while/for
    /// condition position, lock/using headers, …) means NoAnchor. A
    /// field/property/attribute initializer has no enclosing statement and is
    /// NoAnchor too.
    /// </summary>
    private static Liftability ClassifyLift(ExpressionSyntax unaryNode, bool isPrefix)
    {
        var anchor = IncrementTargetSyntaxFacts.FindAnchorStatement(unaryNode);
        if (anchor == null
            || IncrementTargetSyntaxFacts.IsBlockedPath(unaryNode, anchor)
            || CrossesFunctionScope(anchor)
            || !IncrementTargetSyntaxFacts.IsWhitelistedAnchor(anchor))
        {
            return Liftability.NoAnchor;
        }

        if (anchor is ExpressionStatementSyntax expressionStatement)
        {
            return IncrementTargetSyntaxFacts.IsStandaloneExpression(unaryNode, expressionStatement)
                ? Liftability.Standalone
                : isPrefix ? Liftability.InlinePrefix : Liftability.LiftablePostfix;
        }

        return isPrefix ? Liftability.InlinePrefix : Liftability.LiftablePostfix;
    }

    /// <summary>
    /// True when the anchor statement lives inside a lambda/anonymous-method
    /// or a local function body (decision 14 keeps M1 out of both even when
    /// the hoist itself would be block-local).
    /// </summary>
    private static bool CrossesFunctionScope(StatementSyntax anchor)
    {
        for (var current = anchor.Parent; current != null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return true;
            }

            if (current is MemberDeclarationSyntax)
            {
                return false;
            }
        }

        return false;
    }
}
