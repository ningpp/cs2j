using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBOp = Microsoft.CodeAnalysis.Operations;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 if-condition conjunction linearization (CS2J5355 eradication for the
/// SDK json shapes <c>else if (NumberHandling.HasValue &amp;&amp; NumberHandling !=
/// JsonNumberHandling.Strict)</c> / <c>if (!isPolymorphic.Value &amp;&amp;
/// typeToGenerate.Mode == JsonSourceGenerationMode.Serialization)</c>).
/// <para>
/// A lifted site in the right arm of <c>&amp;&amp;</c> cannot be hoisted — the
/// temporary would be evaluated even when the left arm short-circuits. Split
/// the conjunction before the first arm that needs a hoist:
/// </para>
/// <code>
/// if (A &amp;&amp; B &amp;&amp; C) body
///     →  if (A) { if (B &amp;&amp; C) body }
/// if (A &amp;&amp; B) body else otherwise
///     →  bool __cs2j_n_scN = false;
///        if (A) { __cs2j_n_scN = B; }        // B's temps are spliced here
///        if (__cs2j_n_scN) body else otherwise;
/// </code>
/// The split keeps the source short-circuit evaluation order and frequency:
/// <c>A</c> is always evaluated, the tail only when <c>A</c> holds, and
/// <c>body</c>/<c>otherwise</c> run exactly as before (the flag captures the
/// tail result so the else clause stays on the outer statement). The tail's
/// leftmost component sits in an always-evaluated slot of the inner anchor, so
/// Pass1 can hoist its unstable operands. The boolean-true literal lane
/// (<c>bool? == true</c>/<c>!= true</c>) needs no temporary and is skipped.
/// </summary>
internal static class NullableIfConjunctionLinearizer
{
    public static CompilationUnitSyntax Linearize(
        CompilationUnitSyntax root,
        SemanticModel model,
        NullableLiftAnalyzer analyzer,
        out bool changed)
    {
        changed = false;

        var candidates = new List<(IfStatementSyntax If, List<ExpressionSyntax> Components, int Split)>();
        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            var components = FlattenConjunction(ifStatement.Condition);
            if (components.Count < 2)
            {
                continue;
            }

            var split = FindFirstHoistComponent(components, model, analyzer);
            if (split < 1)
            {
                continue;
            }

            candidates.Add((ifStatement, components, split));
        }

        if (candidates.Count == 0)
        {
            return root;
        }

        // Replace outermost statements only: a nested if inside a replaced
        // statement rides along in the replacement's body, so replacing both
        // would drop the outer rewrite.
        var replacements = new List<(IfStatementSyntax If, StatementSyntax Replacement)>();
        var names = new NullableLiftSyntaxFacts.TempNameAllocator(root);
        var sequence = 0;
        foreach (var candidate in candidates.OrderBy(item => item.If.SpanStart))
        {
            if (replacements.Any(item => item.If.Span.Contains(candidate.If.Span)))
            {
                continue;
            }

            replacements.Add((
                candidate.If,
                BuildReplacement(candidate.If, candidate.Components, candidate.Split, names, ++sequence)));
            changed = true;
        }

        if (!changed)
        {
            return root;
        }

        var map = replacements.ToDictionary(item => item.If, item => item.Replacement);
        return (CompilationUnitSyntax)root.ReplaceNodes(
            map.Keys, (original, _) => map[original]);
    }

    /// <summary>
    /// Flatten a left-associative <c>&amp;&amp;</c> chain into evaluation order
    /// (outer parens stripped). Returns a single-element list for a
    /// non-conjunction expression.
    /// </summary>
    private static List<ExpressionSyntax> FlattenConjunction(ExpressionSyntax condition)
    {
        var components = new List<ExpressionSyntax>();
        Collect(condition);
        return components;

        void Collect(ExpressionSyntax expression)
        {
            var unwrapped = NullableLiftSyntaxFacts.UnwrapParens(expression);
            if (unwrapped is BinaryExpressionSyntax binary
                && binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                Collect(binary.Left);
                Collect(binary.Right);
            }
            else
            {
                components.Add(expression);
            }
        }
    }

    /// <summary>
    /// Index of the first conjunction component (beyond the always-evaluated
    /// first one) that contains a built-in lifted site whose unstable operand
    /// would need a hoisted temporary; -1 when none does. Nested lambdas and
    /// local functions are opaque (their sites belong to their own scope).
    /// </summary>
    private static int FindFirstHoistComponent(
        IReadOnlyList<ExpressionSyntax> components,
        SemanticModel model,
        NullableLiftAnalyzer analyzer)
    {
        for (var i = 1; i < components.Count; i++)
        {
            if (ComponentNeedsHoist(components[i], model, analyzer))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool ComponentNeedsHoist(
        ExpressionSyntax component,
        SemanticModel model,
        NullableLiftAnalyzer analyzer)
    {
        foreach (var node in component.DescendantNodesAndSelf(
                     node => node is not AnonymousFunctionExpressionSyntax
                         and not LocalFunctionStatementSyntax))
        {
            switch (model.GetOperation(node))
            {
                case NBOp.IBinaryOperation binary
                    when binary.IsLifted
                        && NullableLiftAnalyzer.IsBuiltInLiftedOperator(binary.OperatorMethod):
                {
                    if (node is not BinaryExpressionSyntax syntax
                        || analyzer.ClassifyBinary(syntax) is not { } classification
                        || classification.Domain
                            is NullableValueDomain.Other
                            or NullableValueDomain.NotNullable
                            or NullableValueDomain.SpecialNumeric
                        || classification.Flavor == LiftedBinaryFlavor.UserDefined
                        || NullableLiftAnalyzer.IsBooleanTrueLiteralComparison(syntax, classification))
                    {
                        break;
                    }

                    if (OperandNeedsHoist(syntax.Left, binary.LeftOperand, analyzer)
                        || OperandNeedsHoist(syntax.Right, binary.RightOperand, analyzer))
                    {
                        return true;
                    }

                    break;
                }

                case NBOp.IUnaryOperation unary
                    when unary.IsLifted
                        && NullableLiftAnalyzer.IsBuiltInLiftedOperator(unary.OperatorMethod):
                {
                    if (node is not PrefixUnaryExpressionSyntax syntax
                        || analyzer.ClassifyUnary(syntax) is not { } classification
                        || classification.Domain
                            is NullableValueDomain.Other
                            or NullableValueDomain.NotNullable
                            or NullableValueDomain.SpecialNumeric
                        || classification.Flavor == LiftedUnaryFlavor.UserDefined)
                    {
                        break;
                    }

                    if (unary.Operand is { } operand
                        && !NullableLiftAnalyzer.IsNullConstant(operand)
                        && !analyzer.IsStableReference(syntax.Operand))
                    {
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    private static bool OperandNeedsHoist(
        ExpressionSyntax syntax, IOperation? operation, NullableLiftAnalyzer analyzer) =>
        !NullableLiftAnalyzer.IsNullConstant(operation) && !analyzer.IsStableReference(syntax);

    private static StatementSyntax BuildReplacement(
        IfStatementSyntax ifStatement,
        IReadOnlyList<ExpressionSyntax> components,
        int splitIndex,
        NullableLiftSyntaxFacts.TempNameAllocator names,
        int sequence)
    {
        var prefix = components[0];
        for (var i = 1; i < splitIndex; i++)
        {
            prefix = BinaryExpression(SyntaxKind.LogicalAndExpression, prefix, components[i]);
        }

        var tail = components[splitIndex];
        for (var i = splitIndex + 1; i < components.Count; i++)
        {
            tail = BinaryExpression(SyntaxKind.LogicalAndExpression, tail, components[i]);
        }

        StatementSyntax replacement;
        if (ifStatement.Else == null)
        {
            var inner = IfStatement(
                tail.WithoutTrivia(),
                ifStatement.Statement.WithoutLeadingTrivia());
            replacement = IfStatement(prefix.WithoutTrivia(), Block(inner));
        }
        else
        {
            var flagName = names.Allocate("sc", sequence);
            // The synthesized `bool` keyword needs an explicit trailing space:
            // without a token separator a raw round-trip glues it into the
            // identifier (`bool__cs2j_n_sc1`), which is lexically legal and
            // therefore survives the reparse gate before failing to bind.
            var declaration = LocalDeclarationStatement(
                VariableDeclaration(
                    PredefinedType(Token(SyntaxKind.BoolKeyword).WithTrailingTrivia(Space)),
                    SingletonSeparatedList(
                        VariableDeclarator(flagName).WithInitializer(
                            EqualsValueClause(
                                LiteralExpression(SyntaxKind.FalseLiteralExpression))))));
            var assign = ExpressionStatement(
                AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(flagName),
                    Token(SyntaxKind.EqualsToken),
                    tail.WithoutTrivia()));
            replacement = Block(
                declaration,
                IfStatement(prefix.WithoutTrivia(), Block(assign)),
                IfStatement(IdentifierName(flagName), ifStatement.Statement, ifStatement.Else));
        }

        return replacement
            .WithLeadingTrivia(ifStatement.GetLeadingTrivia())
            .WithTrailingTrivia(ifStatement.GetTrailingTrivia())
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind));
    }
}
