using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBOp = Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// Semantic classification for the C34 decimal-ops rule. Every decision goes
/// through the <see cref="SemanticModel"/> (<c>GetOperation</c>/<c>GetTypeInfo</c>/
/// <c>GetSymbolInfo</c>); token text is never trusted, so <c>using D = System.Decimal;</c>
/// aliases resolve correctly while user types named <c>Decimal</c> and user-defined
/// operators are never touched (design hard invariant 1).
/// </summary>
internal static class DecimalTypeFacts
{
    // ── domains ──

    public enum OperandDomain
    {
        /// <summary>static type is System.Decimal — emitted unchanged.</summary>
        Decimal,

        /// <summary>sbyte/byte/short/ushort/char/int/long value — explicit (decimal) cast.</summary>
        SignedOrNarrowInt,

        /// <summary>real uint (E09 skips checked regions) — UnsignedMath.ToDecimalUInt32((int)x).</summary>
        NativeUInt32,

        /// <summary>real ulong — UnsignedMath.ToDecimalUInt64((long)x).</summary>
        NativeUInt64,

        /// <summary>E09 wrapper fingerprint Cs2jUnsigned.UInt / dotnet.System.UInt.</summary>
        WrappedUInt32,

        /// <summary>E09 wrapper fingerprint Cs2jUnsigned.ULong / dotnet.System.ULong.</summary>
        WrappedUInt64,

        /// <summary>integer literal operand — rendered with an explicit (decimal) cast.</summary>
        LiteralInteger,

        /// <summary>anything else (float/double/unknown wrapper/…) — CS2J5361.</summary>
        Other,
    }

    public enum SiteClassification
    {
        /// <summary>Not a C34 site (non-decimal binding, string concat, event subscription, …).</summary>
        NotOwned,

        /// <summary>Built-in decimal arithmetic/comparison/unary/compound site to rewrite.</summary>
        Owned,

        /// <summary>Nullable&lt;decimal&gt; lifted site (belongs to C22; left untouched).</summary>
        Lifted,

        /// <summary>Source user-defined operator (belongs to C14; left untouched).</summary>
        UserOperator,

        /// <summary>dynamic binding — CS2J5362.</summary>
        Dynamic,
    }

    public static bool IsDecimalType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_Decimal;

    /// <summary>
    /// True for a built-in operator method declared on <see cref="decimal"/>
    /// (op_Addition/op_Equality/op_UnaryNegation/…). Source user operators carry
    /// <see cref="MethodKind.UserDefinedOperator"/> and never match.
    /// </summary>
    public static bool IsBuiltInDecimalOperator(IMethodSymbol? method) =>
        method is { MethodKind: MethodKind.BuiltinOperator }
        && method.ContainingType.SpecialType == SpecialType.System_Decimal;

    /// <summary>True for a lifted (Nullable&lt;decimal&gt;) built-in operation.</summary>
    public static bool IsLifted(IOperation? operation) => operation switch
    {
        NBOp.IBinaryOperation b => b.IsLifted,
        NBOp.IUnaryOperation u => u.IsLifted,
        NBOp.ICompoundAssignmentOperation c => c.IsLifted,
        _ => false,
    };

    public static SiteClassification ClassifyOperation(IOperation? operation)
    {
        IMethodSymbol? operatorMethod = operation switch
        {
            NBOp.IBinaryOperation b => b.OperatorMethod,
            NBOp.IUnaryOperation u => u.OperatorMethod,
            NBOp.ICompoundAssignmentOperation c => c.OperatorMethod,
            _ => null,
        };

        if (IsLifted(operation))
        {
            return SiteClassification.Lifted;
        }

        var resultType = operation switch
        {
            NBOp.IBinaryOperation b => b.Type,
            NBOp.IUnaryOperation u => u.Type,
            NBOp.ICompoundAssignmentOperation c => c.Type,
            _ => null,
        };

        if (resultType?.TypeKind == TypeKind.Dynamic)
        {
            return SiteClassification.Dynamic;
        }

        if (operatorMethod != null)
        {
            return IsBuiltInDecimalOperator(operatorMethod)
                ? SiteClassification.Owned
                : SiteClassification.UserOperator;
        }

        // Roslyn exposes the built-in decimal operators with a null
        // OperatorMethod (same shape as intrinsic integer operators), so
        // ownership falls back to the binding's decimal domain. Lifted and
        // dynamic cases were already handled above; string concatenation,
        // integer and enum operations never bind into the decimal domain.
        return IsBuiltInBindingByDomain(operation)
            ? SiteClassification.Owned
            : SiteClassification.NotOwned;
    }

    private static bool IsBuiltInBindingByDomain(IOperation? operation)
    {
        switch (operation)
        {
            case NBOp.IBinaryOperation binary:
                // A decimal-result binary binding with a null OperatorMethod is
                // one of the built-in decimal arithmetic sites: bitwise/shift
                // built-ins do not accept decimal, and user-defined operators
                // surface a non-null UserDefinedOperator method.
                if (IsDecimalType(binary.Type))
                {
                    return true;
                }

                if (binary.OperatorKind is NBOp.BinaryOperatorKind.Equals
                    or NBOp.BinaryOperatorKind.NotEquals
                    or NBOp.BinaryOperatorKind.LessThan
                    or NBOp.BinaryOperatorKind.LessThanOrEqual
                    or NBOp.BinaryOperatorKind.GreaterThan
                    or NBOp.BinaryOperatorKind.GreaterThanOrEqual)
                {
                    // Comparisons return bool; look at the compared operands.
                    return IsDecimalType(binary.LeftOperand?.Type)
                        || IsDecimalType(binary.RightOperand?.Type);
                }

                return false;
            case NBOp.IUnaryOperation unary:
                return IsDecimalType(unary.Type);
            case NBOp.ICompoundAssignmentOperation compound:
                return IsDecimalType(compound.Type);
            default:
                return false;
        }
    }

    /// <summary>
    /// Natural (pre-conversion) domain of a binary/unary/compound operand
    /// expression. Conversion operations inserted by C# for implicit decimal
    /// promotion are ignored: classification looks at the source syntax's own
    /// type, so <c>d + i</c> promotes <c>i</c> while <c>d + 1m</c> stays decimal.
    /// </summary>
    public static OperandDomain ClassifyOperand(SemanticModel model, ExpressionSyntax expression)
    {
        var type = model.GetTypeInfo(expression).Type;
        if (type == null)
        {
            return OperandDomain.Other;
        }

        if (type.TypeKind == TypeKind.Dynamic)
        {
            return OperandDomain.Other;
        }

        if (IsDecimalType(type))
        {
            return OperandDomain.Decimal;
        }

        if (expression.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression)
            && IsIntegralSpecialType(type.SpecialType))
        {
            // uint/ulong literals keep their suffix in source; (decimal)2u compiles.
            return OperandDomain.LiteralInteger;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
                return OperandDomain.SignedOrNarrowInt;
            case SpecialType.System_UInt32:
                return OperandDomain.NativeUInt32;
            case SpecialType.System_UInt64:
                return OperandDomain.NativeUInt64;
        }

        // E09 wrapper fingerprints: the real E09 support structs live in the
        // dotnet.System namespace (src/Cs2jUnsigned/UInt.cs, ULong.cs); the
        // Cs2jUnsigned namespace is the defensive forward surface. E09 currently
        // keeps uint/ulong primitives across most of the C# intermediate tree,
        // so both paths are rare but routed through the same ToDecimal helpers.
        if (type is INamedTypeSymbol namedType
            && namedType.ContainingNamespace?.ToDisplayString() is "Cs2jUnsigned" or "dotnet.System")
        {
            switch (namedType.Name)
            {
                case "UInt":
                    return OperandDomain.WrappedUInt32;
                case "ULong":
                    return OperandDomain.WrappedUInt64;
            }
        }

        return OperandDomain.Other;
    }

    private static bool IsIntegralSpecialType(SpecialType specialType) => specialType is
        SpecialType.System_SByte
        or SpecialType.System_Byte
        or SpecialType.System_Int16
        or SpecialType.System_UInt16
        or SpecialType.System_Char
        or SpecialType.System_Int32
        or SpecialType.System_UInt32
        or SpecialType.System_Int64
        or SpecialType.System_UInt64;

    /// <summary>
    /// True when the node is (or is inside) a lambda converted to
    /// <c>System.Linq.Expressions.Expression&lt;TDelegate&gt;</c> (CS2J5363).
    /// Mirrors the C15 predicate; v1 keeps the check local to this rule.
    /// </summary>
    public static bool IsWithinExpressionTree(SemanticModel model, SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax lambda
                && model.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol converted
                && converted.IsGenericType
                && converted.OriginalDefinition.ContainingNamespace?.ToDisplayString()
                    == "System.Linq.Expressions"
                && converted.OriginalDefinition.Name == "Expression")
            {
                return true;
            }
        }

        return false;
    }

    // ── compound target shapes ──

    public enum TargetShape
    {
        /// <summary>Local/parameter, bare field, this/field or static field: read and write in place.</summary>
        Simple,

        /// <summary>Property/indexer/event or member access off an instance receiver: hoist required.</summary>
        Complex,
    }

    public static TargetShape ClassifyTargetShape(SemanticModel model, ExpressionSyntax target)
    {
        if (target is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is ThisExpressionSyntax
                or BaseExpressionSyntax)
            {
                return model.GetSymbolInfo(memberAccess.Name).Symbol is IFieldSymbol
                    ? TargetShape.Simple
                    : TargetShape.Complex;
            }

            var symbol = model.GetSymbolInfo(target).Symbol;
            if (symbol is IFieldSymbol field)
            {
                // Static fields need no receiver evaluation; instance fields off a
                // non-this receiver get the hoist treatment (receiver once).
                return field.IsStatic ? TargetShape.Simple : TargetShape.Complex;
            }

            return TargetShape.Complex;
        }

        if (target is ElementAccessExpressionSyntax)
        {
            return TargetShape.Complex;
        }

        if (target is IdentifierNameSyntax)
        {
            return model.GetSymbolInfo(target).Symbol switch
            {
                IFieldSymbol or ILocalSymbol or IParameterSymbol => TargetShape.Simple,
                _ => TargetShape.Complex,
            };
        }

        return TargetShape.Complex;
    }

    /// <summary>True when a compound target denotes a static property/event (no receiver evaluation).</summary>
    public static bool IsStaticMember(SemanticModel model, ExpressionSyntax target)
    {
        var symbol = model.GetSymbolInfo(target).Symbol;
        return symbol switch
        {
            IPropertySymbol property => property.IsStatic,
            IEventSymbol @event => @event.IsStatic,
            IMethodSymbol method => method.IsStatic, // indexer accessors via element access never reach here
            _ => false,
        };
    }

    // ── const-decimal planning (DC-12) ──

    /// <summary>
    /// Enumerate every const-decimal declarator (field or local) together with
    /// its initializer value expression.
    /// </summary>
    public static IEnumerable<(SyntaxNode Declarator, ISymbol Symbol, ExpressionSyntax Value)>
        EnumerateConstDecimalDeclarators(SemanticModel model, SyntaxNode root)
    {
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                if (variable.Initializer?.Value is { } value
                    && model.GetDeclaredSymbol(variable) is IFieldSymbol { IsConst: true } fieldSymbol
                    && IsDecimalType(fieldSymbol.Type))
                {
                    yield return (variable, fieldSymbol, value);
                }
            }
        }

        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in local.Declaration.Variables)
            {
                if (variable.Initializer?.Value is { } value
                    && model.GetDeclaredSymbol(variable) is ILocalSymbol { IsConst: true } localSymbol
                    && IsDecimalType(localSymbol.Type))
                {
                    yield return (variable, localSymbol, value);
                }
            }
        }
    }

    /// <summary>
    /// True when a syntax node is a built-in decimal operator that C34 rewrites.
    /// Used to seed const-downgrade planning (DC-12): a const initializer holding
    /// such a node cannot remain a compile-time constant.
    /// </summary>
    public static bool IsOwnedDecimalOperatorNode(SemanticModel model, SyntaxNode node)
    {
        var operation = model.GetOperation(node);
        return operation switch
        {
            NBOp.IBinaryOperation => ClassifyOperation(operation) == SiteClassification.Owned,
            NBOp.IUnaryOperation => ClassifyOperation(operation) == SiteClassification.Owned,
            NBOp.ICompoundAssignmentOperation => ClassifyOperation(operation) == SiteClassification.Owned,
            _ => false,
        };
    }

    /// <summary>1-based line/column of a site for MSBuild-style diagnostics.</summary>
    public static (int Line, int Column) GetLineColumn(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}
