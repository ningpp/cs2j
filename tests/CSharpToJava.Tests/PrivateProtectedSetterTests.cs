using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a C# 'private protected' property setter is emitted as a Java
/// 'protected' setter, not 'private'. Reproduces Match.java:[214/215]
/// "找不到符号 方法 setIndex(int)/setLength(int)" — Capture declares
/// 'public int Index { get; private protected set; }'; the converter emitted a
/// private setIndex(), but the call site lives in subclass Match, so a private
/// setter is inaccessible. C# 'private protected' (accessible to derived classes
/// in the same assembly) maps closest to Java 'protected'.
/// </summary>
public class PrivateProtectedSetterTests
{
    [Fact]
    public void PrivateProtectedSetter_EmitsProtectedJavaSetter()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
public class Capture
{
    public int Index { get; private protected set; }
}",
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // 'private protected' must widen to protected so subclasses can call the setter.
        Assert.Contains("protected void setIndex(int", code);
        Assert.DoesNotContain("private void setIndex(int", code);
    }
}
