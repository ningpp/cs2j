using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SuppressTypeTests
{
    [Fact]
    public void ISerializable_Interface_Not_In_Implements()
    {
        var result = Convert(@"
using System.Runtime.Serialization;

public class MyUri : ISerializable
{
    public void GetObjectData(SerializationInfo info, StreamingContext context) { }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("__suppress__", result.GeneratedCode);
        Assert.DoesNotContain("ISerializable", result.GeneratedCode);
    }

    [Fact]
    public void GetObjectData_Method_With_Suppressed_Parameters_Is_Skipped()
    {
        var result = Convert(@"
using System.Runtime.Serialization;

public class MyUri : ISerializable
{
    public void GetObjectData(SerializationInfo info, StreamingContext context) { }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("getObjectData", result.GeneratedCode);
    }

    [Fact]
    public void NormalizationForm_Maps_To_Normalizer_Form()
    {
        var result = Convert(@"
using System.Text;

public class Helper
{
    public string Normalize(string input)
    {
        return input.Normalize(NormalizationForm.FormC);
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Normalizer$Form", result.GeneratedCode);
        Assert.Contains("Normalizer.Form", result.GeneratedCode);
    }

    [Fact]
    public void FormatException_Maps_To_Compat_FormatException()
    {
        var result = Convert(@"
public class MyFormatException : FormatException
{
    public MyFormatException() { }
    public MyFormatException(string message) : base(message) { }
    public MyFormatException(string message, Exception inner) : base(message, inner) { }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("NumberFormatException", result.GeneratedCode);
        Assert.Contains("FormatException", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
