using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class DefaultParameterStripperTests
{
    private readonly ITestOutputHelper _output;
    public DefaultParameterStripperTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Options_Default_AllEnabled()
    {
        var o = new DefaultParameterStripperOptions();
        Assert.True(o.GenerateOverloads);
        Assert.False(o.Verbose);
        Assert.False(o.Strict);
    }

    [Fact]
    public void Statistics_StartsZero()
    {
        var s = new DefaultParameterStripperStatistics();
        Assert.Equal(0, s.FilesTransformed);
        Assert.Equal(0, s.OverloadsGenerated);
    }

    [Fact]
    public void Strip_NoDefaults_ReturnsUnchanged()
    {
        var result = Strip("class C { public void M(int a) { } }");
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void Strip_SingleDefault_StripsAndGeneratesOverload()
    {
        var result = Strip("""class C { public void M(int a, string b = "x") { } }""");
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DeclarationsStripped);
        Assert.Equal(1, result.Statistics.ParametersStripped);
        Assert.Equal(1, result.Statistics.OverloadsGenerated);
        Assert.DoesNotContain("= \"x\"", result.OutputCode);
        Assert.Contains("public void M(int a)", result.OutputCode);
        Assert.Contains("M(a, \"x\")", result.OutputCode);
        _output.WriteLine(result.OutputCode);
    }

    [Fact]
    public void Strip_StaticMethod_GeneratesOverload()
    {
        var result = Strip("class C { public static int M(int a, int b = 10) { return a + b; } }");
        Assert.True(result.Changed);
        Assert.Contains("public static int M(int a)", result.OutputCode);
        Assert.Contains("return M(a, 10)", result.OutputCode);
    }

    [Fact]
    public void Strip_AbstractMethod_GeneratesConcreteOverload()
    {
        var result = Strip("""abstract class C { public abstract void M(int a, string b = "x"); }""");
        Assert.True(result.Changed);
        Assert.Contains("public void M(int a)", result.OutputCode);
        Assert.Contains("M(a, \"x\")", result.OutputCode);
    }

    [Fact]
    public void Strip_InterfaceMethod_GeneratesDimOverload()
    {
        var result = Strip("""interface I { void M(int a, string b = "x"); }""");
        Assert.True(result.Changed);
        Assert.Contains("void M(int a) => M(a, \"x\")", result.OutputCode);
    }

    [Fact]
    public void Strip_DelegateDeclaration_Unchanged()
    {
        var result = Strip("delegate void D(int a = 1);");
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.SkippedUnsupported);
    }

    [Fact]
    public void Strip_PartialMethod_Unchanged()
    {
        var result = Strip("partial class C { partial void M(int a = 1); }");
        Assert.False(result.Changed);
    }

    [Fact]
    public void Strip_OverrideMethod_StripsButNoOverload()
    {
        var src = """
        class B { public virtual void M(int a, string b = "x") { } }
        class D : B { public override void M(int a, string b = "y") { } }
        """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("= \"x\"", result.OutputCode);
        Assert.DoesNotContain("= \"y\"", result.OutputCode);
        // Base class should generate overload, override should not
        Assert.Contains("public void M(int a)", result.OutputCode);
    }

    [Fact]
    public void Strip_DefaultExpressionVerbatimCopy()
    {
        var src = """
        enum E { A, B }
        class C { public void M(int a, E b = E.A, string c = null, int d = -1) { } }
        """;
        var result = Strip(src);
        Assert.True(result.Changed);
        Assert.Contains("M(a, E.A, null, -1)", result.OutputCode);
    }

    [Fact]
    public void Strip_OutputIsSyntacticallyValid()
    {
        var src = """class C { public void M(int a, string b = "x", bool c = true) { } }""";
        var result = Strip(src);
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should be valid C#. Errors:\n" + string.Join("\n", errors));
    }

    private static DefaultParameterStripperResult Strip(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("strip-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var stripper = new DefaultParameterStripper();
        return stripper.Strip(src, compilation.GetSemanticModel(tree));
    }
}
