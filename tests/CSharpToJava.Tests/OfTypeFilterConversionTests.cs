using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# <c>Enumerable.OfType&lt;T&gt;()</c> on a non-generic enumerable (such as
/// <c>TraceListenerCollection</c>) must keep its runtime type filter: Java erases
/// the type argument, so a bare <c>ofType()</c> only unchecked-casts every
/// element and the following typed ToArray throws ArrayStoreException.
/// </summary>
public class OfTypeFilterConversionTests
{
    private const string Source = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;

        namespace System.Diagnostics
        {
            public class TraceListener { }

            public class DefaultTraceListener : TraceListener { }

            public class TraceListenerCollection : IEnumerable
            {
                private readonly List<TraceListener> _items = new List<TraceListener>();

                public void Add(TraceListener listener) { _items.Add(listener); }

                public IEnumerator GetEnumerator() { return _items.GetEnumerator(); }
            }

            public static class Trace
            {
                public static TraceListenerCollection Listeners { get; } = new TraceListenerCollection();
            }
        }

        namespace Sample
        {
            public class DebugAssertRedirector : System.Diagnostics.TraceListener { }

            public class TestClass
            {
                public void M()
                {
                    var redirectors = System.Diagnostics.Trace.Listeners
                        .OfType<DebugAssertRedirector>().ToArray();
                }
            }
        }
        """;

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

    [Fact]
    public void OfType_OnNonGenericEnumerable_EmitsClassLiteralFilter()
    {
        var result = Convert(Source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ofType(DebugAssertRedirector.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ofType()", result.GeneratedCode, StringComparison.Ordinal);
    }
}
