using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;

class TestSyntax
{
    static void Main()
    {
        var tempDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator("_cs2jSwap0")
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.IdentifierName("x"))))));
        
        var tempDecl2 = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator("__cs2jSwap0")
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.IdentifierName("x"))))));
        
        Console.WriteLine("With _ prefix: [" + tempDecl.ToFullString() + "]");
        Console.WriteLine("With __ prefix: [" + tempDecl2.ToFullString() + "]");
    }
}
