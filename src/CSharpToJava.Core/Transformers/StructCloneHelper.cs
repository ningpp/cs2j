using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers;

/// <summary>
/// Shared utilities for struct value-copy (clone) semantics during C#-to-Java conversion.
/// C# structs are value types — assignment, argument passing, and returns all copy the value.
/// Java has no value types, so we insert .clone() calls to preserve these semantics.
/// </summary>
internal static class StructCloneHelper
{
    /// <summary>
    /// Returns true when <paramref name="type"/> is a user-defined C# struct that requires
    /// clone() calls to preserve value-type copy semantics in Java.
    /// Excludes primitive types, enums, Nullable&lt;T&gt;, and types without source declarations.
    /// </summary>
    public static bool IsUserDefinedStruct(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        if (named.TypeKind != TypeKind.Struct || named.TypeKind == TypeKind.Enum)
            return false;

        if (named.SpecialType != SpecialType.None)
            return false;

        if (named.OriginalDefinition.SpecialType != SpecialType.None)
            return false;

        if (named.OriginalDefinition.ToDisplayString() == "System.Nullable<T>")
            return false;

        return named.DeclaringSyntaxReferences.Length > 0
            || named.OriginalDefinition.DeclaringSyntaxReferences.Length > 0;
    }

    /// <summary>
    /// Returns true when the expression produces a fresh value that doesn't need cloning
    /// (e.g. new Foo(), method return values, default(T)).
    /// </summary>
    public static bool IsCloneableTemporary(ExpressionSyntax expression)
    {
        // Unwrap parentheses
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;

        return expression is ObjectCreationExpressionSyntax       // new Foo()
            or ImplicitObjectCreationExpressionSyntax              // new()
            or InvocationExpressionSyntax                          // Method() — return is a fresh copy if the method was converted
            or DefaultExpressionSyntax                             // default(T)
            or LiteralExpressionSyntax;                            // literal values
        // Note: ElementAccessExpressionSyntax (array[i]) is intentionally NOT excluded.
        // In C# array[i] for structs returns a copy, but in Java it returns a reference.
        // The existing ArgumentTransformer skips it for backward compatibility.
    }

    /// <summary>
    /// Appends .clone() to the transformed expression, handling complex expressions
    /// that need parenthesizing.
    /// </summary>
    public static string BuildCloneExpression(ExpressionSyntax originalSyntax, string transformedExpression)
    {
        var trimmed = transformedExpression.Trim();
        if (trimmed.Length == 0)
            return transformedExpression;

        if (CanCallCloneDirectly(originalSyntax))
            return $"{transformedExpression}.clone()";

        return $"(({transformedExpression})).clone()";
    }

    /// <summary>
    /// Checks whether .clone() can be appended directly (simple expressions)
    /// vs needing parenthesization (complex expressions).
    /// </summary>
    private static bool CanCallCloneDirectly(ExpressionSyntax expression)
        => expression is IdentifierNameSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax;

    /// <summary>
    /// For assignment/declaration/return contexts: returns the expression with .clone()
    /// appended if the type is a user-defined struct and the expression is not a temporary.
    /// </summary>
    public static string CloneStructValueIfNeeded(
        ExpressionSyntax exprNode,
        string transformedExpr,
        ITypeSymbol? exprType,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return transformedExpr;

        if (!IsUserDefinedStruct(exprType))
            return transformedExpr;

        if (IsCloneableTemporary(exprNode))
            return transformedExpr;

        // Skip if the expression already ends with .clone()
        if (transformedExpr.TrimEnd().EndsWith(".clone()"))
            return transformedExpr;

        return BuildCloneExpression(exprNode, transformedExpr);
    }
}
