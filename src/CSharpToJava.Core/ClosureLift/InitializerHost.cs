using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// D18③ host resolution: a lambda whose initializer host is a field/property
/// <c>= …</c> clause (never a property expression body or a local-function
/// body). Capture-free non-<c>static</c> lambdas of this shape are lifted by
/// the M1 static path (GraphLayout CS2J4504), so the analyzer needs the same
/// classification and a member-name anchor for the emitted method.
/// </summary>
internal static class InitializerHost
{
    /// <summary>
    /// True when <paramref name="lambda"/> sits in a field/property initializer;
    /// <paramref name="memberName"/> is the hosting field/property name used as
    /// the <c>Cs2jLambda_&lt;member&gt;_&lt;k&gt;</c> anchor.
    /// </summary>
    public static bool TryGetMemberAnchor(LambdaExpressionSyntax lambda, out string memberName)
    {
        memberName = string.Empty;

        foreach (var ancestor in lambda.Ancestors())
        {
            switch (ancestor)
            {
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property }:
                    memberName = property.Identifier.Text;
                    return memberName.Length > 0;

                case EqualsValueClauseSyntax
                {
                    Parent: VariableDeclaratorSyntax
                    {
                        Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax },
                    } declarator,
                }:
                    memberName = declarator.Identifier.Text;
                    return memberName.Length > 0;

                // A declaration reached without crossing an initializer clause is
                // a property expression body / method-like host: not this family.
                case BaseFieldDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case AccessorDeclarationSyntax:
                case BaseMethodDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                    return false;
            }
        }

        return false;
    }
}
