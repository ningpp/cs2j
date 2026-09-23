using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GenericNewConstraintInliningTests
{

    [Fact]
    public void AddToMap_StandaloneMethod_HasFallback()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<TS>()", code);
    }

    [Fact]
    public void AddToMap_Standalone_IEnumerableConstraint_FallbackHasExplicitTypeArg()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : IEnumerable<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<TS>()", code);
        Assert.DoesNotContain("new CSharpList<>()", code);
    }

    [Fact]
    public void AddToMap_Standalone_ConcreteElementType_FallbackHasConcreteTypeArg()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<T, TC>(Dictionary<T, TC> dictionary, T key, string value)
        where TC : ICollection<string>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<String>()", code);
        Assert.DoesNotContain("new CSharpList<>()", code);
    }

    [Fact]
    public void AddToMap_Standalone_MultipleConstraints_FallbackUsesCollectionConstraint()
    {
        var result = Convert("""
using System.Collections.Generic;

public interface ICustomSink { void Flush(); }

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICustomSink, ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<TS>()", code);
        Assert.DoesNotContain("new CSharpList<>()", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
