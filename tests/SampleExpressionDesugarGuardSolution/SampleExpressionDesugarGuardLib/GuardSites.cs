using System.Numerics;

namespace SampleExpressionDesugarGuardLib;

/// <summary>
/// C36 CE-GUARD strict fixture (M3): interface static abstract/virtual
/// members are legal C# 11/12 and this solution compiles natively, but Java
/// has no equivalent feature. The expression-desugar pass must inventory the
/// declarations and atomically fail the strict chain (CS2J0585) without
/// rewriting any file.
/// </summary>
public interface IMonoid<TSelf> where TSelf : IMonoid<TSelf>
{
    static abstract TSelf Identity { get; }

    static abstract TSelf Combine(TSelf left, TSelf right);

    static abstract TSelf operator +(TSelf left, TSelf right);

    static virtual string Describe() => typeof(TSelf).Name;
}

/// <summary>
/// Implementations are ordinary static members: not abstract/virtual, so they
/// must NOT be counted as 0585 declaration sites. The generic-math operator
/// call below resolves against the implementing struct, not an interface.
/// </summary>
public readonly record struct Vec2(int X, int Y) : IMonoid<Vec2>, IAdditionOperators<Vec2, Vec2, Vec2>
{
    public static Vec2 Identity => new(0, 0);

    public static Vec2 Combine(Vec2 left, Vec2 right) => left + right;

    public static Vec2 operator +(Vec2 left, Vec2 right)
        => new(left.X + right.X, left.Y + right.Y);
}
