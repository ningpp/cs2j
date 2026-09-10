using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a user-defined generic method whose name collides (case-insensitively)
/// with JsonSerializer.DeserializeAsync&lt;T&gt; is NOT incorrectly marked as needing
/// a TypeReference&lt;T&gt; token. The user method may call a different method (e.g.
/// Serializer.DeserializeWrapper&lt;T&gt;) that expects Class&lt;T&gt;, so the token
/// type must remain Class&lt;T&gt; to match.
/// </summary>
public class JsonSerializerMethodNameCollisionTests
{
    [Fact]
    public void UserMethodNamedDeserializeAsync_DoesNotUseTypeReferenceToken()
    {
        // The user defines a method named 'deserializeAsync' (lowercase) that calls
        // a wrapper method expecting Class<T>, NOT JsonSerializer.DeserializeAsync<T>.
        // A sibling method 'runTest' calls both JsonSerializer.DeserializeAsync<T>
        // (the real one) and the user's deserializeAsync<T>. The name collision
        // (case-insensitive) must NOT cause the user's method to inherit TypeReference.
        var result = Convert("""
            using System.Threading.Tasks;
            using System.Text.Json;

            class TestClass {
                private Wrapper Serializer { get; } = new Wrapper();

                // User method whose name collides with JsonSerializer.DeserializeAsync
                private async Task deserializeAsync<T>(string json)
                {
                    var obj = (T)(await Serializer.DeserializeWrapper<T>(json, null));
                    obj.ToString();
                }

                // This method calls BOTH JsonSerializer.DeserializeAsync<T> AND
                // the user's deserializeAsync<T>. The call to the JsonSerializer
                // version must NOT cause the user method to be marked as TypeRef.
                public async Task runTest<T>(string json)
                {
                    await deserializeAsync<T>(json);
                    _ = JsonSerializer.DeserializeAsync<T>(json);
                }
            }

            class Wrapper {
                public Task<T> DeserializeWrapper<T>(string json, object options)
                {
                    return Task.FromResult<T>(default);
                }
            }
            """);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The user's deserializeAsync method should use Class<T>, NOT TypeReference<T>
        Assert.Contains("Class<T>", code);
        Assert.DoesNotContain("TypeReference<T>", code);
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
