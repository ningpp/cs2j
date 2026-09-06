using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public class DefaultParameterVerifierTests
{
    [Fact]
    public void Verify_NoResidualDefaults_ReturnsNoErrors()
    {
        var code = "class C { public void M(int a) { } }";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Empty(errors);
    }

    [Fact]
    public void Verify_ResidualDefaultInMethod_ReturnsError()
    {
        var code = """class C { public void M(int a, string b = "x") { } }""";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Single(errors);
        Assert.Equal(DefaultParameterStripSeverity.Error, errors[0].Severity);
    }

    [Fact]
    public void Verify_DelegateDefault_Ignored()
    {
        var code = "delegate void D(int a = 1);";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Empty(errors);
    }

    [Fact]
    public void Verify_IndexerDefault_Ignored()
    {
        var code = "class C { public int this[int i = 0] { get => 0; set { } } }";
        var errors = DefaultParameterVerifier.Verify(code);
        Assert.Empty(errors);
    }
}
