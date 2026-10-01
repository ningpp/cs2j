using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// CE-GUARD (design §2.5): inventory / explicit rejection of C# 11/12 syntax
/// the family does not lower — interface <c>static abstract/virtual</c>
/// members (0585), <c>scoped</c> (0586) and generic attributes (0587);
/// expression-tree collection expressions are defended by the rule (0588).
///
/// M3 performs the semantic analysis and emits the diagnostics/inventory; M0
/// ships only the syntax-only cheap scan that feeds the family dirty gate, so
/// guard-relevant files already enter analysis (and stay byte-preserved).
/// </summary>
internal static class NewSyntaxGuardAnalyzer
{
    /// <summary>
    /// Cheap syntax-only trigger: true if the file may contain any CE-GUARD
    /// syntax. Deliberately over-inclusive (semantic precision lands in M3).
    /// </summary>
    public static bool ContainsGuardSyntax(CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax m when IsInterfaceStaticAbstractOrVirtual(m):
                case PropertyDeclarationSyntax p when IsInterfaceStaticAbstractOrVirtual(p):
                case IndexerDeclarationSyntax i when IsInterfaceStaticAbstractOrVirtual(i):
                case EventDeclarationSyntax e when IsInterfaceStaticAbstractOrVirtual(e):
                case EventFieldDeclarationSyntax ef when IsInterfaceStaticAbstractOrVirtual(ef):
                case OperatorDeclarationSyntax o when IsInterfaceStaticAbstractOrVirtual(o):
                    return true;

                case ClassDeclarationSyntax c when IsGenericAttributeDeclaration(c):
                    return true;

                case AttributeSyntax a when a.Name is GenericNameSyntax:
                    return true;
            }
        }

        return root.DescendantTokens()
            .Any(token => token.IsKind(SyntaxKind.ScopedKeyword));
    }

    /// <summary>
    /// Interface member (or field-like event) declared <c>static abstract</c>
    /// or <c>static virtual</c>. Syntax-only: parent must be an interface and
    /// the modifier list carries static + (abstract|virtual).
    /// </summary>
    private static bool IsInterfaceStaticAbstractOrVirtual(MemberDeclarationSyntax member)
    {
        if (member.Parent is not InterfaceDeclarationSyntax)
            return false;

        var modifiers = member.Modifiers;
        var isStatic = modifiers.Any(t => t.IsKind(SyntaxKind.StaticKeyword));
        var isAbstractOrVirtual = modifiers.Any(t => t.IsKind(SyntaxKind.AbstractKeyword))
            || modifiers.Any(t => t.IsKind(SyntaxKind.VirtualKeyword));
        return isStatic && isAbstractOrVirtual;
    }

    /// <summary>
    /// Generic attribute declaration: a generic class whose base list mentions
    /// Attribute (cheap name suffix; M3 resolves the base symbol).
    /// </summary>
    private static bool IsGenericAttributeDeclaration(ClassDeclarationSyntax classDeclaration)
    {
        if (classDeclaration.TypeParameterList == null
            || classDeclaration.BaseList == null)
        {
            return false;
        }

        return classDeclaration.BaseList.Types.Any(baseType =>
            baseType.Type is NameSyntax name && NameEndsWithAttribute(name));
    }

    private static bool NameEndsWithAttribute(NameSyntax name)
    {
        var text = name switch
        {
            GenericNameSyntax generic => generic.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.Text,
            _ => name.ToString(),
        };
        return text == "Attribute" || text.EndsWith("Attribute", StringComparison.Ordinal);
    }
}
