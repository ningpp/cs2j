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

/// <summary>
/// One hoisted binding: declared type syntax plus the two form flags. Casts into
/// an unconstrained type parameter must route through object — `(T)someException`
/// is CS0030, `(T)(object)someException` compiles for constrained and
/// unconstrained T alike.
/// </summary>
internal sealed record BindingInfo(TypeSyntax Type, bool SelfCompare, bool CastThroughObject);

/// <summary>Shared state for one reduction sweep (C03 and C37 use the same shape).</summary>
internal sealed record PatternReductionContext(
    SemanticModel? Model,
    PatternDesugarOptions Options,
    List<PatternDesugarDiagnostic> Diagnostics,
    string? FilePath,
    Func<string, string> NewName,
    LoweringScope? Scope = null,
    PatternDesugarPlanBuilder? Builder = null)
{
    /// <summary>
    /// Model lookup that never throws: synthesized (re-parented) nodes are
    /// detached from the semantic model, so GetTypeInfo would throw on them.
    /// </summary>
    public ITypeSymbol? SafeTypeOf(ExpressionSyntax expression)
    {
        if (Model == null)
            return null;
        try
        {
            return Model.GetTypeInfo(expression).Type;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Report(string code, PatternDesugarSeverity severity, string message, SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var where = span.IsValid
            ? $" ({FilePath ?? span.Path}:{span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})"
            : "";
        Diagnostics.Add(new PatternDesugarDiagnostic(code, severity, message + where, FilePath));
        if (code is PatternDesugarCodes.UnsupportedPattern or PatternDesugarCodes.GotoCaseInLoweredSwitch)
            Builder?.NoteReductionNodeError();
    }
}

/// <summary>
/// Binding scope for one lowered switch or merged catch chain.
///
/// C# pattern variables scope to the ENCLOSING BLOCK (empirically: no
/// redeclaration across if/else-if, no shadowing of outer locals — CS0128 /
/// CS0136), so `o is T v` conditions cannot be used in a desugared chain.
/// Bindings are instead HOISTED to plain declarations at the top of the
/// lowering block and ASSIGNED inside the condition:
///   T v;                                   (hoisted declaration)
///   gov is T && ((v = (T)gov) != null)     reference/type-parameter → != null
///   gov is T && ((v = (T)gov) == v)        value type → self-compare (always true)
/// Untyped designations ({ X: 1 } p, var x) and synthetic member bindings use
/// eager `var p = gov;` prologue statements instead (no cast, no type needed).
/// </summary>
internal sealed class LoweringScope
{
    /// <summary>name → (declared type syntax, use the self-compare form).</summary>
    public Dictionary<string, BindingInfo> AssignBindings { get; } = new(StringComparer.Ordinal);


    /// <summary>Names eagerly assigned via `var name = Subject;` before the chain.</summary>
    public List<string> EagerBindings { get; } = new();

    /// <summary>Synthetic member-access bindings created while trial-reducing recursive patterns.</summary>
    public Dictionary<RecursivePatternSyntax, string> SyntheticNames { get; } = new();

    /// <summary>Register (once) a synthetic binding for a type-ful recursive pattern.</summary>
    public string RegisterSynthetic(
        RecursivePatternSyntax pattern, TypeSyntax type, bool selfCompare, Func<string, string> newName)
    {
        if (SyntheticNames.TryGetValue(pattern, out var existing))
            return existing;
        var name = newName("__cs2jPat");
        SyntheticNames[pattern] = name;
        AssignBindings[name] = new BindingInfo(type, selfCompare, false);
        return name;
    }

    /// <summary>Register (once) a synthetic binding deduplicated by type text, for
    /// constant/relational patterns on object/dynamic governing.</summary>
    public string GetOrCreateSyntheticBinding(TypeSyntax type, Func<string, string> newName)
    {
        var existing = AssignBindings.FirstOrDefault(kv => kv.Value.Type.IsEquivalentTo(type)).Key;
        if (existing != null)
            return existing;
        var name = newName("__cs2jPat");
        AssignBindings[name] = new BindingInfo(type, SelfCompare: true, CastThroughObject: false);
        return name;
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
/// set is deliberately narrow: ==/!=, `is T`, hoisted-variable assignment,
/// &&/||, !, member access — everything the main converter already accepts.
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

    /// <summary>
    /// Reduces a bare constant value (`case 1:` labels and constant patterns).
    /// Kept separate from the PatternSyntax entry point so callers can pass an
    /// in-tree expression directly: wrapping it in a synthesized
    /// ConstantPatternSyntax re-parents the node and detaches it from the
    /// semantic model, which would break the object-governing type test.
    /// </summary>
    internal static PatternReduction ReduceConstant(
        ExpressionSyntax value,
        ExpressionSyntax subject,
        ITypeSymbol? subjectType,
        PatternReductionContext ctx)
        => ConstantPatternReducer.ReduceCore(value, subject, subjectType, ctx);

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

    internal static ExpressionSyntax IsTypeTest(ExpressionSyntax subject, TypeSyntax type) =>
        SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(), SyntaxFactory.TypePattern(type.WithoutTrivia()));

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

    /// <summary>
    /// The hoisted-binding condition form: `subject is T && ((name = (T)subject) (∗≠null | == name))`.
    /// The self-compare form is used for value types and type parameters (null
    /// comparison would not compile for them).
    /// </summary>
    internal static ExpressionSyntax AssignBindingCondition(
        ExpressionSyntax subject,
        TypeSyntax type,
        string name,
        bool selfCompare,
        bool castThroughObject = false)
    {
        var bound = SyntaxFactory.IdentifierName(name);
        var castOperand = castThroughObject
            ? (ExpressionSyntax)SyntaxFactory.CastExpression(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                subject.WithoutTrivia())
            : subject.WithoutTrivia();
        var assignment = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                bound,
                SyntaxFactory.CastExpression(type.WithoutTrivia(), castOperand)));
        var truth = selfCompare
            ? SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, assignment, bound)
            : SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression, assignment, NullLiteral());
        return SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
            IsTypeTest(subject, type), truth);
    }

    /// <summary>
    /// `subject is CT && ((__cs2jPat_n = (CT)subject) ∗ c)` for object/dynamic
    /// governing: a bare `gov == c` would box and compare by reference (silently
    /// always false). The assignment expression's own value feeds the comparison,
    /// so one conjunct binds and compares; the synthetic hoisted binding is
    /// deduplicated per type and declared at the lowering block top.
    /// </summary>
    internal static PatternReduction TypeTestThenCompare(
        ExpressionSyntax subject,
        ExpressionSyntax operand,
        SyntaxKind comparisonKind,
        ITypeSymbol? operandType,
        PatternReductionContext ctx)
    {
        if (operandType == null)
            return NotSupported();

        var typeSyntax = SyntaxFactory.ParseTypeName(operandType.ToDisplayString());
        var boundName = ctx.Scope != null
            ? ctx.Scope.GetOrCreateSyntheticBinding(typeSyntax, ctx.NewName)
            : ctx.NewName("__cs2jPat");

        // `(__cs2jPat_n = (CT)subject) <op> operand` — the cast applies to the
        // SUBJECT (the constant operand is what we compare against).
        var assignment = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(boundName),
                SyntaxFactory.CastExpression(typeSyntax, subject.WithoutTrivia())));
        var combined = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
            IsTypeTest(subject, typeSyntax),
            SyntaxFactory.BinaryExpression(comparisonKind, assignment, operand.WithoutTrivia()));

        return new PatternReduction(combined, Array.Empty<StatementSyntax>(), true);
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
        // `var x` always matches; the eager `var x = gov;` prologue statement is
        // emitted by the component (registered during analysis), so the condition
        // itself is plain `true`.
        return PatternReducer.SupportedTrue();
    }
}

internal sealed class ConstantPatternReducer : IPatternReducer
{
    public bool CanReduce(PatternSyntax pattern, PatternReductionContext ctx)
        => pattern is ConstantPatternSyntax;

    public PatternReduction Reduce(
        PatternSyntax pattern, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
        => ReduceCore(((ConstantPatternSyntax)pattern).Expression, subject, subjectType, ctx);

    internal static PatternReduction ReduceCore(
        ExpressionSyntax constantValue, ExpressionSyntax subject, ITypeSymbol? subjectType, PatternReductionContext ctx)
    {
        var constant = constantValue;

        if (constant.IsKind(SyntaxKind.NullLiteralExpression))
        {
            if (subjectType is INamedTypeSymbol { SpecialType: not SpecialType.System_Nullable_T } valueType
                && valueType.IsValueType)
            {
                ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                    ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    "PD010: null pattern on a non-nullable value-type governing is unreachable",
                    constantValue);
                return PatternReducer.NotSupported();
            }

            return new PatternReduction(
                SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                    subject.WithoutTrivia(), PatternReducer.NullLiteral()),
                Array.Empty<StatementSyntax>(), true);
        }

        if (PatternReducer.RequiresTypeTest(subjectType))
        {
            var constantType = ctx.SafeTypeOf(constant);
            if (constantType == null)
            {
                ctx.Report(PatternDesugarCodes.UnsupportedPattern,
                    ctx.Options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    "PD010: constant pattern on object/dynamic governing needs a semantic model to pick the type-test form",
                    constantValue);
                return PatternReducer.NotSupported();
            }

            return PatternReducer.TypeTestThenCompare(
                subject, constant, SyntaxKind.EqualsExpression, constantType, ctx);
        }

        return new PatternReduction(
            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                subject.WithoutTrivia(), constant.WithoutTrivia()),
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
            PatternReducer.IsTypeTest(subject, typePattern.Type),
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
        if (declaration.Designation is not SingleVariableDesignationSyntax single)
            return new PatternReduction(
                PatternReducer.IsTypeTest(subject, declaration.Type),
                Array.Empty<StatementSyntax>(), true);

        if (ctx.Scope == null
            || !ctx.Scope.AssignBindings.TryGetValue(single.Identifier.ValueText, out var binding))
        {
            // No lowering scope (bare reducer use): fall back to an is-pattern form.
            return new PatternReduction(
                SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(),
                    SyntaxFactory.DeclarationPattern(declaration.Type.WithoutTrivia(), single.WithoutTrivia())),
                Array.Empty<StatementSyntax>(), true);
        }

        return new PatternReduction(
            PatternReducer.AssignBindingCondition(subject, binding.Type, single.Identifier.ValueText, binding.SelfCompare),
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
            var operandType = ctx.SafeTypeOf(relational.Expression);
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
            if (recursive.Designation is SingleVariableDesignationSyntax userBinding)
            {
                if (ctx.Scope != null
                    && ctx.Scope.AssignBindings.TryGetValue(userBinding.Identifier.ValueText, out var binding))
                {
                    conditions.Add(PatternReducer.AssignBindingCondition(
                        subject, binding.Type, userBinding.Identifier.ValueText,
                        binding.SelfCompare, binding.CastThroughObject));
                    baseExpression = SyntaxFactory.IdentifierName(userBinding.Identifier.WithoutTrivia());
                }
                else
                {
                    conditions.Add(PatternReducer.IsTypeTest(subject, recursive.Type));
                    baseExpression = SyntaxFactory.IdentifierName(userBinding.Identifier.WithoutTrivia());
                }
            }
            else if (recursive.PropertyPatternClause != null)
            {
                if (ctx.Scope != null)
                {
                    // Member access needs a name — register (idempotently) a
                    // deterministic synthetic hoisted binding. The self-compare form
                    // compiles for value types and reference types alike.
                    var synthetic = ctx.Scope.RegisterSynthetic(
                        recursive, recursive.Type, selfCompare: true, ctx.NewName);
                    var binding = ctx.Scope.AssignBindings[synthetic];
                    conditions.Add(PatternReducer.AssignBindingCondition(
                        subject, binding.Type, synthetic, binding.SelfCompare, binding.CastThroughObject));
                    baseExpression = SyntaxFactory.IdentifierName(synthetic);
                }
                else
                {
                    conditions.Add(PatternReducer.IsTypeTest(subject, recursive.Type));
                }
            }
            else
            {
                conditions.Add(PatternReducer.IsTypeTest(subject, recursive.Type));
            }
        }
        else if (recursive.Designation is SingleVariableDesignationSyntax varBinding
                 && ctx.Scope != null
                 && ctx.Scope.EagerBindings.Contains(varBinding.Identifier.ValueText))
        {
            // Type-less designation: the eager `var p = gov;` prologue assigned the
            // subject already — no condition contribution, member access on p.
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
