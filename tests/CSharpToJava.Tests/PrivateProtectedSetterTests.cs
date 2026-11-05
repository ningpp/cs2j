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

    /// <summary>
    /// 读取侧回归（csharp-sdk regular-expressions）：'public int Length { get; private protected set; }'
    /// 仍是属性声明，Java 侧发射 bean getter getLength()；成员访问必须发同一形态。
    /// 之前按「private protected set ⇒ C11 访问器形态」无条件发 Length()/Index()，
    /// 导致 RegexRunner/Regex/RegexReplacement/MatchCollection 29 处
    /// 「找不到符号 方法 Length()/Index()」。
    /// </summary>
    [Fact]
    public void PrivateProtectedProperty_MemberAccess_EmitsBeanGetter()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
public class Capture
{
    public int Index { get; private protected set; }
    public int Length { get; private protected set; }
}

public class Runner
{
    public int Measure(Capture capture)
    {
        return capture.Index + capture.Length;
    }
}",
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("capture.getIndex()", code);
        Assert.Contains("capture.getLength()", code);
        Assert.DoesNotContain("capture.Index()", code);
        Assert.DoesNotContain("capture.Length()", code);
    }
}
