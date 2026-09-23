using System;
using System.Collections.Generic;

namespace SampleTupleLib;

/// <summary>Long tuples (Rest layout), generic tuples and explicit ValueTuple syntax.</summary>
public class TupleLongAndGeneric
{
    public (int, int, int, int, int, int, int, int) Eight() => (1, 2, 3, 4, 5, 6, 7, 8);

    public int Eighth()
    {
        var t = Eight();
        return t.Item8 + t.Item1;
    }

    public (int, int, int, int, int, int, int, int, int) Nine()
        => (1, 2, 3, 4, 5, 6, 7, 8, 9);

    public int Ninth()
    {
        var t = Nine();
        return t.Item9;
    }

    public ValueTuple<int, string> ExplicitValueTuple() => ValueTuple.Create(1, "one");

    public (int, string) ExplicitConstruction() => new ValueTuple<int, string>(2, "two");

    public List<(int, string)> GenericList() => new List<(int, string)>();

    public Dictionary<string, (int, int)> GenericDictionary() => new Dictionary<string, (int, int)>();

    public bool TupleEquality((int, string) left, (int, string) right) => left == right;

    public bool TupleInequality((int, string) left, (int, string) right) => left != right;

    public (long, long) Widening()
    {
        (int, int) small = (3, 4);
        return small;
    }

    public (int a, int b)? NullableTuple() => (1, 2);

    public int ReadNullable((int a, int b)? maybe) => maybe?.a ?? 0;
}
