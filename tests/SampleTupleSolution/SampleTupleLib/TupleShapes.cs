using System.Collections.Generic;

namespace SampleTupleLib;

/// <summary>Tuple types in every declaration position, including named elements.</summary>
public class TupleShapes
{
    private (int count, string name) _state = (1, "initial");
    private (int, int)[] _pairs = new[] { (1, 2), (3, 4) };

    public (int count, string name) State => _state;

    public (int, string) Make(int a, string b) => (a, b);

    public (int x, int y) Named(int a, int b) => (x: a, y: b);

    public string Describe((int count, string name) item) => item.name + "=" + item.count;

    public int Sum((int, int) pair) => pair.Item1 + pair.Item2;

    public void Assign()
    {
        _state = (5, "assigned");
    }

    public (int, int)[] Pairs => _pairs;
}
