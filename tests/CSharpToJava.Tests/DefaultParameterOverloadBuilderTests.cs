using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class DefaultParameterOverloadBuilderTests
{
    private readonly ITestOutputHelper _output;
    public DefaultParameterOverloadBuilderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SingleDefault_GeneratesOneOverload()
    {
        var src = """class C { public void M(int a, string b = "x") { } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        var code = overloads[0].ToFullString();
        _output.WriteLine(code);
        Assert.Contains("public void M(int a)", code);
        Assert.Contains("M(a,\"x\")", code);
    }

    [Fact]
    public void TwoDefaults_GeneratesTwoOverloads()
    {
        var src = """class C { public void M(int a, string b = "x", bool c = true) { } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Equal(2, overloads.Count);
        Assert.Contains("M(a,\"x\",true)", overloads[0].ToFullString());
        Assert.Contains("M(a,b", overloads[1].ToFullString());
        Assert.Contains("true)", overloads[1].ToFullString());
    }

    [Fact]
    public void GenericMethod_WritesExplicitTypeArgs()
    {
        var src = """class C { public T M<T>(int a, string b = "x") where T : class { return default; } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        var code = overloads[0].ToFullString();
        Assert.Contains("M<T>(a,\"x\")", code);
        Assert.Contains("where T : class", code);
    }

    [Fact]
    public void NonVoidMethod_UsesReturn()
    {
        var src = """class C { public int M(int a, string b = "x") { return 0; } }""";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        Assert.Contains("returnM(a,\"x\")", overloads[0].ToFullString());
    }

    [Fact]
    public void Constructor_UsesThisInitializer()
    {
        var src = """class C { public C(int a, string b = "x") { } }""";
        var (tree, model) = Parse(src);
        var ctor = tree.GetRoot().DescendantNodes().OfType<ConstructorDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(ctor, model, new DefaultParameterStripperStatistics());
        Assert.Single(overloads);
        var code = overloads[0].ToFullString();
        Assert.Contains("public C(int a)", code);
        Assert.Contains(":this(a,\"x\")", code);
    }

    [Fact]
    public void NoDefaults_ReturnsEmpty()
    {
        var src = "class C { public void M(int a) { } }";
        var (tree, model) = Parse(src);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var overloads = DefaultParameterOverloadBuilder.BuildOverloads(method, model, new DefaultParameterStripperStatistics());
        Assert.Empty(overloads);
    }

    private static (SyntaxTree tree, SemanticModel model) Parse(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("ob-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree, compilation.GetSemanticModel(tree));
    }
}
