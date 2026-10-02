using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// How a captured variable is written at one site.
/// </summary>
public enum CaptureWriteKind
{
    /// <summary>Left side of an assignment expression (declarator initializers excluded by construction).</summary>
    Assignment,

    /// <summary>Prefix/postfix ++ or -- operand.</summary>
    IncrementDecrement,

    /// <summary>Expression of a ref/out argument.</summary>
    RefOutArgument,
}

/// <summary>
/// One external local/parameter/range variable referenced inside a boundary body.
/// Extracted from the E02 local-function analyzer (design C32 §4.2): the single
/// capture-classification implementation for the whole tree.
/// </summary>
public sealed record CapturedSymbolInfo(
    ISymbol Symbol,
    string Name,
    ITypeSymbol Type,
    string TypeDisplayFullyQualified);

/// <summary>One write site of a captured symbol.</summary>
public sealed record CaptureWriteSite(
    IdentifierNameSyntax Identifier,
    CaptureWriteKind Kind);

/// <summary>
/// Stateless capture primitives shared by local-function-lift (E02) and
/// closure-lift (C32). Strategy (what triggers a lift, grouping, foreach
/// conservatism) belongs to each pass; these only answer
/// "which symbols are referenced from outside the boundary" and
/// "where is a symbol written".
/// </summary>
public static class CaptureClassification
{
    /// <summary>
    /// Enumerates distinct local/parameter/range-variable symbols referenced
    /// inside <paramref name="bodyRoot"/> but declared outside it.
    /// Shape extracted verbatim from LocalFunctionLiftAnalyzer.CollectCaptures:
    /// NameColon labels, the boundary's own parameters and symbols declared
    /// inside the body are skipped; ErrorType symbols are skipped.
    /// </summary>
    /// <param name="model">Semantic model for the tree.</param>
    /// <param name="bodyRoot">The boundary body (local function body, lambda body/expression).</param>
    /// <param name="boundaryMethod">
    /// The boundary's own method symbol (local function/lambda); parameters it
    /// owns are skipped. Pass null when no parameter exclusion is needed.
    /// </param>
    /// <param name="descendPredicate">
    /// Optional DescendantNodes predicate. E02 passes "do not descend into
    /// nested local functions"; C32 passes the same (nested local functions are
    /// a residual CS2J3204 boundary) while nested lambdas are descended.
    /// </param>
    public static IReadOnlyList<CapturedSymbolInfo> CollectCapturedSymbols(
        SemanticModel model,
        SyntaxNode bodyRoot,
        IMethodSymbol? boundaryMethod,
        Func<SyntaxNode, bool>? descendPredicate = null)
    {
        var captures = new List<CapturedSymbolInfo>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var identifier in bodyRoot
            .DescendantNodes(descendPredicate)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol is not (ILocalSymbol or IParameterSymbol or IRangeVariableSymbol))
                continue;

            // Skip references to the boundary's own parameters.
            if (boundaryMethod != null
                && SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, boundaryMethod))
                continue;

            // Skip symbols declared inside the boundary body itself
            // (lambda parameters, variables declared in nested blocks).
            if (IsDeclaredInside(symbol, bodyRoot))
                continue;

            if (!seen.Add(symbol))
                continue;

            var type = symbol switch
            {
                ILocalSymbol ls => ls.Type,
                IParameterSymbol ps => ps.Type,
                _ => null,
            };

            if (type == null || type.TypeKind == TypeKind.Error)
                continue;

            var typeDisplay = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            captures.Add(new CapturedSymbolInfo(symbol, symbol.Name, type, typeDisplay));
        }

        return captures;
    }

    /// <summary>
    /// Enumerates every write site of <paramref name="symbol"/> inside
    /// <paramref name="root"/>: assignment left values (declarator
    /// initializers are EqualsValueClause, not assignments, and therefore
    /// never matched), prefix/postfix ++/-- operands, and ref/out arguments.
    /// Shape extracted verbatim from LocalFunctionLiftAnalyzer.IsCapturedVariableWritten.
    /// </summary>
    public static IReadOnlyList<CaptureWriteSite> FindWriteSites(
        SemanticModel model,
        ISymbol symbol,
        SyntaxNode root,
        Func<SyntaxNode, bool>? descendPredicate = null)
    {
        var sites = new List<CaptureWriteSite>();

        foreach (var identifier in root
            .DescendantNodes(descendPredicate)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            var resolved = model.GetSymbolInfo(identifier).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(resolved, symbol))
                continue;

            var parent = identifier.Parent;
            if (parent is AssignmentExpressionSyntax assignment
                && assignment.Left == identifier)
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.Assignment));
                continue;
            }

            if (parent is PrefixUnaryExpressionSyntax prefix
                && prefix.Operand == identifier
                && (prefix.IsKind(SyntaxKind.PreIncrementExpression)
                    || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.IncrementDecrement));
                continue;
            }

            if (parent is PostfixUnaryExpressionSyntax postfix
                && postfix.Operand == identifier
                && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
                    || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.IncrementDecrement));
                continue;
            }

            if (parent is ArgumentSyntax argument
                && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
            {
                sites.Add(new CaptureWriteSite(identifier, CaptureWriteKind.RefOutArgument));
            }
        }

        return sites;
    }

    /// <summary>
    /// True when <paramref name="node"/> (normally an identifier) is lexically
    /// inside a <c>nameof(...)</c> invocation. Such references are string
    /// literals, never variable accesses. Extracted from E02.
    /// </summary>
    public static bool IsInsideNameOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == "nameof")
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// True for capture kinds that cannot be spelled in generated C# source:
    /// anonymous types (unspeakable <c>&lt;anonymous&gt;</c> name), pointer
    /// types (baseline compile error when lambda-captured), ref struct prefix.
    /// Extracted verbatim from E02 IsUnsupportedCaptureType.
    /// </summary>
    public static bool IsUnsupportedCaptureKind(string? typeDisplay)
    {
        if (typeDisplay == null)
            return false;
        if (typeDisplay.Contains("<anonymous"))
            return true;
        if (typeDisplay.Contains("*"))
            return true;
        if (typeDisplay.StartsWith("ref "))
            return true;
        return false;
    }

    private static bool IsDeclaredInside(ISymbol symbol, SyntaxNode body)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var location = syntaxRef.GetSyntax().GetLocation();
            if (body.SpanStart <= location.SourceSpan.Start
                && location.SourceSpan.Start < body.Span.End)
            {
                return true;
            }
        }
        return false;
    }
}
