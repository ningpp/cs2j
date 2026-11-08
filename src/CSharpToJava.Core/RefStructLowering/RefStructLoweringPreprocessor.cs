using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefStructLowering;

/// <summary>
/// Independent post-pass <c>ref-struct-lowering</c> (pass stage Declarative,
/// runs right after <c>stackalloc-lowering</c>). It scans the whole file with
/// its <see cref="SemanticModel"/> and removes the <c>ref</c> modifier from user
/// <c>ref struct</c> declarations that provably have no ref-only members,
/// turning them into ordinary value types Java can consume.
/// <para>
/// Removing <c>ref</c> loses the only C# carrier of <see cref="Type.IsByRefLike"/>.
/// The pass therefore rewrites the declaration in one atomic step:
/// <list type="bullet">
/// <item>the declaration implements <c>Cs2jPrelude.Cs2jByRefLikeMarker</c>,
/// an explicit marker that both sides can query;</item>
/// <item>every <c>System.Type.IsByRefLike</c> property read in the tree becomes
/// <c>Cs2jPrelude.Cs2jByRefLikeHelper.IsByRefLike(expr)</c>, so reflection-based
/// invalid-type checks in the C# oracle keep their behaviour.</item>
/// </list>
/// The Java converter sees only an ordinary struct, an ordinary interface and
/// an ordinary static call — no <c>ref struct</c> and no special casing. The
/// compat side maps the marker/helper to <c>TypeHelper.getIsByRefLike</c>.
/// </para>
/// <para>
/// A declaration keeps its <c>ref</c> modifier (and reports RSL001) as soon as
/// any conservative signal is present: a pointer/function-pointer typed
/// field/property, a <c>ref</c>/<c>ref readonly</c> field, a ref-returning
/// member, a field/property typed as a ref struct (Span/ReadOnlySpan/Memory or
/// any <see cref="INamedTypeSymbol.IsRefLikeType"/> type, including a type
/// parameter constrained with <c>allows ref struct</c>), or a nested ref struct
/// type. A <c>typeof(...)</c> use is <b>not</b> a preservation signal: the
/// byref-like-ness it observes is carried by the marker instead.
/// </para>
/// Clean files are returned byte-for-byte and re-running the pass on its own
/// output is a no-op (idempotent).
/// </summary>
public sealed class RefStructLoweringPreprocessor
{
    private const string MarkerTypeName = "global::Cs2jPrelude.Cs2jByRefLikeMarker";
    private const string HelperTypeName = "global::Cs2jPrelude.Cs2jByRefLikeHelper";

    public RefStructLoweringResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new RefStructLoweringStatistics();
        var diagnostics = new List<RefStructLoweringDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        var rewriter = new LoweringRewriter(semanticModel);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        foreach (var (declaration, reason) in rewriter.Preserved)
        {
            statistics.RefStructsPreserved++;
            diagnostics.Add(CreatePreservedDiagnostic(declaration, reason, filePath));
        }

        statistics.RefStructsLowered = rewriter.LoweredCount;
        statistics.IsByRefLikeSitesRewritten = rewriter.RewrittenSites;

        if (rewriter.LoweredCount == 0 && rewriter.RewrittenSites == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = newRoot.ToFullString();
        if (!ParsesWithoutErrors(outputCode))
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new RefStructLoweringDiagnostic(
                RefStructLoweringSeverity.Warning,
                RefStructLoweringDiagnosticCode.RSL010,
                "ref-struct-lowering rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new RefStructLoweringResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Conservative ref-only analysis. Returns a human-readable reason when the
    /// declaration must keep its <c>ref</c> modifier; null when it is safe to
    /// downgrade to an ordinary struct.
    /// </summary>
    private static string? FindRefOnlySignal(
        StructDeclarationSyntax declaration, SemanticModel model)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
        {
            return "the struct symbol could not be resolved";
        }

        foreach (var nested in symbol.GetTypeMembers())
        {
            if (nested.IsRefLikeType)
            {
                return $"nested ref struct type '{nested.Name}'";
            }
        }

        foreach (var member in symbol.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field:
                    if (field.RefKind != RefKind.None)
                    {
                        return $"ref field '{field.Name}'";
                    }

                    if (IsRefOnlyType(field.Type, out var fieldType))
                    {
                        return $"field '{field.Name}' has ref-only type '{fieldType}'";
                    }

                    break;

                case IPropertySymbol property:
                    if (property.RefKind != RefKind.None)
                    {
                        return $"ref-returning property '{property.Name}'";
                    }

                    if (IsRefOnlyType(property.Type, out var propertyType))
                    {
                        return $"property '{property.Name}' has ref-only type '{propertyType}'";
                    }

                    break;

                case IMethodSymbol method:
                    if (method.MethodKind == MethodKind.Ordinary
                        && method.RefKind != RefKind.None)
                    {
                        return $"ref-returning member '{method.Name}'";
                    }

                    break;
            }
        }

        return null;
    }

    private static bool IsRefOnlyType(ITypeSymbol type, out string displayName)
    {
        displayName = type.ToDisplayString();
        switch (type)
        {
            case IPointerTypeSymbol:
                return true;

            case IFunctionPointerTypeSymbol:
                return true;

            // `T` constrained with `allows ref struct` may hold a ref struct.
            case ITypeParameterSymbol { AllowsRefLikeType: true }:
                return true;

            case INamedTypeSymbol named:
                if (named.IsRefLikeType)
                {
                    return true;
                }

                var definition = named.OriginalDefinition.ToDisplayString();
                return definition is "System.Span<T>"
                    or "System.ReadOnlySpan<T>"
                    or "System.Memory<T>";
        }

        return false;
    }

    /// <summary>
    /// Remove only the <c>ref</c> modifier token and append the byref-like marker
    /// interface. When <c>ref</c> is the first modifier its leading trivia
    /// (indentation) moves to the struct keyword so the declaration keeps its
    /// layout; otherwise the preceding modifier's trailing trivia already
    /// separates the tokens.
    /// </summary>
    private static StructDeclarationSyntax LowerRefStruct(
        StructDeclarationSyntax declaration)
    {
        var refToken = declaration.Modifiers.First(m => m.IsKind(SyntaxKind.RefKeyword));
        var isFirst = declaration.Modifiers[0] == refToken;
        var modifiers = declaration.Modifiers.Remove(refToken);

        var keyword = declaration.Keyword;
        if (isFirst)
        {
            keyword = keyword.WithLeadingTrivia(
                refToken.LeadingTrivia.Concat(keyword.LeadingTrivia));
        }

        var withoutRef = declaration.WithModifiers(modifiers).WithKeyword(keyword);
        var marker = SyntaxFactory.SimpleBaseType(
            SyntaxFactory.ParseTypeName(MarkerTypeName));

        BaseListSyntax baseList;
        if (withoutRef.BaseList is { } existing)
        {
            // C# 13 allows a ref struct to implement interfaces; append rather
            // than replace so those interfaces survive the downgrade.
            baseList = existing.AddTypes(
                marker.WithLeadingTrivia(SyntaxFactory.Space));
        }
        else
        {
            // The identifier normally already carries a trailing space from
            // `ref struct R { }`; only add a leading one when it does not, so
            // the emitted base list never gets a doubled space.
            var colon = SyntaxFactory.Token(SyntaxKind.ColonToken)
                .WithTrailingTrivia(SyntaxFactory.Space);
            if (!withoutRef.Identifier.HasTrailingTrivia)
            {
                colon = colon.WithLeadingTrivia(SyntaxFactory.Space);
            }

            baseList = SyntaxFactory.BaseList(
                    colon,
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(marker))
                .WithTrailingTrivia(SyntaxFactory.Space);
        }

        return withoutRef.WithBaseList(baseList);
    }

    private sealed class LoweringRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;

        public LoweringRewriter(SemanticModel model)
            : base(visitIntoStructuredTrivia: false)
            => _model = model;

        public int LoweredCount { get; private set; }

        public int RewrittenSites { get; private set; }

        public List<(StructDeclarationSyntax Declaration, string Reason)> Preserved { get; } = new();

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        {
            // Visit children first so a nested `Type.IsByRefLike` read inside a
            // lowered struct is rewritten before the declaration itself changes.
            var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
            if (!node.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            {
                return visited;
            }

            // Symbol queries must use the ORIGINAL node: the semantic model
            // belongs to the input tree, not to `visited`.
            var reason = FindRefOnlySignal(node, _model);
            if (reason != null)
            {
                Preserved.Add((node, reason));
                return visited;
            }

            LoweredCount++;
            return LowerRefStruct(visited);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Name.Identifier.ValueText == "IsByRefLike"
                && _model.GetSymbolInfo(node).Symbol is IPropertySymbol
                {
                    Name: "IsByRefLike",
                } property
                && property.ContainingType?.ToDisplayString() == "System.Type")
            {
                RewrittenSites++;
                return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.ParseExpression(HelperTypeName + ".IsByRefLike"),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(node.Expression.WithoutTrivia()))))
                    .WithTriviaFrom(node);
            }

            return base.VisitMemberAccessExpression(node);
        }
    }

    private static RefStructLoweringDiagnostic CreatePreservedDiagnostic(
        StructDeclarationSyntax declaration,
        string reason,
        string filePath)
    {
        var line = 1;
        var column = 1;
        try
        {
            var span = declaration.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // Location unavailable: fall back to (1,1).
        }

        return new RefStructLoweringDiagnostic(
            RefStructLoweringSeverity.Warning,
            RefStructLoweringDiagnosticCode.RSL001,
            $"ref struct '{declaration.Identifier.Text}' preserved: {reason}.",
            filePath, line, column);
    }

    private static bool ParsesWithoutErrors(string code)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                code, new CSharpParseOptions(LanguageVersion.Latest));
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private static RefStructLoweringResult Rollback(
        string sourceCode,
        RefStructLoweringStatistics statistics,
        IReadOnlyList<RefStructLoweringDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
}
