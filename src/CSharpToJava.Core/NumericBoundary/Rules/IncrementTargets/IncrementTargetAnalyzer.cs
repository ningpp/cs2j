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

    Property,
    Indexer,

    /// <summary>Array element whose element type is enum/decimal/custom-inc/nint (decision 15).</summary>
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
/// Coarse M0 liftability of a site (design decision 14). M1 adds the
/// preceding-side-effect proof; M0 only separates standalone statements,
/// value-consumed positions inside the unconditional-execution whitelist
/// (ExpressionStatement/LocalDeclaration/Return/Throw/YieldReturn) and
/// anchor-free positions.
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
}

/// <summary>
/// M0 four-tuple classification of one ++/-- site:
/// (UnaryKind, TargetKind, ArithmeticCore, Checkedness, Liftability).
/// M1 extends the record with get/set symbols and RefKind details.
/// </summary>
internal sealed record IncrementTargetClassification(
    SyntaxKind UnaryKind,
    TargetKind Target,
    ArithmeticCore Core,
    bool IsChecked,
    Liftability Lift,
    IPropertySymbol? Property);

/// <summary>
/// Read-only semantic classifier for C17 (design §4.2). Every decision comes
/// from the <see cref="SemanticModel"/> (GetSymbolInfo/GetTypeInfo/GetOperation);
/// token text is never trusted. M0 never writes the tree, emits no diagnostics
/// and moves no statistics.
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
    /// Classify a PrefixUnary/PostfixUnary ++/-- node; returns null for any other
    /// node or operator kind.
    /// </summary>
    public IncrementTargetClassification? Classify(ExpressionSyntax unaryNode)
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

        var operand = unaryNode is PrefixUnaryExpressionSyntax pre
            ? pre.Operand
            : ((PostfixUnaryExpressionSyntax)unaryNode).Operand;

        var operation = _model.GetOperation(unaryNode) as NBOp.IIncrementOrDecrementOperation;
        var operandType = _model.GetTypeInfo(operand).Type
            ?? _model.GetTypeInfo(operand).ConvertedType;

        var symbolInfo = _model.GetSymbolInfo(operand);
        var symbol = symbolInfo.Symbol;
        var isPrefix = kind is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression;
        var isChecked = ResolveIsChecked(unaryNode);
        var lift = ClassifyLift(unaryNode, isPrefix);

        // ── target-dominated shapes (checked before symbol resolution) ──

        if (IsEventSymbol(symbol) || symbolInfo.CandidateSymbols.OfType<IEventSymbol>().Any())
        {
            return new IncrementTargetClassification(
                kind, TargetKind.Event, ArithmeticCore.DirectMap, isChecked, Liftability.NoAnchor, null);
        }

        if (operand is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PointerIndirectionExpression }
            or MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.PointerMemberAccessExpression })
        {
            return new IncrementTargetClassification(
                kind, TargetKind.Pointer, ArithmeticCore.DirectMap, isChecked, Liftability.NoAnchor, null);
        }

        if (operand is ConditionalAccessExpressionSyntax or MemberBindingExpressionSyntax
            || unaryNode.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any())
        {
            return new IncrementTargetClassification(
                kind, TargetKind.Conditional, ArithmeticCore.DirectMap, isChecked, Liftability.NoAnchor, null);
        }

        // ── element access: indexer vs true array (decision 4 / 15) ──

        if (operand is ElementAccessExpressionSyntax)
        {
            if (symbol is IPropertySymbol indexerProperty)
            {
                var indexerTarget = indexerProperty.RefKind != RefKind.None
                    ? TargetKind.RefReturning
                    : TargetKind.Indexer;
                return new IncrementTargetClassification(
                    kind, indexerTarget,
                    ClassifyCore(unaryNode, operandType, operation, forComplexTarget: true),
                    isChecked, lift, indexerProperty);
            }

            if (IsTrueArrayElement(operand, out var elementType))
            {
                var elementCore = ClassifyCore(unaryNode, elementType, operation, forComplexTarget: false);
                var special = elementCore is ArithmeticCore.EnumCast
                    or ArithmeticCore.EnumFromValue
                    or ArithmeticCore.Decimal
                    or ArithmeticCore.CustomOp
                    or ArithmeticCore.NativeInt;
                return new IncrementTargetClassification(
                    kind,
                    special ? TargetKind.ArrayElementSpecial : TargetKind.NotOwned,
                    special ? elementCore : ArithmeticCore.DirectMap,
                    isChecked,
                    special ? lift : Liftability.NotNeeded,
                    null);
            }

            return new IncrementTargetClassification(
                kind, TargetKind.Other, ArithmeticCore.BuiltinNarrow, isChecked, lift, null);
        }

        // ── property (member access / identifier bound to IPropertySymbol) ──

        if (symbol is IPropertySymbol property)
        {
            var propertyTarget = property.RefKind != RefKind.None
                ? TargetKind.RefReturning
                : TargetKind.Property;
            return new IncrementTargetClassification(
                kind, propertyTarget,
                ClassifyCore(unaryNode, operandType, operation, forComplexTarget: true),
                isChecked, lift, property);
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
                return new IncrementTargetClassification(
                    kind, TargetKind.Other, ArithmeticCore.BuiltinNarrow, isChecked, lift, null);
            }

            var core = ClassifyCore(unaryNode, operandType, operation, forComplexTarget: false);
            var target = core == ArithmeticCore.DirectMap
                ? TargetKind.NotOwned
                : simpleShape.Value;
            return new IncrementTargetClassification(
                kind, target, core, isChecked,
                target == TargetKind.NotOwned ? Liftability.NotNeeded : lift, null);
        }

        return new IncrementTargetClassification(
            kind, TargetKind.Other, ArithmeticCore.BuiltinNarrow, isChecked, lift, null);
    }

    /// <summary>All ++/-- sites in tree order (rules and tests iterate this in M0/M1).</summary>
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
    /// Local/parameter or a side-effect-free field access (bare identifier,
    /// this./base. or a simple-name qualified member). Reuses the C15 shape
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
        receiver is ThisExpressionSyntax
            or BaseExpressionSyntax
            or IdentifierNameSyntax
            or ParenthesizedExpressionSyntax;

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
            return ArithmeticCore.CustomOp;
        }

        if (type?.TypeKind == TypeKind.Enum)
        {
            // C05 FromValue dual fingerprint is M2 work; do not guess it here.
            return ArithmeticCore.EnumCast;
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
    /// Coarse lift bucket (decision 14 whitelist). Walks parents until the
    /// nearest enclosing statement: crossing an anonymous function/query or
    /// hitting a non-whitelisted statement (if/while/for/… condition etc.)
    /// means NoAnchor; a field/property/attribute initializer has no enclosing
    /// statement and is therefore also NoAnchor.
    /// </summary>
    private static Liftability ClassifyLift(ExpressionSyntax unaryNode, bool isPrefix)
    {
        for (SyntaxNode? current = unaryNode; current.Parent != null; current = current.Parent)
        {
            var parent = current.Parent;

            if (parent is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax)
            {
                return Liftability.NoAnchor;
            }

            switch (parent)
            {
                case ExpressionStatementSyntax expressionStatement:
                    return expressionStatement.Expression == unaryNode
                        ? Liftability.Standalone
                        : isPrefix ? Liftability.InlinePrefix : Liftability.LiftablePostfix;
                case LocalDeclarationStatementSyntax
                    or ReturnStatementSyntax
                    or ThrowStatementSyntax
                    or YieldStatementSyntax:
                    return isPrefix ? Liftability.InlinePrefix : Liftability.LiftablePostfix;
            }

            if (parent is StatementSyntax)
            {
                // Site sits in a non-whitelisted statement (condition position,
                // switch section label expressions, lock/using headers, …).
                return Liftability.NoAnchor;
            }
        }

        return Liftability.NoAnchor;
    }
}
