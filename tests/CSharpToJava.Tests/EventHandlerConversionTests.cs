using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for System.EventHandler<T> delegate conversion.
///
/// Bug: EventHandler<T> delegate takes 2 parameters (Object sender, T args),
/// but the converter was using Consumer<T> which only accepts 1 parameter.
/// The fix uses BiConsumer<Object, T> for EventHandler<T>.
/// </summary>
public class EventHandlerConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void EventHandler_GenericClass_UseBiConsumer()
    {
        const string code = """
            using System;

            public class ProgressEventArgs : EventArgs
            {
                public double Ratio { get; set; }
            }

            public class AlgorithmBase
            {
                public event EventHandler<ProgressEventArgs> ProgressChanged;

                protected void NotifyProgress(object sender, ProgressEventArgs args)
                {
                    ProgressChanged?.Invoke(sender, args);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Should use BiConsumer for EventHandler<T>
        Assert.Contains("BiConsumer<Object, ProgressEventArgs>", result.GeneratedCode);

        // The listener add/remove methods should take BiConsumer
        Assert.Contains("addProgressChangedListener(java.util.function.BiConsumer<Object, ProgressEventArgs>", result.GeneratedCode);
        Assert.Contains("removeProgressChangedListener(java.util.function.BiConsumer<Object, ProgressEventArgs>", result.GeneratedCode);

        // The fire method should invoke with both sender and args
        Assert.Contains("_handler.accept(sender, args)", result.GeneratedCode);
    }

    [Fact]
    public void EventHandler_NonGeneric_UseBiConsumerObject()
    {
        const string code = """
            using System;

            public class EventSource
            {
                public event EventHandler SomethingHappened;

                protected void OnSomethingHappened(object sender, EventArgs args)
                {
                    SomethingHappened?.Invoke(sender, args);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Should use BiConsumer<Object, Object> for non-generic EventHandler
        Assert.Contains("BiConsumer<Object, Object>", result.GeneratedCode);
    }

    [Fact]
    public void EventHandler_MethodReference_ShouldCompile()
    {
        const string code = """
            using System;

            public class ProgressEventArgs : EventArgs
            {
                public double Ratio { get; set; }
            }

            public class AlgorithmBase
            {
                public event EventHandler<ProgressEventArgs> ProgressChanged;

                protected void NotifyProgress(object sender, ProgressEventArgs args)
                {
                    // This method reference should work with BiConsumer
                }

                public void AddListener(AlgorithmBase other)
                {
                    other.ProgressChanged += this.NotifyProgress;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Should generate addProgressChangedListener with BiConsumer parameter
        Assert.Contains("addProgressChangedListener", result.GeneratedCode);
        // Method group should be wrapped in explicit lambda for BiConsumer type inference compatibility
        // (see EventHandlerMethodReferenceTests for rationale)
        Assert.Contains("notifyProgress(sender, args)", result.GeneratedCode);
    }
}
