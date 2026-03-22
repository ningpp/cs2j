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
}