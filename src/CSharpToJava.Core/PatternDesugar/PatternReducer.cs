using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// Result of reducing one pattern against one subject expression. Condition is
/// null exactly when Supported is false (an explicit PD010 was reported instead —
/// never emit a placeholder/always-true fallback).
/// </summary>
internal sealed record PatternReduction(
    ExpressionSyntax? Condition,
    IReadOnlyList<StatementSyntax> Prologue,
    bool Supported);

/// <summary>Shared state for one reduction sweep (C03 and C37 use the same shape).</summary>
internal sealed record PatternReductionContext(
    SemanticModel? Model,
    PatternDesugarOptions Options,
    List<PatternDesugarDiagnostic> Diagnostics,
    string? FilePath,
    Func<string, string> NewName)
{
    public void Report(string code, PatternDesugarSeverity severity, string message, SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var where = span.IsValid
            ? $" ({FilePath ?? span.Path}:{span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})"
            : "";
        Diagnostics.Add(new PatternDesugarDiagnostic(code, severity, message + where, FilePath));
    }
}

/// <summary>
/// Extension point frozen for C37: one reducer per pattern kind. C37 registers
/// positional/list/slice reducers without touching the C03 call sites.
/// </summary>
internal interface IPatternReducer
{
    bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx);

    PatternReduction Reduce(
        PatternSyntax pattern,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx);
}

/// <summary>
/// Reduces pattern syntax into plain boolean C# expressions. The output form
/// set is deliberately narrow: ==/!=, `is T [v]`, &&/||, !, member access —
/// everything the main converter already accepts. Object/dynamic/type-parameter
/// governing never emits a bare `gov == c` (boxing makes it a silent reference
/// comparison); those go through `gov is CT v && v == c`.
/// </summary>
internal static class PatternReducer
{
    // C03 statement-side registry, in dispatch order. C37 appends entries.
    private static readonly IReadOnlyList<IPatternReducer> Reducers =
    [
        new DiscardPatternReducer(),
        new VarPatternReducer(),
        new ConstantPatternReducer(),
        new TypePatternReducer(),
        new DeclarationPatternReducer(),
        new RelationalPatternReducer(),
        new BinaryPatternReducer(),
        new UnaryPatternReducer(),
        new ParenthesizedPatternReducer(),
        new RecursivePatternReducer(),
    ];

    public static PatternReduction Reduce(
        PatternSyntax pattern,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
    {
        foreach (var reducer in Reducers)
        {
            if (reducer.CanReduce(pattern, ctx))
                return reducer.Reduce(pattern, subject, subjectType, ctx);
        }

        ctx.Report(PatternDesugarCodes.UnsupportedPattern,
            ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
            $"PD010: pattern '{pattern.GetType().Name}' cannot be reduced by C03 (positional/list/slice patterns are supported by C37)",
            pattern);
        return new PatternReduction(null, Array.Empty<StatementSyntax>(), false);
    }

    internal static PatternReduction SupportedTrue() =>
        new(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            Array.Empty<StatementSyntax>(), true);

    internal static PatternReduction NotSupported() =>
        new(null, Array.Empty<StatementSyntax>(), false);

    internal static ExpressionSyntax NullLiteral() =>
        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

    internal static ExpressionSyntax NotNull(ExpressionSyntax subject) =>
        SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression,
            subject.WithoutTrivia(), NullLiteral());

    internal static ExpressionSyntax IsPattern(
        ExpressionSyntax subject, TypeSyntax type, VariableDesignationSyntax? designation)
    {
        PatternSyntax pattern = designation == null
            ? SyntaxFactory.TypePattern(type.WithoutTrivia())
            : SyntaxFactory.DeclarationPattern(type.WithoutTrivia(), designation);
        return SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(), pattern);
    }

    internal static bool RequiresTypeTest(ITypeSymbol? subjectType)
        => subjectType is { TypeKind: TypeKind.Dynamic or TypeKind.TypeParameter }
           || (subjectType is INamedTypeSymbol named && named.SpecialType == SpecialType.System_Object);

    internal static bool IsNullable(ITypeSymbol? subjectType)
        => subjectType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    internal static bool IsReferenceLike(ITypeSymbol? type)
        => type != null
           && (type.IsReferenceType || type.TypeKind == TypeKind.TypeParameter);

    internal static ITypeSymbol? GetMemberType(ITypeSymbol? type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers(name))
            {
                if (member is IPropertySymbol { Parameters.Length: 0 } property) return property.Type;
                if (member is IFieldSymbol field) return field.Type;
            }
        }
        return null;
    }

    /// <summary>`gov is CT v && <v compare c>` for object/dynamic/type-parameter subjects.</summary>
    internal static PatternReduction TypeTestThenCompare(
        ExpressionSyntax subject,
        ExpressionSyntax operand,
        SyntaxKind comparisonKind,
        ITypeSymbol? operandType,
        PatternReductionContext ctx)
    {
        if (operandType == null)
            return NotSupported();

        var bound = SyntaxFactory.Identifier(ctx.NewName("__cs2jPat"));
        var typeTest = SyntaxFactory.IsPatternExpression(
            subject.WithoutTrivia(),
            SyntaxFactory.DeclarationPattern(
                SyntaxFactory.ParseTypeName(operandType.ToDisplayString()),
                SyntaxFactory.SingleVariableDesignation(bound)));
        var comparison = SyntaxFactory.BinaryExpression(comparisonKind,
            SyntaxFactory.IdentifierName(bound), operand.WithoutTrivia());
        return new PatternReduction(
            SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, typeTest, comparison),
            Array.Empty<StatementSyntax>(), true);
    }
}

internal sealed class DiscardPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is DiscardPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
        => PatternReducer.SupportedTrue();
}

internal sealed class VarPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is VarPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var varPattern = (VarPatternSyntax)pattern;
        return varPattern.Designation switch
        {
            SingleVariableDesignationSyntax single => new PatternReduction(
                SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(),
                    SyntaxFactory.VarPattern(single.WithoutTrivia())),
                Array.Empty<StatementSyntax>(), true),
            _ => PatternReducer.SupportedTrue(),
        };
    }
}

internal sealed class ConstantPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is ConstantPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var constant = (ConstantPatternSyntax)pattern;

        if (constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            if (subjectType is INamedTypeSymbol { SpecialType: not SpecialType.System_Nullable_T } valueType
                && valueType.IsValueType)
            {
                ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                    ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    "PD010: null pattern on a non-nullable value-type governing is unreachable",
                    constant);
                return PatternReducer.NotSupported();
            }

            return new PatternReduction(
                SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                    subject.WithoutTrivia(), PatternReducer.NullLiteral()),
                Array.Empty<StatementSyntax>(), true);
        }

        if (PatternReducer.RequiresTypeTest(subjectType))
        {
            var constantType = ctx.Model?.GetTypeInfo(constant.Expression).Type;
            if (constantType == null)
            {
                ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                    ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    "PD010: constant pattern on object/dynamic governing needs a semantic model to pick the type-test form",
                    constant);
                return PatternReducer.NotSupported();
            }

            return PatternReducer.TypeTestThenCompare(
                subject, constant.Expression, SyntaxKind.EqualsExpression, constantType, ctx);
        }

        return new PatternReduction(
            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                subject.WithoutTrivia(), constant.Expression.WithoutTrivia()),
            Array.Empty<StatementSyntax>(), true);
    }
}

internal sealed class TypePatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is TypePatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var typePattern = (TypePatternSyntax)pattern;
        return new PatternReduction(
            PatternReducer.IsPattern(subject, typePattern.Type, null),
            Array.Empty<StatementSyntax>(), true);
    }
}

internal sealed class DeclarationPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is DeclarationPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var declaration = (DeclarationPatternSyntax)pattern;
        return new PatternReduction(
            PatternReducer.IsPattern(subject, declaration.Type, declaration.Designation),
            Array.Empty<StatementSyntax>(), true);
    }
}

internal sealed class RelationalPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is RelationalPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var relational = (RelationalPatternSyntax)pattern;
        var kind = relational.OperatorToken.Kind() switch
        {
            SyntaxKind.LessThanToken => SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanEqualsToken => SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanToken => SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanEqualsToken => SyntaxKind.GreaterThanOrEqualExpression,
            _ => SyntaxKind.None,
        };
        if (kind == SyntaxKind.None)
            return PatternReducer.NotSupported();

        ExpressionSyntax comparison;
        if (PatternReducer.IsNullable(subjectType))
        {
            // Lifted relational produces bool?, which cannot drive an if — guard with
            // != null and compare the .Value (mirrors the compiler's own lowering).
            var value = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                subject.WithoutTrivia(),
                SyntaxFactory.IdentifierName("Value"));
            comparison = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                PatternReducer.NotNull(subject),
                SyntaxFactory.BinaryExpression(kind, value, relational.Expression.WithoutTrivia()));
        }
        else if (PatternReducer.RequiresTypeTest(subjectType))
        {
            var operandType = ctx.Model?.GetTypeInfo(relational.Expression).Type;
            if (operandType == null)
                return PatternReducer.NotSupported();
            return PatternReducer.TypeTestThenCompare(
                subject, relational.Expression, kind, operandType, ctx);
        }
        else
        {
            comparison = SyntaxFactory.BinaryExpression(kind,
                subject.WithoutTrivia(), relational.Expression.WithoutTrivia());
        }

        return new PatternReduction(comparison, Array.Empty<StatementSyntax>(), true);
    }
}

internal sealed class BinaryPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is BinaryPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var binary = (BinaryPatternSyntax)pattern;
        var kind = binary.IsKind(SyntaxKind.AndPattern)
            ? SyntaxKind.LogicalAndExpression
            : SyntaxKind.LogicalOrExpression;
        var left = PatternReducer.Reduce(binary.Left, subject, subjectType, ctx);
        var right = PatternReducer.Reduce(binary.Right, subject, subjectType, ctx);
        if (!left.Supported || !right.Supported)
            return PatternReducer.NotSupported();
        return new PatternReduction(
            SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.BinaryExpression(kind, left.Condition!, right.Condition!)),
            Array.Empty<StatementSyntax>(), true);
    }
}

internal sealed class UnaryPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is UnaryPatternSyntax unary && unary.OperatorToken.IsKind(SyntaxKind.NotKeyword);

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var unary = (UnaryPatternSyntax)pattern;
        var inner = PatternReducer.Reduce(unary.Pattern, subject, subjectType, ctx);
        if (!inner.Supported)
            return PatternReducer.NotSupported();
        return new PatternReduction(
            SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(inner.Condition!)),
            Array.Empty<StatementSyntax>(), true);
    }
}

internal sealed class ParenthesizedPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is ParenthesizedPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
        => PatternReducer.Reduce(
            ((ParenthesizedPatternSyntax)pattern).Pattern, subject, subjectType, ctx);
}

internal sealed class RecursivePatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is RecursivePatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var recursive = (RecursivePatternSyntax)pattern;

        if (recursive.PositionalPatternClause != null)
        {
            ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                "PD010: positional patterns are reduced by C37", recursive);
            return PatternReducer.NotSupported();
        }

        var conditions = new List<ExpressionSyntax>();
        var baseExpression = subject.WithoutTrivia();

        if (recursive.Type != null)
        {
            var needsSyntheticBinding = recursive.Designation is not SingleVariableDesignationSyntax
                && recursive.PropertyPatternClause != null;
            var designation = needsSyntheticBinding
                ? SyntaxFactory.SingleVariableDesignation(
                    SyntaxFactory.Identifier(ctx.NewName("__cs2jPat")))
                : recursive.Designation;
            conditions.Add(PatternReducer.IsPattern(subject, recursive.Type, designation));
            if (designation is SingleVariableDesignationSyntax binding)
                baseExpression = SyntaxFactory.IdentifierName(binding.Identifier.WithoutTrivia());
        }
        else if (recursive.Designation is SingleVariableDesignationSyntax varBinding)
        {
            // Type-less recursive pattern with a designation: bind the subject itself.
            conditions.Add(SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(),
                SyntaxFactory.VarPattern(varBinding.WithoutTrivia())));
            baseExpression = SyntaxFactory.IdentifierName(varBinding.Identifier.WithoutTrivia());
        }
        else if (PatternReducer.IsReferenceLike(subjectType))
        {
            conditions.Add(PatternReducer.NotNull(subject));
        }

        if (recursive.PropertyPatternClause != null)
        {
            foreach (var sub in recursive.PropertyPatternClause.Subpatterns)
            {
                var memberName = sub.NameColon?.Name.Identifier.ValueText
                    ?? (sub.ExpressionColon?.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                if (memberName == null)
                {
                    ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                        ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                        "PD010: property subpattern without an explicit member name", sub);
                    return PatternReducer.NotSupported();
                }

                var memberType = PatternReducer.GetMemberType(subjectType, memberName);
                var memberExpression = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    baseExpression,
                    SyntaxFactory.IdentifierName(memberName));

                var subReduction = PatternReducer.Reduce(sub.Pattern, memberExpression, memberType, ctx);
                if (!subReduction.Supported)
                    return PatternReducer.NotSupported();

                // Reference-typed intermediate members must be null-guarded before use.
                if (PatternReducer.IsReferenceLike(memberType))
                    conditions.Add(PatternReducer.NotNull(memberExpression));
                conditions.Add(subReduction.Condition!);
            }
        }

        if (conditions.Count == 0)
            return PatternReducer.SupportedTrue();

        var joined = conditions.Aggregate((l, r) =>
            (ExpressionSyntax)SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, l, r));
        return new PatternReduction(
            SyntaxFactory.ParenthesizedExpression(joined), Array.Empty<StatementSyntax>(), true);
    }
}
