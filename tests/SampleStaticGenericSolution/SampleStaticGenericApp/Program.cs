using SampleStaticGenericLib;

namespace SampleStaticGenericApp;

public class Program
{
    public static void Main()
    {
        var edges = new TestEdge[]
        {
            new TestEdge(0, 1),
            new TestEdge(1, 2),
            new TestEdge(0, 2),
            new TestEdge(3, 1),
        };
        var graph = new EdgeGraph<TestEdge>(edges, 4);

        var order = GraphSort.Sort(graph);
    }
}
