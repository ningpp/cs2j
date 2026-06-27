using CSharpToJava.Core.GotoEliminator;

namespace CSharpToJava.Tests;

public partial class GotoEliminatorTests
{
    [Fact]
    public void Options_Default_IsNotStrictNotVerbose()
    {
        var o = new GotoEliminatorOptions();
        Assert.False(o.Strict);
        Assert.False(o.Verbose);
    }

    [Fact]
    public void Diagnostics_Record_Properties_RoundTrip()
    {
        var d = new GotoEliminatorDiagnostic(GotoEliminatorSeverity.Warning, "msg", "M", 7);
        Assert.Equal(GotoEliminatorSeverity.Warning, d.Severity);
        Assert.Equal("msg", d.Message);
        Assert.Equal("M", d.MethodName);
        Assert.Equal(7, d.Line);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new GotoEliminatorStatistics();
        Assert.Equal(0, s.FilesTransformed);
        Assert.Equal(0, s.GotosEliminated);
    }
}
