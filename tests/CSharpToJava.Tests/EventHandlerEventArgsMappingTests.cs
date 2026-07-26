using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.EventArgs and System.EventHandler are properly mapped
/// when used in event declarations. EventArgs should map to Object, and
/// non-generic EventHandler should map to BiConsumer&lt;Object, Object&gt;.
/// </summary>
public class EventHandlerEventArgsMappingTests
{
    [Fact]
    public void EventHandlerOfEventArgs_MapsEventArgsToObject()
    {
        // event EventHandler<EventArgs> should produce BiConsumer<Object, Object>
        // (EventArgs maps to Object per TypeMappings.json)
        var source = @"
using System;
interface IViewer {
    event EventHandler<EventArgs> ViewChangeEvent;
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // The listener type should be BiConsumer<Object, Object>, not BiConsumer<Object, EventArgs>
        Assert.Contains("BiConsumer<Object, Object>", result.GeneratedCode);
        Assert.DoesNotContain("EventArgs", result.GeneratedCode);
    }

    [Fact]
    public void NonGenericEventHandler_MapsToBiConsumerObjectObject()
    {
        // event EventHandler (non-generic) should produce BiConsumer<Object, Object>
        var source = @"
using System;
interface IViewerObject {
    event EventHandler MarkForDraggingEvent;
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // The listener type should be BiConsumer<Object, Object>, not EventHandler
        Assert.Contains("BiConsumer<Object, Object>", result.GeneratedCode);
        Assert.DoesNotContain("EventHandler", result.GeneratedCode);
    }

    [Fact]
    public void EventHandlerOfCustomArgs_PreservesCustomType()
    {
        // event EventHandler<CustomArgs> should produce BiConsumer<Object, CustomArgs>
        var source = @"
using System;
class MyCustomArgs : EventArgs { }
interface IViewer {
    event EventHandler<MyCustomArgs> Changed;
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("BiConsumer<Object, MyCustomArgs>", result.GeneratedCode);
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
