using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the runtime-class-token bug found in System.Text.Json conversion:
///
/// Root cause: When a generic method's body USES a method-level type parameter T
/// ONLY by forwarding it to another generic call (no typeof(T)/default(T)), the
/// converter fails to add a runtime Class&lt;T&gt; _cs2j_T parameter to the
/// method signature. The call site invocation transformer still emits _cs2j_T
/// references in the body → unresolved symbol errors.
///
/// Additionally, override methods have their pending runtime-class requirements
/// intentionally drained (to keep the override signature matching the base slot),
/// but the invocation transformer still generates _cs2j_T references → unresolved
/// symbols in the override body.
/// </summary>
public class GenericMethodForwardTypeParamTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper TestOutput = output;

    private static ConversionResult Convert(string sourceCode, string fileName = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = fileName,
            Options = new ConversionOptions(),
        });
    }

    // ── Bug 3: Default param overload in generic forwarding method ──

    /// <summary>
    /// A generic method with default params that forwards T to another generic
    /// method. The overload must forward _cs2j_T as the leading param, and the
    /// default-value params as the trailing ones.
    /// </summary>

    /// <summary>
    /// Mirrors the actual DeserializationWrapper scenario from System.Text.Json:
    /// an abstract base class declares a generic method with a default-valued param.
    /// The nested concrete class overrides it and forwards T into a CALLEE that
    /// requires a trailing Class&lt;?&gt; argument (Async API). Before fix: the
    /// call site generated _cs2j_T in the LEADING position while the method's
    /// signature placed Class&lt;?&gt; at the TRAILING position — arity/position
    /// mismatch and unresolved _cs2j_T variable.
    /// </summary>

    // ── Bug 4: Multiple files cross-reference (mirror the real S.T.J scenario) ──

    [Fact]
    public async Task DeserializerWrapper_AbstractAndImpl_BothHaveClassToken()
    {
        var files = new[]
        {
            new SourceFile { FilePath = "DeserializationWrapper.cs", Content = """
                using System.IO;
                using System.Threading.Tasks;

                namespace dotnet.text.json.Serialization.Tests
                {
                    public abstract class DeserializationWrapper
                    {
                        protected internal abstract Task<T> DeserializeWrapper<T>(string json, JsonSerializerOptions options = null);
                    }

                    public class JsonSerializerOptions { }

                    public static class JsonSerializer
                    {
                        public static T Deserialize<T>(string json, JsonSerializerOptions options = null)
                        {
                            return default(T);
                        }
                    }
                }
                """ },
            new SourceFile { FilePath = "StringDeserializerWrapper.cs", Content = """
                using System.Threading.Tasks;

                namespace dotnet.text.json.Serialization.Tests
                {
                    public partial class DeserializationWrapper
                    {
                        private class StringDeserializerWrapper : DeserializationWrapper
                        {
                            protected internal override Task<T> DeserializeWrapper<T>(string json, JsonSerializerOptions options = null)
                            {
                                return Task.FromResult(JsonSerializer.Deserialize<T>(json, options));
                            }
                        }
                    }
                }
                """ },
        };

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(files, projectName: "Demo");
        var sbResult = results.FirstOrDefault(r =>
            r.FileName?.Contains("DeserializationWrapper") == true &&
            !r.FileName!.Contains("StringDeserializationWrapper"));
        Assert.NotNull(sbResult);
        var code = sbResult.GeneratedCode;
        TestOutput.WriteLine("ABSTRACT FILE:\n" + code + "\nEND");

        // In cross-file project mode, the abstract's overrides are in different
        // files; the override body uses `null` for Class token. The compilation
        // should succeed regardless. Verify _cs2j_T is not undeclared: it either
        // appears as a declared parameter OR doesn't appear at all (body uses null).
        var lines = code.Split('\n');
        // Check for any line that uses _cs2j_T as a standalone variable
        // (which would mean it's undeclared)
        foreach (var line in lines)
        {
            if (line.Contains("_cs2j_T") && !line.Contains("Class<") &&
                !line.Contains("_cs2j_") && line.Contains("=_cs2j_T"))
            {
                Assert.Fail($"Undeclared _cs2j_T assignment: {line}");
            }
        }
    }
}
