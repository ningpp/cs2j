using System;
using System.Collections.Generic;

namespace SampleTupleLib;

/// <summary>Deconstruction in every legal position, nested tuples, discards and long tuples.</summary>
public static class TupleDeconstruction
{
    public static (int, int) GetPoint() => (3, 4);

    public static int DeclareAndUse()
    {
        var (a, b) = GetPoint();
        return a + b;
    }

    public static int Swap()
    {
        int x = 1;
        int y = 2;
        (x, y) = (y, x);
        return x * 10 + y;
    }

    public static int ExplicitTyped()
    {
        (int first, int second) = GetPoint();
        return first + second;
    }

    public static int Discards()
    {
        var (keep, _, _) = GetTriple();
        return keep;
    }

    public static (int, int, int) GetTriple() => (7, 8, 9);

    public static int InForeach()
    {
        var pairs = new List<(int, int)> { (1, 2), (3, 4) };
        int total = 0;
        foreach (var (left, right) in pairs)
        {
            total += left * right;
        }

        return total;
    }

    public static int InFor()
    {
        int total = 0;
        for (var (i, j) = GetPoint(); i < 10; i++)
        {
            total += j;
        }

        return total;
    }

    public static (string, (int, int)) Normalize(string input)
    {
        var (key, value) = Split(input);
        return (key, (value, value * 2));
    }

    public static (string, int) Split(string input)
    {
        int index = input.IndexOf('=');
        return index < 0 ? (input, 0) : (input.Substring(0, index), int.Parse(input.Substring(index + 1)));
    }

    public static int NestedDeconstruction((string label, (int lo, int hi) range) box)
    {
        var (label, (lo, hi)) = box;
        return label.Length + lo + hi;
    }

    public static (int, int) TryGet(Dictionary<string, (int, int)> map, string key)
    {
        if (map.TryGetValue(key, out var pair) && pair.Item1 >= 0)
        {
            return pair;
        }

        return (0, 0);
    }

    public static bool IsVarPattern((int, int) value)
    {
        if (value is var (left, right))
        {
            return left < right;
        }

        return false;
    }
}
