using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// Syntax/semantic facts for C35 (design §2/§3.5/§7.5): the inline whitelist,
/// segment-temp naming with collision avoidance, ref-context / E03-composition
/// predicates, residual node kinds and explained-annotation pairing.
/// All predicates stay local to this directory; shared family facts are reused
/// read-only.
/// </summary>
internal static class NullConditionalSyntaxFacts
{
    /// <summary>
    /// True when the receiver is on the single-segment inline whitelist
    /// (design §2 NC-VALUE): locals, parameters, fields and <c>this</c>.
    /// Properties are excluded (a repeated property read may run a getter twice).
    /// </summary>
    public static bool IsInlineWhitelistReceiver(
        ExpressionSyntax receiver, SemanticModel? model)
    {
        if (model == null)
            return false;

        switch (receiver)
        {
            case ThisExpressionSyntax:
                return true;

            case IdentifierNameSyntax:
                break;

            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }:
                break;

            default:
                return false;
        }

        var symbol = model.GetSymbolInfo(receiver).Symbol;
        return symbol is ILocalSymbol
            or IParameterSymbol
            or IFieldSymbol;
    }

    /// <summary>
    /// True when every argument expression is side-effect-safe for a zero-temp
    /// inline shape in an anchor-free context (design §3.5: 接收者与全部实参均在
    /// 简单白名单). Safe = literals, <c>typeof</c>, simple identifiers binding
    /// locals/parameters/fields/consts, or whitelist-member access. Invocations,
    /// object creations and index accesses are unsafe.
    /// </summary>
    public static bool AreArgumentsInlineSafe(
        IEnumerable<ArgumentSyntax>? arguments, SemanticModel? model)
    {
        if (arguments == null)
            return true;

        foreach (var argument in arguments)
        {
            if (!IsInlineSafeExpression(argument.Expression, model))
                return false;
        }

        return true;
    }

    private static bool IsInlineSafeExpression(ExpressionSyntax expression, SemanticModel? model)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax:
            case TypeOfExpressionSyntax:
            case DefaultExpressionSyntax:
                return true;

            case IdentifierNameSyntax:
            {
                var symbol = model?.GetSymbolInfo(expression).Symbol;
                return symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol;
            }

            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }:
            {
                var symbol = model?.GetSymbolInfo(expression).Symbol;
                return symbol is IFieldSymbol;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// True when the chain root sits in a ref/out argument or ref expression
    /// (design §2: CS2J0560, defensive mirror of the baseline compile error).
    /// </summary>
    public static bool IsInRefContext(ConditionalAccessExpressionSyntax root)
    {
        if (root.Ancestors().Any(a => a is RefExpressionSyntax))
            return true;

        return root.Parent is ArgumentSyntax arg
            && arg.RefKindKeyword.Kind() != SyntaxKind.None;
    }

    /// <summary>
    /// True when the chain is the initializer of an E03 <c>___tN</c> temp
    /// (NC-6; E03 naming is <c>___tN</c>). Counting-only heuristic; correctness
    /// never depends on it.
    /// </summary>
    public static bool IsE03TempInitializer(ConditionalAccessExpressionSyntax root)
    {
        var current = root.Parent;
        while (current is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            current = current.Parent;

        return current is EqualsValueClauseSyntax
        {
            Parent: VariableDeclaratorSyntax
            {
                Identifier.Text: var name,
                Parent: VariableDeclarationSyntax
                {
                    Parent: LocalDeclarationStatementSyntax,
                },
            },
        } && name.StartsWith("___t", StringComparison.Ordinal);
    }

    /// <summary>True for the three residual node kinds NC-12 tracks.</summary>
    public static bool IsResidualNode(SyntaxNode node)
        => node is ConditionalAccessExpressionSyntax
            or ElementBindingExpressionSyntax
            or MemberBindingExpressionSyntax;

    /// <summary>True when node itself or any ancestor is accounted for (explained).</summary>
    public static bool IsExplained(SyntaxNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current.HasAnnotation(ExpressionDesugarSyntaxFacts.ExplainedAnnotation)
                || current.HasAnnotation(ExpressionDesugarSyntaxFacts.ExplainedSubtreeAnnotation))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    /// <summary>
    /// Finds the leftmost root placeholder binding of a binding-rooted
    /// expression: walks member/element access, invocation and parentheses
    /// chains down to a <see cref="MemberBindingExpressionSyntax"/> or
    /// <see cref="ElementBindingExpressionSyntax"/>.
    /// </summary>
    public static ExpressionSyntax? FindRootBinding(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case MemberBindingExpressionSyntax:
                case ElementBindingExpressionSyntax:
                    return current;

                case MemberAccessExpressionSyntax memberAccess:
                    current = memberAccess.Expression;
                    break;

                case ElementAccessExpressionSyntax elementAccess:
                    current = elementAccess.Expression;
                    break;

                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    break;

                default:
                    return null;
            }
        }
    }
}

/// <summary>
/// Per-file segment-temp name allocator (<c>__ed_sN</c>, design §4.2).
/// Existing local declarations seed the used-name set so user identifiers
/// are never collided; one allocator instance per file is shared by every
/// chain plan.
/// </summary>
internal sealed class NullConditionalTempNameAllocator
{
    private readonly HashSet<string> _used;
    private int _counter;

    public NullConditionalTempNameAllocator(CompilationUnitSyntax root)
    {
        _used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var local in root.DescendantNodes()
                     .OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in local.Declaration.Variables)
                _used.Add(variable.Identifier.Text);
        }
    }

    /// <summary>Allocates a fresh <c>__ed_sN</c> name absent from the file.</summary>
    public string Allocate()
    {
        while (true)
        {
            var name = $"__ed_s{_counter}";
            _counter++;
            if (_used.Add(name))
                return name;
        }
    }
}
