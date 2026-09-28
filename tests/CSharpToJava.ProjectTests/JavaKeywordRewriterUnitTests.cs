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

    [Fact]
    public void NeedsEscape_True_WhenNameOrDecapIsJavaKeyword()
    {
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("True"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("assert"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("@class"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("Null"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("_"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("yield"));
        Assert.True(JavaKeywordSyntaxFacts.NeedsEscape("@true"));
    }

    [Fact]
    public void NeedsEscape_False_WhenNeitherNameNorDecapIsKeyword()
    {
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("foo"));
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("FALSE"));
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("WHILE"));
        Assert.False(JavaKeywordSyntaxFacts.NeedsEscape("@Foo"));
    }

    [Fact]
    public void NewName_AppendsValue_ToStrippedName()
    {
        Assert.Equal("TrueValue", JavaKeywordSyntaxFacts.NewName("True"));
        Assert.Equal("assertValue", JavaKeywordSyntaxFacts.NewName("assert"));
        Assert.Equal("classValue", JavaKeywordSyntaxFacts.NewName("@class"));
        Assert.Equal("_Value", JavaKeywordSyntaxFacts.NewName("_"));
        Assert.Equal("yieldValue", JavaKeywordSyntaxFacts.NewName("yield"));
    }
}
