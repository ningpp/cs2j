using System;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the NetworkSimplex pattern where an outer class contains
/// sibling nested classes: one implementing IEnumerable&lt;T&gt; and another
/// implementing IEnumerator&lt;T&gt;. The IEnumerable&lt;T&gt; class returns
/// the sibling IEnumerator&lt;T&gt; from its GetEnumerator().
///
/// The converter must keep the IEnumerator&lt;T&gt; class as
/// CSharpGenericEnumerator&lt;T&gt; (not downgrade it to
/// CSharpGenericEnumerator&lt;Object&gt;), otherwise the iterator() return
/// type CSharpGenericEnumerator&lt;T&gt; cannot accept the new instance.
/// </summary>
public class SiblingEnumeratorTypeMismatchTests
{
    [Fact]
    public void SiblingNestedEnumerator_KeepsTypedGeneric()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class NetworkSimplex
{
    public IncEdges IncidentEdges(int v)
    {
        return new IncEdges(v, this);
    }

    public class IncEdges : IEnumerable<NetworkEdge>
    {
        private int _v;
        private NetworkSimplex _nw;

        public IncEdges(int v, NetworkSimplex nw)
        {
            _v = v;
            _nw = nw;
        }

        IEnumerator<NetworkEdge> IEnumerable<NetworkEdge>.GetEnumerator()
        {
            return new IncEdgeEnumerator(null, null);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new IncEdgeEnumerator(null, null);
        }
    }

    internal class IncEdgeEnumerator : IEnumerator<NetworkEdge>
    {
        private IEnumerator _outEdges;
        private IEnumerator _inEdges;

        internal IncEdgeEnumerator(IEnumerator outEdges, IEnumerator inEdges)
        {
            _outEdges = outEdges;
            _inEdges = inEdges;
        }

        public NetworkEdge Current => null;
        object IEnumerator.Current => null;
        public bool MoveNext() => false;
        public void Reset() { }
        public void Dispose() { }
    }
}

public class NetworkEdge { }
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // IncEdgeEnumerator should implement CSharpGenericEnumerator<NetworkEdge>, not <Object>
        Assert.Contains("CSharpGenericEnumerator<NetworkEdge>", code);
        // It should NOT be downgraded to CSharpGenericEnumerator<Object>
        Assert.DoesNotContain("CSharpGenericEnumerator<Object>", code);
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
