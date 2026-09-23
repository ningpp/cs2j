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
