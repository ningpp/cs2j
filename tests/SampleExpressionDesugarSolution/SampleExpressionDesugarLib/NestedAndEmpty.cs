using System;
using System.Collections.Generic;

namespace SampleExpressionDesugarLib;

public sealed class OrderLine
{
    public int ProductId { get; set; }

    public int Quantity { get; set; }
}

public sealed class Order
{
    public string Name { get; set; } = string.Empty;

    public List<OrderLine> Lines { get; set; } = [];

    public IReadOnlyList<string> Tags { get; set; } = [];
}

/// <summary>Nested DTO construction: mixed targets across parent/child sites.</summary>
public static class NestedBuild
{
    public static Order Build()
    {
        string[] extraTags = ["c", "d"];

        // Spread site is lifted at its own local declaration: inside the
        // object initializer below, the preceding 'new Order()' side effect
        // makes statement lifting unobservable (CS2J0583 territory).
        IReadOnlyList<string> tags = ["a", "b", .. extraTags];

        return new Order
        {
            Name = "o",
            Lines =
            [
                new OrderLine { ProductId = 1, Quantity = 2 },
                new OrderLine { ProductId = 3, Quantity = 1 },
            ],
            Tags = tags,
        };
    }
}

/// <summary>
/// Empty collection matrix (F): <c>[]</c> and <c>[..empty]</c> across the
/// four target shapes.
/// </summary>
public static class EmptyForms
{
    public static int[] EmptyArray() => [];

    public static List<int> EmptyList() => [];

    public static IEnumerable<int> EmptyInterface() => [];

    public static int EmptySpanLength()
    {
        ReadOnlySpan<int> values = [];
        return values.Length;
    }

    public static int[] SpreadEmptyArray(int[] empty) => [.. empty];

    public static List<int> SpreadEmptyList(List<int> empty) => [.. empty];

    public static IEnumerable<int> SpreadEmptyInterface(IEnumerable<int> empty) => [.. empty];

    public static int SpreadEmptySpanLength(int[] empty)
    {
        ReadOnlySpan<int> values = [.. empty];
        return values.Length;
    }
}
