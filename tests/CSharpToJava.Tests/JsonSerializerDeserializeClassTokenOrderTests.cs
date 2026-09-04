using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for Class token placement when a generic method
/// body uses typeof(T) or default(T). The converter adds a leading
/// Class&lt;T&gt; _cs2j_T parameter to the method signature (via
/// AddPendingRuntimeClassParameters). Call sites must PREPEND the
/// Class literal to match the leading parameter position.
/// </summary>
public class JsonSerializerDeserializeClassTokenOrderTests
{
    [Fact]
    public void GenericMethodWithTypeOf_LeadingClassToken_PrependedToMatchSignature()
    {
        // JsonSerializer.Deserialize<TValue>(string json) where the method body
        // uses typeof(TValue). The converter adds a leading Class<TValue> _cs2j_TValue
        // parameter, so the call site must place Test.class BEFORE json.
        var result = Convert(@"
using System;

public static class JsonSerializer {
    public static TValue Deserialize<TValue>(string json) {
        return Deserialize<TValue>(json, typeof(TValue));
    }

    public static TValue Deserialize<TValue>(string json, Type type) {
        return default(TValue);
    }
}

public class Test {
    public void M() {
        string json = ""{}"";
        var result = JsonSerializer.Deserialize<Test>(json);
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        System.Console.WriteLine("=== GENERATED CODE ===");
        System.Console.WriteLine(result.GeneratedCode);
        System.Console.WriteLine("=== END CODE ===");

        // The method signature should have a leading Class<TValue> _cs2j_TValue parameter
        Assert.Contains("deserialize(Class<TValue> _cs2j_TValue, String json)", result.GeneratedCode);

        // The call site should place Test.class in the leading position (before json)
        var lines = result.GeneratedCode.Split('\n');
        var callLine = lines.FirstOrDefault(l => l.Contains("deserialize(") && l.Contains("Test.class"));
        Assert.NotNull(callLine);
        var classPos = callLine.IndexOf("Test.class");
        var jsonPos = callLine.IndexOf("json");
        Assert.True(jsonPos >= 0 && classPos >= 0,
            $"Expected both 'json' and 'Test.class' in call line: {callLine}");
        // Class literal should come BEFORE json (leading position)
        Assert.True(classPos < jsonPos,
            $"Expected 'Test.class' before 'json' in call line: {callLine}");
    }

    [Fact]
    public void GenericMethodWithDefault_LeadingClassToken_PrependedToMatchSignature()
    {
        // Variant where the method body uses default(T) in addition to typeof(T).
        var result = Convert(@"
using System;

public static class JsonSerializer {
    public static TValue Deserialize<TValue>(string json) {
        TValue v = default(TValue);
        return Deserialize<TValue>(json, typeof(TValue));
    }

    public static TValue Deserialize<TValue>(string json, Type type) {
        return default(TValue);
    }
}

public class Test {
    public void M() {
        string json = ""{}"";
        var result = JsonSerializer.Deserialize<Test>(json);
    }
}");

        Assert.True(result.Success, result.GeneratedCode);

        // The method signature should have a leading Class<TValue> _cs2j_TValue parameter
        Assert.Contains("deserialize(Class<TValue> _cs2j_TValue, String json)", result.GeneratedCode);

        var lines = result.GeneratedCode.Split('\n');
        var callLine = lines.FirstOrDefault(l => l.Contains("deserialize(") && l.Contains("Test.class"));
        Assert.NotNull(callLine);
        var classPos = callLine.IndexOf("Test.class");
        var jsonPos = callLine.IndexOf("json");
        Assert.True(jsonPos >= 0 && classPos >= 0,
            $"Expected both 'json' and 'Test.class' in call line: {callLine}");
        Assert.True(classPos < jsonPos,
            $"Expected 'Test.class' before 'json' in call line: {callLine}");
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
