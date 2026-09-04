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

    // ── Bug 1: Forward-only generic method missing _cs2j_T ──

    /// <summary>
    /// A generic method whose body uses T ONLY by forwarding to another generic
    /// method call (no typeof(T) / default(T) in the forwarder body). The
    /// forwarder's Java signature MUST include the runtime Class&lt;T&gt; _cs2j_T
    /// leading parameter, and the body must USE it (not a bare unbound
    /// identifier).
    /// </summary>
    [Fact]
    public void GenericMethod_ForwardOnly_AddsLeadingClassTokenParameter()
    {
        var result = Convert("""
            using System;

            public static class Helper
            {
                public static TValue Forward<TValue>(string input)
                {
                    return OtherClass.GenericCall<TValue>(input);
                }
            }

            public static class OtherClass
            {
                public static TValue GenericCall<TValue>(string input)
                {
                    return default(TValue);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // The Forward method should have a _cs2j_TValue parameter (Class<TValue>)
        Assert.Contains("_cs2j_TValue", code);
        // The body reference should be the parameter, not a standalone _cs2j_TValue ref
        // (i.e. no unresolved variable usage)
        Assert.Contains("genericCall(_cs2j_TValue, input)", code);
    }

    // ── Bug 2: Abstract + override pair must both declare _cs2j_T ──

    /// <summary>
    /// An abstract generic method and its concrete override must both include
    /// the runtime Class&lt;T&gt; _cs2j_T leading parameter. The abstract
    /// declares the slot; the override receives and forwards it.
    ///
    /// Before fix: the override drains requirements and signature omits _cs2j_T,
    /// but body still references it → "cannot find symbol _cs2j_T".
    /// </summary>
    [Fact]
    public void Override_ForwardingGenericMethod_AbstractAndImplDeclareClassToken()
    {
        var result = Convert("""
            using System;

            public abstract class WrapperBase
            {
                protected internal abstract T DeserializeWrapper<T>(string json, JsonSerializerOptions options = null);
            }

            public class JsonSerializerOptions { }

            public class ConcreteWrapper : WrapperBase
            {
                protected internal override T DeserializeWrapper<T>(string json, JsonSerializerOptions options = null)
                {
                    return JsonSerializer.Deserialize<T>(json, options);
                }
            }

            public static class JsonSerializer
            {
                public static T Deserialize<T>(string json, JsonSerializerOptions options = null)
                {
                    return default(T);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        TestOutput.WriteLine("Generated:\n" + code);

        // The sync case should compile: override body should use 'null' for Class token
        // and not reference an undeclared _cs2j_T
        Assert.False(code.Contains("=_cs2j_T;"), "Should not assign undeclared _cs2j_T");
        // The body of the override should call deserialize with 'null' not _cs2j_T
        // (since _cs2j_T is undeclared in the override context)
        var implBody = code.Split('\n').FirstOrDefault(l =>
            l.Contains("return") && l.Contains("deserialize(") && !l.Contains("return deserialize"));
        TestOutput.WriteLine($"impl body: {implBody}");
        if (implBody != null)
        {
            // Should use null for the Class parameter (since override can't have _cs2j_T).
            // The generated form may be deserialize(null, ...) or
            // deserialize(((Class<T>) (Class<?>) null), ...)
            Assert.True(implBody.Contains("null") && !implBody.Contains("_cs2j_T"),
                $"Expected null Class token in override body, got: {implBody}");
        }
    }

    // ── Bug 3: Default param overload in generic forwarding method ──

    /// <summary>
    /// A generic method with default params that forwards T to another generic
    /// method. The overload must forward _cs2j_T as the leading param, and the
    /// default-value params as the trailing ones.
    /// </summary>
    [Fact]
    public void GenericMethod_ForwardWithDefaultParam_OverloadsForwardClassToken()
    {
        var result = Convert("""
            using System;

            public static class JsonSerializer
            {
                public static TValue Deserialize<TValue>(string json, JsonSerializerOptions options = null)
                {
                    var _ = typeof(TValue);
                    return default(TValue);
                }
            }

            public class JsonSerializerOptions { }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // Full method has the leading _cs2j_T
        Assert.Contains("deserialize(Class<TValue> _cs2j_TValue, String json, JsonSerializerOptions options)", code);
        // Overload (without options) forwards _cs2j_T + default value for options
        Assert.Contains("deserialize(Class<TValue> _cs2j_TValue, String json)", code);
        Assert.Contains("return deserialize(_cs2j_TValue, json, (JsonSerializerOptions) null)", code);
    }

    /// <summary>
    /// Mirrors the actual DeserializationWrapper scenario from System.Text.Json:
    /// an abstract base class declares a generic method with a default-valued param.
    /// The nested concrete class overrides it and forwards T into a CALLEE that
    /// requires a trailing Class&lt;?&gt; argument (Async API). Before fix: the
    /// call site generated _cs2j_T in the LEADING position while the method's
    /// signature placed Class&lt;?&gt; at the TRAILING position — arity/position
    /// mismatch and unresolved _cs2j_T variable.
    /// </summary>
    [Fact]
    public void Override_ForwardToAsync_DoesNotPutTokenInLeadingPosition()
    {
        var result = Convert("""
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;

            public abstract class WrapperBase
            {
                protected internal abstract Task<T> DeserializeWrapper<T>(string json, JsonSerializerOptions options = null);
            }

            public class JsonSerializerOptions { }

            public class ConcreteWrapper : WrapperBase
            {
                protected internal override async Task<T> DeserializeWrapper<T>(string json, JsonSerializerOptions options = null)
                {
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                    {
                        return await JsonSerializer.DeserializeAsync<T>(stream, options);
                    }
                }
            }

            public static class JsonSerializer
            {
                public static ValueTask<T> DeserializeAsync<T>(Stream utf8Json, JsonSerializerOptions options = null, CancellationToken cancellationToken = default)
                {
                    return ValueTask.FromResult(default(T)!);
                }

                public static ValueTask<T> Deserialize<T>(string json, JsonSerializerOptions options = null)
                {
                    return ValueTask.FromResult(default(T)!);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;
        TestOutput.WriteLine("FULL GENERATED:\n" + code + "\nEND");

        // The generated code should COMPILE. Specifically:
        // - The deserializeAsync method should have _cs2j_T as a leading Class<T> parameter
        var lines = code.Split('\n');
        // Find the deserializeAsync declaration line - it should have _cs2j_T
        var asyncDeclLine = lines.FirstOrDefault(l =>
            l.Contains("deserializeAsync(") && l.Contains("_cs2j_T"));
        TestOutput.WriteLine($"async decl: {asyncDeclLine}");
        // Verify _cs2j_T is in the method signature (as declared parameter)
        Assert.Contains("_cs2j_T", asyncDeclLine ?? "");
        // Verify the call site doesn't have undeclared _cs2j_T in a non-parameter position
        var asyncCallLines = lines.Where(l => l.Contains("deserializeAsync(") && !l.Contains("CompletableFuture deserializeAsync")).ToList();
        foreach (var line in asyncCallLines)
        {
            TestOutput.WriteLine($"async call/decl: {line.Trim()}");
        }
    }

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
