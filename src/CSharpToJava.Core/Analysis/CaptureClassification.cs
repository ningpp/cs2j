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
/// An outer <c>const</c> local referenced inside a boundary body. A constant is
/// not state: C# closures/iterators do not capture it, and const occurrences
/// (constant patterns, parameter defaults, attribute arguments, goto case,
/// switch labels) require a compile-time constant. Callers that move a body out
/// of its declaring method must inline <see cref="ConstantValue"/> instead of
/// generating a parameter.
/// </summary>
public sealed record ReferencedConstantInfo(
    ILocalSymbol Symbol,
    string Name,
    ITypeSymbol Type,
    object? ConstantValue);

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
    /// inside the body are skipped; ErrorType symbols are skipped (and can be
    /// reported through <paramref name="unresolvedSymbols"/>).
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
    /// <param name="unresolvedSymbols">
    /// Optional sink receiving captured symbols whose type is an ErrorType.
    /// They cannot be spelled as generated parameter types; callers that lift
    /// code must retain the original boundary instead of emitting a reference
    /// to an undeclared name (CS0103).
    /// </param>
    public static IReadOnlyList<CapturedSymbolInfo> CollectCapturedSymbols(
        SemanticModel model,
        SyntaxNode bodyRoot,
        IMethodSymbol? boundaryMethod,
        Func<SyntaxNode, bool>? descendPredicate = null,
        ICollection<ISymbol>? unresolvedSymbols = null)
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

            // const 局部是编译期常量，不是捕获：C# 的闭包/迭代器都不捕获常量。
            // 常量语境（is 模式、参数默认值、特性实参、goto case、switch 标签）
            // 必须保持编译期常量，改写成捕获形参会 CS0103/CS0150。需要拿到值的
            // 调用方改用 <see cref="CollectReferencedConstants"/> 按值内联。
            if (symbol is ILocalSymbol { HasConstantValue: true })
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

            if (type == null)
                continue;

            if (type.TypeKind == TypeKind.Error)
            {
                unresolvedSymbols?.Add(symbol);
                continue;
            }

            var typeDisplay = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            captures.Add(new CapturedSymbolInfo(symbol, symbol.Name, type, typeDisplay));
        }

        return captures;
    }

    /// <summary>
    /// Enumerates distinct outer <c>const</c> locals referenced under
    /// <paramref name="root"/>. Callers that need constant contexts in a local
    /// function's signature (parameter default values, attribute arguments) pass
    /// that syntax root separately. Constants declared inside
    /// <paramref name="root"/> itself are skipped, mirroring
    /// <see cref="CollectCapturedSymbols"/>.
    /// </summary>
    public static IReadOnlyList<ReferencedConstantInfo> CollectReferencedConstants(
        SemanticModel model,
        SyntaxNode root,
        IMethodSymbol? boundaryMethod,
        Func<SyntaxNode, bool>? descendPredicate = null)
    {
        var constants = new List<ReferencedConstantInfo>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var identifier in root
            .DescendantNodes(descendPredicate)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            if (model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol
                { HasConstantValue: true } local)
                continue;

            if (boundaryMethod != null
                && SymbolEqualityComparer.Default.Equals(local.ContainingSymbol, boundaryMethod))
                continue;

            if (IsDeclaredInside(local, root))
                continue;

            if (!seen.Add(local))
                continue;

            constants.Add(new ReferencedConstantInfo(
                local, local.Name, local.Type, local.ConstantValue));
        }

        return constants;
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
        if (typeDisplay.StartsWith("ref ", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="symbol"/> is declared lexically inside <paramref name="body"/>.
    /// Shared by capture discovery and propagation: a nested local function's capture that is
    /// the caller's own local must stay a direct reference at the caller's call site, not become
    /// a capture of the caller's hoisted method.
    /// </summary>
    internal static bool IsDeclaredInside(ISymbol symbol, SyntaxNode body)
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

    /// <summary>
    /// D4 (C45 M3): true when a NESTED lambda inside <paramref name="lambda"/>
    /// references a parameter of this lambda or a local/range variable
    /// declared directly in this lambda's body (not inside a deeper nested
    /// lambda). Such state exists only while this lambda is invoked, so the
    /// nested lambda cannot be hosted by a static host method or a
    /// routine-scoped display instance — its display class must hang off this
    /// lambda's display class.
    /// </summary>
    public static bool OwnsStateForNestedLambda(
        SemanticModel model,
        LambdaExpressionSyntax lambda)
    {
        var lambdaSymbol = model.GetSymbolInfo(lambda).Symbol as IMethodSymbol;
        SyntaxNode? bodyRoot = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => (SyntaxNode?)p.Block ?? p.ExpressionBody,
            SimpleLambdaExpressionSyntax s => (SyntaxNode?)s.Block ?? s.ExpressionBody,
            _ => null,
        };
        if (bodyRoot == null)
            return false;

        var descend = (Func<SyntaxNode, bool>?)(static d => d is not LocalFunctionStatementSyntax);

        foreach (var identifier in lambda.DescendantNodes(descend)
                     .OfType<IdentifierNameSyntax>())
        {
            // The use site must be inside a nested lambda, not this lambda's
            // own direct body (its own parameter/local use is ordinary).
            var enclosingLambda = identifier.Ancestors()
                .OfType<LambdaExpressionSyntax>()
                .FirstOrDefault();
            if (enclosingLambda == null || enclosingLambda == lambda)
                continue;
            if (identifier.Parent is NameColonSyntax || IsInsideNameOf(identifier))
                continue;

            var symbol = model.GetSymbolInfo(identifier).Symbol;

            if (symbol is IParameterSymbol
                && lambdaSymbol != null
                && SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, lambdaSymbol))
            {
                return true;
            }

            if (symbol is ILocalSymbol or IRangeVariableSymbol)
            {
                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    var declaring = reference.GetSyntax();
                    if (declaring.SpanStart < bodyRoot.SpanStart || declaring.SpanStart >= bodyRoot.Span.End)
                        continue;

                    // Declared inside a deeper nested lambda → that lambda's
                    // own state, not ours (span containment alone is too broad).
                    var ownerLambda = declaring.Ancestors()
                        .OfType<LambdaExpressionSyntax>()
                        .FirstOrDefault();
                    if (ownerLambda == lambda)
                        return true;
                }
            }
        }

        return false;
    }
}
