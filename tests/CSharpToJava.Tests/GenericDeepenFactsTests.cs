using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.GenericPreprocess;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

public class GenericDeepenFactsTests
{
    private static (SemanticModel Model, LambdaExpressionSyntax Lambda) BindLambda(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("gd-facts",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var lambda = tree.GetRoot().DescendantNodes().OfType<LambdaExpressionSyntax>().First();
        return (model, lambda);
    }

    [Fact]
    public void ExpressionTreeFacts_Detects_ExpressionLambda()
    {
        var (model, lambda) = BindLambda(
            "using System; using System.Linq.Expressions; class C { void M() { Expression<Func<int,bool>> e = x => x > 1; } }");
        Assert.True(ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda));
    }

    [Fact]
    public void ExpressionTreeFacts_FuncLambda_IsNotExpressionTree()
    {
        var (model, lambda) = BindLambda(
            "using System; class C { void M() { Func<int,bool> f = x => x > 1; } }");
        Assert.False(ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda));
    }

    [Fact]
    public void SyntaxFacts_Names_AreDeterministic()
    {
        Assert.Equal("__cs2j_token_T", GenericDeepenSyntaxFacts.AnchorVariableName("T"));
        Assert.Equal("__cs2j_typeof_TKey", GenericDeepenSyntaxFacts.E10SentinelVariableName("TKey"));
        Assert.Equal("__Cs2jGenericHolder_", GenericDeepenSyntaxFacts.HolderPrefix);
        Assert.Equal("__Cs2jGenericHolder_Outer_1_Inner", GenericDeepenSyntaxFacts.HolderTypeName("Outer`1+Inner"));
    }

    [Fact]
    public void SyntaxFacts_IsTypeParameterType_SemanticAndFallback()
    {
        var tree = CSharpSyntaxTree.ParseText(
            "class C { void M<T>() { object o = typeof(T); } }");
        var compilation = CSharpCompilation.Create("gd-facts2",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var typeNode = tree.GetRoot().DescendantNodes().OfType<TypeOfExpressionSyntax>().First().Type;
        Assert.True(GenericDeepenSyntaxFacts.IsTypeParameterType(model, typeNode));
        // No-model conservative fallback: single uppercase letter qualifies; predefined names don't.
        Assert.True(GenericDeepenSyntaxFacts.IsTypeParameterType(null, SyntaxFactory.ParseTypeName("T")));
        Assert.False(GenericDeepenSyntaxFacts.IsTypeParameterType(null, SyntaxFactory.ParseTypeName("Int32")));
        Assert.True(GenericDeepenSyntaxFacts.IsTypeParameterType(null, SyntaxFactory.ParseTypeName("TValue")));
    }

    [Fact]
    public void Statistics_Merge_SumsCounters()
    {
        var a = new GenericDeepenStatistics { TokenAnchorsInserted = 1, IsAsSitesCovered = 2 };
        var b = new GenericDeepenStatistics { TokenAnchorsInserted = 3 };
        a.Merge(b);
        Assert.Equal(4, a.TokenAnchorsInserted);
        Assert.Equal(2, a.IsAsSitesCovered);
    }
}
