using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline;

internal static class UnsupportedDomainAnalyzer
{
    private static readonly string[] UnsupportedNamespacePrefixes =
    [
        "System.Windows.Forms",
        "System.Windows.Controls",
        "System.Windows",
        "Microsoft.Maui",
        "Windows.UI.Xaml",
        "System.Xaml",
    ];

    public static IReadOnlyList<DiagnosticMessage> AnalyzeSyntaxTree(SyntaxTree syntaxTree)
    {
        if (syntaxTree.GetRoot() is not CompilationUnitSyntax root)
        {
            return [];
        }

        var diagnostics = new List<DiagnosticMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var usingDirective in root.Usings)
        {
            if (usingDirective.Name == null)
            {
                continue;
            }

            if (TryMatchUnsupportedNamespace(usingDirective.Name.ToString(), out var matchedNamespace))
            {
                AddUniqueDiagnostic(
                    diagnostics,
                    seen,
                    $"Unsupported UI/platform namespace '{matchedNamespace}' is not supported by the Java-only conversion pipeline.",
                    usingDirective.GetLocation());
            }
        }

        foreach (var qualifiedName in root.DescendantNodes().OfType<QualifiedNameSyntax>())
        {
            if (TryMatchUnsupportedNamespace(qualifiedName.ToString(), out var matchedNamespace))
            {
                AddUniqueDiagnostic(
                    diagnostics,
                    seen,
                    $"Unsupported UI/platform namespace '{matchedNamespace}' is not supported by the Java-only conversion pipeline.",
                    qualifiedName.GetLocation());
            }
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (TryMatchUnsupportedNamespace(memberAccess.ToString(), out var matchedNamespace))
            {
                AddUniqueDiagnostic(
                    diagnostics,
                    seen,
                    $"Unsupported UI/platform namespace '{matchedNamespace}' is not supported by the Java-only conversion pipeline.",
                    memberAccess.GetLocation());
            }
        }

        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var attributeName = NormalizeAttributeName(attribute.Name.ToString());
            if (attributeName is "DllImport" or "LibraryImport" or "ComImport")
            {
                AddUniqueDiagnostic(
                    diagnostics,
                    seen,
                    $"Unsupported native interop attribute '{attributeName}' is not supported by the Java-only conversion pipeline.",
                    attribute.GetLocation());
            }
        }

        foreach (var methodDeclaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!methodDeclaration.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExternKeyword)))
            {
                continue;
            }

            AddUniqueDiagnostic(
                diagnostics,
                seen,
                $"Unsupported extern method '{methodDeclaration.Identifier.ValueText}' is not supported by the Java-only conversion pipeline.",
                methodDeclaration.Identifier.GetLocation());
        }

        return diagnostics;
    }

    private static bool TryMatchUnsupportedNamespace(string value, out string matchedNamespace)
    {
        foreach (var prefix in UnsupportedNamespacePrefixes)
        {
            if (value.Equals(prefix, StringComparison.Ordinal)
                || value.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                matchedNamespace = prefix;
                return true;
            }
        }

        matchedNamespace = string.Empty;
        return false;
    }

    private static string NormalizeAttributeName(string name)
    {
        var simpleName = name.Split('.').Last();
        return simpleName.EndsWith("Attribute", StringComparison.Ordinal)
            ? simpleName[..^"Attribute".Length]
            : simpleName;
    }

    private static void AddUniqueDiagnostic(
        ICollection<DiagnosticMessage> diagnostics,
        ISet<string> seen,
        string message,
        Location location)
    {
        var lineSpan = location.GetLineSpan();
        var key = $"{lineSpan.Path}|{lineSpan.StartLinePosition.Line}|{lineSpan.StartLinePosition.Character}|{message}";
        if (!seen.Add(key))
        {
            return;
        }

        diagnostics.Add(new DiagnosticMessage(Context.DiagnosticSeverity.Error, message, location));
    }
}