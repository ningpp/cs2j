using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Enqueue/Dequeue → offer/poll mapping does NOT apply to custom types
/// (like MSAGL's GenericBinaryHeapPriorityQueue, BinaryHeapPriorityQueue, EventQueue).
///
/// Regression: the universal name-based mapping at TransformMemberInvocation line 417
/// rewrote Enqueue→offer and Dequeue→poll for ALL types, causing mismatch between
/// call sites (offer/poll) and declarations (enqueue/dequeue via camelCase).
/// System.Collections.Generic.Queue&lt;T&gt; and System.Collections.Queue are handled
/// by TypeMappings.json (Enqueue→add, Dequeue→remove/poll).
/// </summary>
public class EnqueueDequeueMethodMappingTests
{
    [Fact]
    public void CustomGenericHeap_EnqueueWithPriority_GeneratesEnqueue_NotOffer()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class GenericBinaryHeapPriorityQueue<T>
{
    private List<T> _heap = new List<T>();
    public void Enqueue(T element, double priority) { _heap.Add(element); }
    public T Dequeue() { var x = _heap[0]; _heap.RemoveAt(0); return x; }
}

public class ShortestPath
{
    public void Foo(GenericBinaryHeapPriorityQueue<int> pq)
    {
        pq.Enqueue(42, 1.0);
        var x = pq.Dequeue();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Custom type: Enqueue → enqueue (camelCase), NOT offer
        Assert.DoesNotContain(".offer(", code);
        Assert.Contains(".enqueue(", code);

        // Custom type: Dequeue → dequeue (camelCase), NOT poll
        Assert.DoesNotContain(".poll(", code);
        Assert.Contains(".dequeue(", code);
    }

    [Fact]
    public void SystemQueue_Enqueue_Dequeue_UsesTypeMappingAdd()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class Demo
{
    public void Foo()
    {
        var q = new Queue<int>();
        q.Enqueue(42);
        var x = q.Dequeue();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        // System.Collections.Generic.Queue<T>.Enqueue → add (per TypeMappings)
        Assert.Contains(".add(", code);
        // System.Collections.Generic.Queue<T>.Dequeue → remove (per TypeMappings)
        Assert.Contains(".remove(", code);
    }

    [Fact]
    public void CustomHeap_Offer_PollMethodNamesWithPriority()
    {
        var result = Convert(@"
public class EventQueue
{
    public void Enqueue(object evt) { }
    public object Dequeue() { return null; }
}

public class VisibilityGraphGenerator
{
    public void Foo(EventQueue eventQueue, object evt)
    {
        eventQueue.Enqueue(evt);
        var next = eventQueue.Dequeue();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Custom type: should use camelCase, not offer/poll
        Assert.DoesNotContain(".offer(", code);
        Assert.DoesNotContain(".poll(", code);
        Assert.Contains(".enqueue(", code);
        Assert.Contains(".dequeue(", code);
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
