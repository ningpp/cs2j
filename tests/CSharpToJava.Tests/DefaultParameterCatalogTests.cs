using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class DefaultParameterCatalogTests
{
    [Fact]
    public void Collect_SingleMethod_RegistersExistingSignature()
    {
        var src = "class C { public void M(int a, string b = \"x\") { } }";
        var (tree, model) = Parse(src);
        var catalog = new DefaultParameterCatalog();
        catalog.Collect(tree, model);

        var method = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().First();
        var symbol = model.GetDeclaredSymbol(method)!;
        Assert.True(catalog.HasExistingSignature(
            symbol.ContainingType!, "M", "int, string"));
    }

    [Fact]
    public void HasPendingOverload_InitiallyFalse()
    {
        var catalog = new DefaultParameterCatalog();
        Assert.False(catalog.HasPendingOverload(null!, "M", "global::C.M()"));
    }

    [Fact]
    public void RegisterPendingOverload_ThenHasPendingReturnsTrue()
    {
        var catalog = new DefaultParameterCatalog();
        catalog.RegisterPendingOverload(null!, "M", "global::C.M()");
        Assert.True(catalog.HasPendingOverload(null!, "M", "global::C.M()"));
    }

    private static (SyntaxTree tree, SemanticModel model) Parse(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("cat-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree, compilation.GetSemanticModel(tree));
    }
}
