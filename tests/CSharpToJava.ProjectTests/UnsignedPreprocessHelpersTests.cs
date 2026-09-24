using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class UnsignedPreprocessHelpersTests
{
    [Theory]
    [InlineData("net10.0", true)]
    [InlineData("net11.0", true)]
    [InlineData("net10.0-windows", true)]
    [InlineData("net9.0", false)]
    [InlineData("net8.0", false)]
    [InlineData("netcoreapp3.1", false)]
    [InlineData("netstandard2.0", false)]
    [InlineData("net472", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsTargetFrameworkAtLeastNet10_Classifies(string? targetFramework, bool expected)
    {
        Assert.Equal(expected, ProjectUnsignedTypePreprocessor.IsTargetFrameworkAtLeastNet10(targetFramework));
    }
}
