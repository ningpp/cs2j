using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the scenario where an abstract generic method
/// is called with explicit type arguments (e.g. Serializer.DeserializeWrapper&lt;T&gt;(json))
/// and the override implementations call JsonSerializer.Deserialize&lt;T&gt; in their bodies.
/// Both the abstract base and the override implementations must gain a leading
/// Class&lt;T&gt; / TypeReference&lt;T&gt; _cs2j_T parameter so call-site tokens match.
/// </summary>
public class OverrideGenericMethodClassTokenTests
{
    [Fact]
    public void AbstractGenericMethodCalledWithExplicitTypeArg_BothBaseAndOverrideGetClassTokenParam()
    {
        var result = Convert(@"
using System;
using System.Threading.Tasks;

public abstract class DeserializationWrapper
{
    public abstract Task<T> DeserializeWrapper<T>(string json, object options = null);
}

public class StringDeserializerWrapper : DeserializationWrapper
{
    public override Task<T> DeserializeWrapper<T>(string json, object options = null)
    {
        var type = typeof(T);
        return Task.FromResult(default(T));
    }
}

public class Test
{
    private DeserializationWrapper _serializer;

    public void M()
    {
        string json = ""{}"";
        var result = _serializer.DeserializeWrapper<Test>(json);
    }
}");

        var diags = string.Join("\n", result.Diagnostics);
        Assert.True(result.Success, $"Conversion failed:\n{diags}\n\nGeneratedCode:\n{result.GeneratedCode}");
        System.Console.WriteLine("=== GENERATED CODE ===");
        System.Console.WriteLine(result.GeneratedCode);
        System.Console.WriteLine("=== END CODE ===");

        var code = result.GeneratedCode ?? "";

        // The call site must pass Test.class as the first argument
        var callLine = GetLineContaining(code, "deserializeWrapper(");
        Assert.NotNull(callLine);
        Assert.Contains("Test.class", callLine);

        // The abstract base method signature must have a leading Class<T> _cs2j_T parameter
        // to receive the Test.class token from the call site
        Assert.Contains("deserializeWrapper(Class<T> _cs2j_T", code);

        // The override method signature must also have a leading Class<T> _cs2j_T parameter
        // to match the base slot (Java @Override signature must match)
        var overrideLines = code.Split('\n')
            .Where(l => l.Contains("deserializeWrapper(Class<T> _cs2j_T"))
            .ToList();
        Assert.True(overrideLines.Count >= 2,
            $"Expected at least 2 methods (abstract + override) with Class<T> _cs2j_T parameter, found {overrideLines.Count}:\n{code}");
    }

    private static string? GetLineContaining(string code, string substring)
    {
        return code.Split('\n').FirstOrDefault(l => l.Contains(substring));
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
