using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for EventHandler method reference conversion that ensures Java compilation compatibility.
///
/// Bug: When assigning a method group to an EventHandler<T> event (e.g., ProgressChanged += NotifyProgressChanged),
/// the converter generates a method reference (this::notifyProgressChanged). However, Java's type inference
/// sometimes fails to match the method reference to BiConsumer<Object, T>, resulting in compilation errors.
///
/// Fix: Generate an explicit lambda wrapper (sender, args) -> this.methodName(sender, args) instead of
/// a bare method reference this::methodName for void methods with 2 parameters matching the EventHandler signature.
/// </summary>
public class EventHandlerMethodReferenceTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void EventHandler_MethodReferenceAssignment_ShouldUseExplicitLambda()
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

                private void NotifyProgressChanged(object sender, ProgressEventArgs args)
                {
                    // Handler implementation
                }

                public void AddListener(AlgorithmBase child)
                {
                    // This method group assignment should generate an explicit lambda
                    // to avoid Java type inference issues with BiConsumer
                    child.ProgressChanged += this.NotifyProgressChanged;
                }

                public void RemoveListener(AlgorithmBase child)
                {
                    child.ProgressChanged -= this.NotifyProgressChanged;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Should generate add/remove listener methods
        Assert.Contains("addProgressChangedListener", result.GeneratedCode);
        Assert.Contains("removeProgressChangedListener", result.GeneratedCode);

        // Should use explicit lambda instead of bare method reference for type inference compatibility
        // Pattern: (sender, args) -> this.notifyProgressChanged(sender, args)
        Assert.Contains("(sender, args) -> this.notifyProgressChanged(sender, args)", result.GeneratedCode);

        // Should NOT use the bare method reference pattern that causes compilation errors
        Assert.DoesNotContain("this::notifyProgressChanged", result.GeneratedCode);
    }

    [Fact]
    public void EventHandler_MethodReferenceInSameClass_ShouldUseExplicitLambda()
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
                    if (ProgressChanged != null)
                    {
                        ProgressChanged.Invoke(sender, args);
                    }
                }

                public void StartListenToProgress(AlgorithmBase child)
                {
                    child.ProgressChanged += NotifyProgress;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Should use explicit lambda for the method reference
        Assert.Contains("(sender, args) -> notifyProgress(sender, args)", result.GeneratedCode);
    }

    [Fact]
    public void EventHandler_WithDifferentMethodNames_ShouldGenerateCorrectLambdas()
    {
        const string code = """
            using System;

            public class CustomEventArgs : EventArgs
            {
                public int Value { get; set; }
            }

            public class EventSource
            {
                public event EventHandler<CustomEventArgs> CustomEvent;

                private void OnCustomEvent(object sender, CustomEventArgs e)
                {
                    // Handler
                }

                public void Subscribe(EventSource source)
                {
                    source.CustomEvent += this.OnCustomEvent;
                }

                public void Unsubscribe(EventSource source)
                {
                    source.CustomEvent -= this.OnCustomEvent;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Should use explicit lambda with correct parameter names
        Assert.Contains("(sender, e) -> this.onCustomEvent(sender, e)", result.GeneratedCode);
    }
}
