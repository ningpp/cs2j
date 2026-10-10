using CSharpToJava.CLI;
using CSharpToJava.Core.EnumPreprocessor;
using Xunit;

namespace CSharpToJava.Tests;

public class ProjectEnumPreprocessorTests
{
    [Fact]
    public void IntermediateDirectoryName_Is_Correct()
    {
        Assert.Equal(".cs2j-enum-src", ProjectEnumPreprocessor.IntermediateDirectoryName);
    }
}
