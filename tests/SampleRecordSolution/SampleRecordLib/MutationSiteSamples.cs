namespace SampleRecordLib;

// MS-1：简单成员赋值 → with
public record struct Mutable(int X, int Y)
{
    public static Mutable Bump(Mutable m)
    {
        m.X = 1;
        m.Y = 2;
        return m;
    }
}

// MS-7：集合元素
public record struct Item(int Id);
public static class Container
{
    public static void BumpAll(System.Collections.Generic.List<Item> items)
    {
        items[0] = new Item(42);
    }
}
