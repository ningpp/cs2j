using System;
using SampleTupleLib;

namespace SampleTupleApp;

/// <summary>Cross-project tuple flow: consumption of tuple-typed APIs from the library.</summary>
public static class Program
{
    public static int Main()
    {
        var shapes = new TupleShapes();
        var (count, name) = shapes.State;
        Console.WriteLine(name + count);

        var pair = shapes.Make(count, name);
        Console.WriteLine(pair.Item1 + pair.Item2);

        var (lo, hi) = TupleDeconstruction.GetPoint();
        Console.WriteLine(lo + hi);

        var (key, nested) = TupleDeconstruction.Normalize("a=3");
        Console.WriteLine(key + nested.Item1 + nested.Item2);

        var eight = new TupleLongAndGeneric().Eight();
        Console.WriteLine(eight.Item1 + eight.Item8);

        var list = new TupleLongAndGeneric().GenericList();
        foreach (var (id, label) in list)
        {
            Console.WriteLine(id + label);
        }

        return 0;
    }
}
