using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for nested class static modifier correctness.
/// In Java, a static inner class cannot reference the enclosing class's type parameters.
/// When a C# nested class uses the enclosing type's type parameters, it must be
/// translated as a non-static inner class in Java.
/// </summary>
public class NestedClassStaticModifierTests
{
    [Fact]
    public void NestedClass_ReferencesEnclosingTypeParameters_IsNotStatic()
    {
        var result = Convert("""
public class OuterDictionary<TKey, TValue>
{
    private sealed class Entry
    {
        public TKey _key;
        public TValue _value;
        public Entry _next;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private final class Entry", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("private static final class Entry", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedClass_DoesNotReferenceEnclosingTypeParameters_IsStatic()
    {
        var result = Convert("""
public class OuterDictionary<TKey, TValue>
{
    private sealed class Helper
    {
        public int _count;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private static final class Helper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedClass_InNonGenericOuter_IsStatic()
    {
        var result = Convert("""
public class Outer
{
    private sealed class Entry
    {
        public int _value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private static final class Entry", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedClass_ReferencesEnclosingTypeParameterInMethod_IsNotStatic()
    {
        var result = Convert("""
public class Container<T>
{
    private class Inner
    {
        public T GetValue() => default;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private class Inner", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("private static class Inner", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedStruct_ReferencesEnclosingTypeParameters_IsNotStatic()
    {
        var result = Convert("""
public class OuterDictionary<TKey, TValue>
{
    private struct Entry
    {
        public TKey Key;
        public TValue Value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private class Entry", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("private static class Entry", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedClass_ReferencesEnclosingTypeParameterViaBaseType_IsNotStatic()
    {
        var result = Convert("""
public class Outer<T>
{
    private class Inner : Base<T>
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private class Inner", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("private static class Inner", result.GeneratedCode, StringComparison.Ordinal);
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
