using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CSharpToJava.Core.Analysis;

namespace CSharpToJava.Core.EqualityConcat;

/// <summary>
/// Semantic categories emitted by <see cref="SemanticKindClassifier"/>
/// (design §4.1). Every rule shares this single classifier; rule files never
/// guess categories from syntax shapes.
/// </summary>
internal enum EqualityConcatKind
{
    /// <summary>string ==/!= bound to string.op_Equality/op_Inequality (EC-1).</summary>
    StringEquality,

    /// <summary>Reference identity bound to object.op_Equality, incl. (object) escapes (EC-2).</summary>
    ObjectIdentity,

    /// <summary>delegate ==/!= bound to the delegate type's predefined operator (EC-6).</summary>
    DelegateEquality,

    /// <summary>record class/struct synthesized == (UserDefinedOperator, implicit, record) (EC-7).</summary>
    RecordValueEquality,

    /// <summary>Pure string + bound to string.op_Addition (EC-3).</summary>
    StringConcat,

    /// <summary>string += compound assignment bound to string.op_Addition (EC-4).</summary>
    StringConcatCompound,

    /// <summary>Lifted ==/!= (Nullable), assigned to the C22 pass (EC-8).</summary>
    LiftedNullableEquality,

    /// <summary>Tuple-typed binary that E06 should already have rewritten (EC-9 edge).</summary>
    TupleEqualityResidual,

    /// <summary>event == null: left alone, the converter has a dedicated path.</summary>
    EventNullCheck,

    /// <summary>Source/metadata user-defined operator: belongs to C14.</summary>
    UserDefinedOperator,

    /// <summary>Predefined primitive/enum/other operator: intentionally untouched.</summary>
    PredefinedOther,

    /// <summary>Site inside an Expression&lt;Lambda&gt; tree (skip, CS2J0472).</summary>
    ExpressionTree,

    /// <summary>Dynamically bound site (CS2J0471).</summary>
    Dynamic,

    /// <summary>Neither operation nor symbols can classify the site (CS2J0470).</summary>
    Unknown,
}

/// <summary>Result of classifying one binary/assignment syntax node.</summary>
internal sealed class EqualityConcatClassification
{
    public required EqualityConcatKind Kind;

    /// <summary>Bound operator method (symbol-primary judgment, design §2 decision 1).</summary>
    public IMethodSymbol? BoundMethod;

    /// <summary>True for an equality site (<c>==</c>/<c>!=</c>).</summary>
    public bool IsEquality;

    /// <summary>True when the equality site is <c>!=</c> (or a negated kind).</summary>
    public bool IsNotEquals;

    /// <summary>Operation returned for the node, when present.</summary>
    public IOperation? Operation;
}

/// <summary>
/// The single semantic classifier of the equality/concat pass. Per the M0
/// probe (Roslyn referenced by this repository; probe project under
/// tests/EqualityConcatProbe), category distinction is driven primarily by
/// the bound symbol from GetSymbolInfo/OperatorMethod and secondarily by the
/// public <see cref="BinaryOperatorKind"/>: all equality sites report the
/// public kind Equals/NotEquals and string concatenation reports Add, so the
/// internal CSharp BinaryOperatorKind names (StringEqual etc.) must never be
/// used for public API judgment.
/// </summary>
internal static class SemanticKindClassifier
{
    /// <summary>Classify a <see cref="BinaryExpressionSyntax"/> or <see cref="AssignmentExpressionSyntax"/>.</summary>
    public static EqualityConcatClassification Classify(SyntaxNode node, SemanticModel model)
    {
        // Pipeline step 1: expression-tree ancestor short-circuit.
        var expressionTreeLambda = node.Ancestors().OfType<LambdaExpressionSyntax>()
            .FirstOrDefault(lambda => ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda));
        if (expressionTreeLambda != null)
        {
            return New(EqualityConcatKind.ExpressionTree);
        }

        var typeInfo = model.GetTypeInfo(node);

        // Pipeline step 3: dynamic binding.
        if (IsDynamic(typeInfo.Type) || IsDynamic(typeInfo.ConvertedType))
        {
            return New(EqualityConcatKind.Dynamic);
        }

        var operation = model.GetOperation(node);

        switch (operation)
        {
            case ICompoundAssignmentOperation compound:
                return ClassifyCompound(node, model, compound, typeInfo);

            case ITupleBinaryOperation:
                return New(EqualityConcatKind.TupleEqualityResidual, operation: operation);

            case IBinaryOperation binary:
                return ClassifyBinary(node, model, binary);

            case IDynamicInvocationOperation:
                return New(EqualityConcatKind.Dynamic, operation: operation);

            case IInvalidOperation:
                // IInvalidOperation always carries compiler diagnostics (e.g.
                // CS0019 for tuple == null); rely on them and do not invent a
                // site (design §2 decision 15).
                return New(EqualityConcatKind.PredefinedOther, operation: operation);

            default:
                return ClassifyFallback(node, model, typeInfo, operation);
        }
    }

    private static EqualityConcatClassification ClassifyBinary(
        SyntaxNode node,
        SemanticModel model,
        IBinaryOperation binary)
    {
        var isEquality = binary.OperatorKind is BinaryOperatorKind.Equals
            or BinaryOperatorKind.NotEquals
            or BinaryOperatorKind.ObjectValueEquals
            or BinaryOperatorKind.ObjectValueNotEquals;
        var isNotEquals = binary.OperatorKind is BinaryOperatorKind.NotEquals
            or BinaryOperatorKind.ObjectValueNotEquals;

        // Pipeline step 3: lifted equality -> C22 (everything lifted ==/!=,
        // including lifted user-defined operators).
        if (binary.IsLifted && isEquality)
        {
            return New(EqualityConcatKind.LiftedNullableEquality,
                BoundMethodOf(binary.OperatorMethod, node, model),
                isEquality, isNotEquals, binary);
        }

        var method = BoundMethodOf(binary.OperatorMethod, node, model);
        var kind = ClassifyByMethod(
            method, binary.OperatorKind, node, model, isEquality);

        return New(kind, method, isEquality, isNotEquals, binary);
    }

    private static EqualityConcatClassification ClassifyCompound(
        SyntaxNode node,
        SemanticModel model,
        ICompoundAssignmentOperation compound,
        TypeInfo _)
    {
        var method = BoundMethodOf(compound.OperatorMethod, node, model);

        if (compound.IsLifted)
        {
            // Lifted compound arithmetic/concatenation is not a string site.
            return New(EqualityConcatKind.PredefinedOther, method, operation: compound);
        }

        if (compound.OperatorKind == BinaryOperatorKind.Add
            && method is { ContainingType.SpecialType: SpecialType.System_String }
            && method.Name == "op_Addition")
        {
            return New(EqualityConcatKind.StringConcatCompound, method, operation: compound);
        }

        return New(EqualityConcatKind.PredefinedOther, method, operation: compound);
    }

    /// <summary>
    /// Pipeline steps 4-5 symbol dispatch. The bound symbol is the primary
    /// judgment; the public operator kind only separates Add from equality.
    /// </summary>
    private static EqualityConcatKind ClassifyByMethod(
        IMethodSymbol? method,
        BinaryOperatorKind operatorKind,
        SyntaxNode node,
        SemanticModel model,
        bool isEquality)
    {
        if (method == null)
        {
            return EqualityConcatKind.Unknown;
        }

        switch (method.MethodKind)
        {
            case MethodKind.UserDefinedOperator:
                // Record synthesized operators: implicit + record. Covers record
                // class and record struct (M0 probe).
                if (method.IsImplicitlyDeclared && method.ContainingType.IsRecord)
                {
                    return EqualityConcatKind.RecordValueEquality;
                }

                // Source-declared or metadata-only user operators belong to C14.
                return EqualityConcatKind.UserDefinedOperator;

            case MethodKind.BuiltinOperator:
            {
                var containingType = method.ContainingType;

                if (containingType.SpecialType == SpecialType.System_String)
                {
                    if (operatorKind == BinaryOperatorKind.Add && method.Name == "op_Addition")
                    {
                        return EqualityConcatKind.StringConcat;
                    }

                    if (isEquality)
                    {
                        return EqualityConcatKind.StringEquality;
                    }
                }

                if (isEquality && containingType.SpecialType == SpecialType.System_Object)
                {
                    // (ValueTuple)? == null binds object.op_Equality per the M0
                    // probe; ReferenceEquals would change semantics, so treat
                    // it as the tuple residual edge (CS2J0476).
                    if (HasNullableTupleOperand(node, model))
                        return EqualityConcatKind.TupleEqualityResidual;

                    return EqualityConcatKind.ObjectIdentity;
                }

                if (isEquality && IsDelegateContainingType(containingType))
                {
                    // evt == null shares the delegate operator; distinguish by operand.
                    return HasEventOperand(node, model)
                        ? EqualityConcatKind.EventNullCheck
                        : EqualityConcatKind.DelegateEquality;
                }

                return EqualityConcatKind.PredefinedOther;
            }

            default:
                return EqualityConcatKind.PredefinedOther;
        }
    }

    /// <summary>Pipeline step 7: operation-missing fallback (counted and visible).</summary>
    private static EqualityConcatClassification ClassifyFallback(
        SyntaxNode node,
        SemanticModel model,
        TypeInfo typeInfo,
        IOperation? operation)
    {
        if (model.GetSymbolInfo(node).Symbol is IMethodSymbol symbolMethod)
        {
            var isEquality = HasEqualityToken(node);
            var kind = ClassifyByMethod(
                symbolMethod, BinaryOperatorKind.None, node, model, isEquality);
            return New(kind, symbolMethod, isEquality, operation: operation);
        }

        // Conservative: two string operands, no (object) escape -> string equality.
        if (node is BinaryExpressionSyntax fallbackBinary
            && (fallbackBinary.OperatorToken.IsKind(SyntaxKind.EqualsEqualsToken)
                || fallbackBinary.OperatorToken.IsKind(SyntaxKind.ExclamationEqualsToken))
            && !HasObjectEscape(fallbackBinary, model)
            && model.GetTypeInfo(fallbackBinary.Left).Type?.SpecialType == SpecialType.System_String
            && model.GetTypeInfo(fallbackBinary.Right).Type?.SpecialType == SpecialType.System_String)
        {
            var isNotEquals = fallbackBinary.OperatorToken
                .IsKind(SyntaxKind.ExclamationEqualsToken);
            return New(EqualityConcatKind.StringEquality, null,
                isEquality: true, isNotEquals, operation);
        }

        return New(EqualityConcatKind.Unknown, operation: operation);
    }

    private static IMethodSymbol? BoundMethodOf(
        IMethodSymbol? operatorMethod,
        SyntaxNode node,
        SemanticModel model)
        => operatorMethod ?? (model.GetSymbolInfo(node).Symbol as IMethodSymbol);

    /// <summary>
    /// A delegate-equality containing type is either a specific delegate type
    /// (<c>delegate</c>-keyword types, incl. System.Action) or the predefined
    /// System.Delegate operator. System.MulticastDelegate binds the object
    /// operator instead (identity), per the M0 probe.
    /// </summary>
    private static bool IsDelegateContainingType(ITypeSymbol? containingType)
        => containingType is { TypeKind: TypeKind.Delegate }
            || containingType?.SpecialType == SpecialType.System_Delegate;

    private static bool HasEventOperand(SyntaxNode node, SemanticModel model)
    {
        if (node is not BinaryExpressionSyntax binary)
            return false;

        foreach (var expression in new[] { binary.Left, binary.Right })
        {
            var operandOperation = model.GetOperation(expression);
            if (operandOperation is IEventReferenceOperation)
                return true;

            if (model.GetSymbolInfo(expression).Symbol is IEventSymbol)
                return true;
        }

        return false;
    }

    private static bool HasEqualityToken(SyntaxNode node)
        => node is BinaryExpressionSyntax tokenBinary
            && (tokenBinary.OperatorToken.IsKind(SyntaxKind.EqualsEqualsToken)
                || tokenBinary.OperatorToken.IsKind(SyntaxKind.ExclamationEqualsToken));

    private static bool HasObjectEscape(BinaryExpressionSyntax binary, SemanticModel model)
    {
        foreach (var operand in new[] { binary.Left, binary.Right })
        {
            if (operand is CastExpressionSyntax cast
                && model.GetTypeInfo(cast.Type).Type?.SpecialType == SpecialType.System_Object)
            {
                return true;
            }

            if (operand.DescendantNodesAndSelf().OfType<CastExpressionSyntax>()
                .Any(c => model.GetTypeInfo(c.Type).Type?.SpecialType == SpecialType.System_Object))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an operand is <c>(ValueTuple)?</c> — its == null site binds
    /// the object operator but must not be rewritten to ReferenceEquals.
    /// </summary>
    private static bool HasNullableTupleOperand(SyntaxNode node, SemanticModel model)
    {
        if (node is not BinaryExpressionSyntax binary)
            return false;

        foreach (var expression in new[] { binary.Left, binary.Right })
        {
            if (model.GetTypeInfo(expression).Type is INamedTypeSymbol named
                && IsNullableTuple(named))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNullableTuple(INamedTypeSymbol type)
        => type.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && type.TypeArguments.FirstOrDefault() is INamedTypeSymbol inner
            && inner.IsTupleType;

    private static bool IsDynamic(ISymbol? symbol)
        => symbol is ITypeSymbol { TypeKind: TypeKind.Dynamic };

    private static EqualityConcatClassification New(
        EqualityConcatKind kind,
        IMethodSymbol? method = null,
        bool isEquality = false,
        bool isNotEquals = false,
        IOperation? operation = null)
        => new()
        {
            Kind = kind,
            BoundMethod = method,
            IsEquality = isEquality,
            IsNotEquals = isNotEquals,
            Operation = operation,
        };
}
