using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LinqOfTypeConversionTests
{
    [Fact]
    public async Task ProjectOfTypeToArray_OnTraceListeners_FiltersByRequestedType()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = """
                    using System.Diagnostics;
                    using System.Linq;

                    public class DebugAssertRedirector : DefaultTraceListener
                    {
                    }

                    public class Sample
                    {
                        void Redirect()
                        {
                            var defaultListeners = Trace.Listeners.OfType<DefaultTraceListener>().ToArray();
                            foreach (var defaultListener in defaultListeners)
                            {
                                Trace.Listeners.Remove(defaultListener);
                            }
                            Trace.Listeners.Add(new DebugAssertRedirector());
                        }
                    }
                    """,
            },
        });

        var result = Assert.Single(results, item => item.FileName == "Sample.java");
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.Contains(".filter(", java, StringComparison.Ordinal);
        Assert.Contains("instanceof DefaultTraceListener", java, StringComparison.Ordinal);
        Assert.Contains(".toArray(DefaultTraceListener[]::new)", java, StringComparison.Ordinal);
        Assert.DoesNotContain(".ofType()", java, StringComparison.Ordinal);
    }

    private static ConversionOptions CreateProjectOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
    }
}
