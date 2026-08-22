using System.Collections.Generic;
using System.Linq;

namespace SampleStaticGenericLib;

public interface IEdge
{
    int Source { get; }
    int Target { get; }
}

public class TestEdge : IEdge
{
    public int Source { get; set; }
    public int Target { get; set; }

    public TestEdge(int source, int target)
    {
        Source = source;
        Target = target;
    }
}

public class EdgeGraph<TEdge> where TEdge : IEdge
{
    private readonly List<TEdge> _edges;
    private readonly int _nodeCount;

    public EdgeGraph(IEnumerable<TEdge> edges, int nodeCount)
    {
        _edges = edges.ToList();
        _nodeCount = nodeCount;
    }

    public int NodeCount => _nodeCount;

    public IEnumerable<TEdge> OutEdges(int u)
    {
        return _edges.Where(e => e.Source == u);
    }
}

public static class GraphSort
{
    public static int[] Sort<TEdge>(EdgeGraph<TEdge> graph) where TEdge : IEdge
    {
        var visited = new bool[graph.NodeCount];
        var stack = new Stack<int>();
        var order = new List<int>();

        for (int u = 0; u < graph.NodeCount; u++)
        {
            if (visited[u])
                continue;

            visited[u] = true;
            var en = graph.OutEdges(u).Select(e => e.Target).GetEnumerator();

            stack.Push(u);
            while (stack.Count > 0)
            {
                var cu = stack.Peek();
                bool found = false;
                while (en.MoveNext())
                {
                    int v = en.Current;
                    if (!visited[v])
                    {
                        visited[v] = true;
                        stack.Push(v);
                        found = true;
                        en = graph.OutEdges(v).Select(e => e.Target).GetEnumerator();
                        break;
                    }
                }
                if (!found)
                {
                    order.Add(stack.Pop());
                    if (stack.Count > 0)
                        en = graph.OutEdges(stack.Peek()).Select(e => e.Target).GetEnumerator();
                }
            }
        }

        order.Reverse();
        return order.ToArray();
    }
}
