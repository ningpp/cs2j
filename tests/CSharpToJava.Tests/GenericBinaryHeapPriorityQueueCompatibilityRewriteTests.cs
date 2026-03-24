using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GenericBinaryHeapPriorityQueueCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_GenericBinaryHeapPriorityQueue_RewritesSelfTypeCollision()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class GenericBinaryHeapPriorityQueue {
                public void enqueue() {
                    var q = new GenericBinaryHeapPriorityQueue<Integer>();
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("GenericBinaryHeapPriorityQueue.java", generated);

        Assert.Contains("new Microsoft.Msagl.Core.DataStructures.GenericBinaryHeapPriorityQueue<Integer>()", output);
        Assert.DoesNotContain("new GenericBinaryHeapPriorityQueue<Integer>()", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_GenericBinaryHeapPriorityQueue_RewritesDictionaryIndexerAssignmentSemantics()
    {
        const string generated = """
            package Microsoft.Msagl.Core.DataStructures;

            public class GenericBinaryHeapPriorityQueue<T> {
                public void enqueue(T element, double priority) {
                    int i = 1;
                    var _chainVal1 = cache.put(element, new GenericHeapElement<T>(i, priority, element));
                    A[i] = _chainVal1;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("GenericBinaryHeapPriorityQueue.java", generated).Replace("\r\n", "\n");

        Assert.Contains("var heapElement = new GenericHeapElement<T>(i, priority, element);", output);
        Assert.Contains("cache.put(element, heapElement);", output);
        Assert.Contains("A[i] = heapElement;", output);
        Assert.DoesNotContain("var _chainVal1 = cache.put", output);
    }
}