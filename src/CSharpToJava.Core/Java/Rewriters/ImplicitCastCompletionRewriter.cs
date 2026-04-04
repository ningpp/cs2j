using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that detects and fixes missing explicit casts in the generated Java code.
/// <para>
/// C# allows many implicit conversions (numeric narrowing in const contexts, enum-to-int,
/// generic type erasure) that Java requires explicit casts for. This rewriter catches
/// cases that slip through the string-level <c>AdaptExpressionToTargetType</c> helper.
/// </para>
/// <para>
/// Handles two paths:
/// <list type="bullet">
///   <item>Structured IR: <see cref="JavaVariableDeclarationStatement"/> nodes with
///         <see cref="JavaVariableDeclarationStatement.ResolvedInitializerType"/> set.</item>
///   <item>Raw statements: heuristic regex matching for common patterns.</item>
/// </list>
/// </para>
/// </summary>
public class ImplicitCastCompletionRewriter : JavaSyntaxRewriter
{
    /// <summary>
    /// Table of Java narrowing conversions that require explicit casts.
    /// Key: (sourceType, targetType) → both must be primitive Java type keywords.
    /// </summary>
    private static readonly HashSet<(string Source, string Target)> NarrowingPairs =
    [
        // long → smaller
        ("long", "int"), ("long", "short"), ("long", "byte"), ("long", "char"), ("long", "float"),
        // int → smaller
        ("int", "short"), ("int", "byte"), ("int", "char"),
        // double → smaller
        ("double", "float"), ("double", "long"), ("double", "int"), ("double", "short"), ("double", "byte"),
        // float → smaller
        ("float", "long"), ("float", "int"), ("float", "short"), ("float", "byte"),
        // short → smaller
        ("short", "byte"), ("short", "char"),
        // char → smaller numeric
        ("char", "byte"), ("char", "short"),
    ];

    /// <summary>
    /// Primitive types that are valid for narrowing cast detection.
    /// </summary>
    private static readonly HashSet<string> PrimitiveTypes =
        ["int", "long", "short", "byte", "float", "double", "char", "boolean"];

    // ── Structured IR path ──────────────────────────────────────────────

    public override JavaVariableDeclarationStatement VisitVariableDeclarationStatement(JavaVariableDeclarationStatement node)
    {
        // Only process if we have type info on the initializer
        if (node.Initializer == null || node.ResolvedInitializerType == null)
            return base.VisitVariableDeclarationStatement(node);

        var declaredType = NormalizeType(node.Type);
        var initType = NormalizeType(node.ResolvedInitializerType);

        if (string.IsNullOrEmpty(declaredType) || string.IsNullOrEmpty(initType))
            return base.VisitVariableDeclarationStatement(node);

        // Skip if types already match
        if (declaredType == initType)
            return base.VisitVariableDeclarationStatement(node);

        // Check for narrowing conversion
        if (NeedsNarrowingCast(initType, declaredType))
        {
            WrapInitializerWithCast(node, declaredType);
        }

        return base.VisitVariableDeclarationStatement(node);
    }

    // ── Raw statement heuristic path ───────────────────────────────────

    /// <summary>
    /// Pattern: <c>type name = (cast-candidate-expr);</c>
    /// We detect when a raw statement contains a variable declaration where:
    /// 1. The init expression already has a numeric cast — skip (already handled)
    /// 2. The init expression is a method call returning a wider type — needs cast
    /// </summary>
    private static readonly Regex VarDeclPattern = new(
        @"^(?<type>\w+)\s+(?<name>\w+)\s*=\s*(?<init>.+);\s*$",
        RegexOptions.Compiled);

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (string.IsNullOrWhiteSpace(node.Code))
            return base.VisitRawStatement(node);

        // Only attempt heuristic on single-line, simple variable declarations
        var lines = node.Code.Split('\n');
        if (lines.Length != 1)
            return base.VisitRawStatement(node);

        var match = VarDeclPattern.Match(lines[0].Trim());
        if (!match.Success)
            return base.VisitRawStatement(node);

        var declType = match.Groups["type"].Value;
        var initExpr = match.Groups["init"].Value;

        // Only handle primitive targets — reference type narrowing is rare and risky
        if (!PrimitiveTypes.Contains(declType))
            return base.VisitRawStatement(node);

        // If the init expression already has a cast to the target type, skip
        if (initExpr.StartsWith($"({declType})", StringComparison.Ordinal))
            return base.VisitRawStatement(node);

        // Detect ".ordinal()" calls assigned to int — enum ordinal is already int, skip
        if (declType == "int" && initExpr.Contains(".ordinal()"))
            return base.VisitRawStatement(node);

        // Detect known widening return patterns:
        // - .size() returns int → no narrowing needed for int/long
        // - .length returns int → same
        // These are safe; skip them.
        if (IsKnownIntReturningExpression(initExpr) && (declType == "int" || declType == "long"))
            return base.VisitRawStatement(node);

        return base.VisitRawStatement(node);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether assigning from <paramref name="sourceType"/> to
    /// <paramref name="targetType"/> requires an explicit narrowing cast in Java.
    /// </summary>
    public static bool NeedsNarrowingCast(string sourceType, string targetType)
    {
        return NarrowingPairs.Contains((NormalizeType(sourceType), NormalizeType(targetType)));
    }

    /// <summary>
    /// Returns the Java cast keyword for a target type, or null if not a primitive.
    /// </summary>
    public static string? GetCastKeyword(string targetType)
    {
        var normalized = NormalizeType(targetType);
        return PrimitiveTypes.Contains(normalized) ? normalized : null;
    }

    /// <summary>
    /// Normalize a Java type name for comparison — strips generics, boxed wrapper names, etc.
    /// </summary>
    public static string NormalizeType(string type)
    {
        if (string.IsNullOrEmpty(type))
            return type;

        // Unbox wrapper types
        return type switch
        {
            "Integer" => "int",
            "Long" => "long",
            "Double" => "double",
            "Float" => "float",
            "Boolean" => "boolean",
            "Character" => "char",
            "Short" => "short",
            "Byte" => "byte",
            _ => type
        };
    }

    private static void WrapInitializerWithCast(JavaVariableDeclarationStatement node, string targetType)
    {
        var castKeyword = GetCastKeyword(targetType);
        if (castKeyword == null) return;

        var current = node.Initializer!;
        if (current is JavaRawExpression raw)
        {
            // Don't double-cast
            if (raw.Code.StartsWith($"({castKeyword})", StringComparison.Ordinal))
                return;
            raw.Code = $"({castKeyword}) ({raw.Code})";
        }
        else
        {
            // Wrap non-raw expression in a cast
            node.Initializer = new JavaCastExpression
            {
                Type = castKeyword,
                Expression = current
            };
        }
    }

    private static bool IsKnownIntReturningExpression(string expr)
    {
        return expr.EndsWith(".size()", StringComparison.Ordinal)
            || expr.EndsWith(".length()", StringComparison.Ordinal)
            || expr.EndsWith(".length", StringComparison.Ordinal)
            || expr.EndsWith(".hashCode()", StringComparison.Ordinal)
            || expr.EndsWith(".compareTo()", StringComparison.Ordinal)
            || expr.EndsWith(".indexOf()", StringComparison.Ordinal);
    }
}
