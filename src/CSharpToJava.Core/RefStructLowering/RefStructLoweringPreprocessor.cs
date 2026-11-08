using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefStructLowering;

/// <summary>
/// Independent read-only post-pass <c>ref-struct-lowering</c> (pass stage
/// Declarative, runs right after <c>stackalloc-lowering</c>). It scans the whole
/// file with its <see cref="SemanticModel"/> and removes the <c>ref</c> modifier
/// from user <c>ref struct</c> declarations that provably have no ref-only
/// members, turning them into ordinary value types Java can consume.
/// <para>
/// A declaration keeps its <c>ref</c> modifier (and reports RSL001) as soon as
/// any conservative signal is present: a pointer/function-pointer typed
/// field/property, a <c>ref</c>/<c>ref readonly</c> field, a ref-returning
/// member, a field/property typed as a ref struct (Span/ReadOnlySpan/Memory or
/// any <see cref="INamedTypeSymbol.IsRefLikeType"/> type, including a type
/// parameter constrained with <c>allows ref struct</c>), a nested ref struct
/// type, or a <c>typeof(...)</c> use of the type itself (where byref-like-ness
/// is observable through <see cref="Type.IsByRefLike"/>).
/// </para>
/// Only the <c>ref</c> modifier token changes; every other byte of the changed
/// file is preserved. Clean files are returned byte-for-byte and re-running the
/// pass on its own output is a no-op (idempotent).
/// </summary>
public sealed class RefStructLoweringPreprocessor
{
    public RefStructLoweringResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new RefStructLoweringStatistics();
        var diagnostics = new List<RefStructLoweringDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;
        var typeOfTargets = CollectTypeOfTargets(root, semanticModel);

        var replacements = new List<(StructDeclarationSyntax Original, StructDeclarationSyntax Replacement)>();
        foreach (var declaration in root.DescendantNodes().OfType<StructDeclarationSyntax>())
        {
            if (!declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            {
                continue;
            }

            var preservationReason = FindRefOnlySignal(
                declaration, semanticModel, typeOfTargets);
            if (preservationReason != null)
            {
                statistics.RefStructsPreserved++;
                diagnostics.Add(CreatePreservedDiagnostic(
                    declaration, preservationReason, filePath));
                continue;
            }

            replacements.Add((
                declaration,
                RemoveRefModifier(declaration)));
        }

        if (replacements.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var map = new Dictionary<SyntaxNode, SyntaxNode>(
            replacements.Count, ReferenceEqualityComparer.Instance);
        foreach (var pair in replacements)
        {
            map[pair.Original] = pair.Replacement;
        }

        var newRoot = (CompilationUnitSyntax)root.ReplaceNodes(
            map.Keys, (original, _) => map[original]);
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
        statistics.RefStructsLowered += replacements.Count;
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
        StructDeclarationSyntax declaration,
        SemanticModel model,
        HashSet<INamedTypeSymbol> typeOfTargets)
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
        {
            return "the struct symbol could not be resolved";
        }

        if (typeOfTargets.Contains(symbol))
        {
            return "the type is used in a typeof expression";
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

    /// <summary>
    /// Collects ref struct types referenced by <c>typeof(...)</c> in this tree.
    /// Removing <c>ref</c> would flip <see cref="Type.IsByRefLike"/> for the
    /// resulting <see cref="Type"/> instance, which reflection-based callers can
    /// observe, so those declarations must keep their modifier.
    /// </summary>
    private static HashSet<INamedTypeSymbol> CollectTypeOfTargets(
        CompilationUnitSyntax root, SemanticModel model)
    {
        var targets = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var typeOf in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            if (model.GetTypeInfo(typeOf.Type).Type is INamedTypeSymbol { IsRefLikeType: true } named)
            {
                targets.Add(named);
            }
        }

        return targets;
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
    /// Remove only the <c>ref</c> modifier token. When <c>ref</c> is the first
    /// modifier its leading trivia (indentation) moves to the struct keyword so
    /// the declaration keeps its layout; otherwise the preceding modifier's
    /// trailing trivia already separates the tokens.
    /// </summary>
    private static StructDeclarationSyntax RemoveRefModifier(
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

        return declaration.WithModifiers(modifiers).WithKeyword(keyword);
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
