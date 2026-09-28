using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

public class JavaKeywordRewriterUnitTests
{
    private readonly ITestOutputHelper _output;
    public JavaKeywordRewriterUnitTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Models_DefaultCounters_AreZero()
    {
        var stats = new JavaKeywordStatistics();
        Assert.Equal(0, stats.SymbolsRenamed);
        Assert.Equal(0, stats.PlansRolledBack);
        var d = new JavaKeywordDiagnostic(
            JavaKeywordSeverity.Warning, JavaKeywordDiagnosticCode.JK020,
            "operator plan skipped", "Lib/Foo.cs", 7, 3, "Lib.Foo");
        Assert.Equal("JK020", d.Code.ToString());
        Assert.Equal("Lib/Foo.cs(7,3): warning JK020: [Lib.Foo] operator plan skipped", d.ToMsBuildString());
    }
}
