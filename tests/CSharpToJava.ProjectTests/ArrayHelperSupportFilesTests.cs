using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class ArrayHelperSupportFilesTests
{
    [Fact]
    public void GetArrayHelperFileContent_ContainsExpectedNamespaceAndClass()
    {
        var content = ArrayHelperSupportFiles.GetArrayHelperFileContent();
        Assert.Contains("namespace Cs2jArray", content);
        Assert.Contains("public static class ArrayHelper", content);
        Assert.Contains("public static int GetLength(System.Array array)", content);
        Assert.Contains("public static T Find<T>(T[] array, System.Predicate<T> match)", content);
        Assert.Contains("public static T[] Resize<T>(T[] array, int newSize)", content);
    }

    [Fact]
    public void GetProjectFileContent_ContainsNet10Target()
    {
        var content = ArrayHelperSupportFiles.GetProjectFileContent();
        Assert.Contains("net10.0", content);
        Assert.Contains("Microsoft.NET.Sdk", content);
    }

    [Fact]
    public void LibDirectoryName_AndSingleFileHelperFileName_MatchContract()
    {
        Assert.Equal("Cs2jArray", ArrayHelperSupportFiles.LibDirectoryName);
        Assert.Equal("_Cs2jArrayHelper.cs", ArrayHelperSupportFiles.SingleFileHelperFileName);
    }
}
