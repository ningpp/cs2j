namespace SampleDefaultLib;

public struct Point
{
    public int X;
    public int Y;
}

public enum Status
{
    Full = 0,
    Empty = 1,
}

public enum Color
{
    Red = 1,
    Green = 2,
    Blue = 3,
}

[Flags]
public enum Permissions
{
    None = 0,
    Read = 1,
    Write = 2,
}

public class StructContainer<T> where T : struct
{
    public T Value = default;
}

public class ClassContainer<T> where T : class
{
    public T? Value = default;
}

public class FreeContainer<T>
{
    public T Value = default;

    public T Get() => default;
}

public class Defaults
{
    // 字段初始化位置
    private Point _p = default;
    private Status _s = default;
    private Permissions _f = default;
    private int? _n = default;
    private (int, string) _t = default;

    // 方法返回值位置
    public Point GetPoint() => default;
    public Status GetStatus() => default;
    public Permissions GetFlags() => default;
    public (int, string) GetTuple() => default;

    // 泛型方法（无约束）返回值位置
    public T GetDefault<T>() => default;

    // 方法参数默认值位置
    public void Process(int x = default) { }

    // 数组元素位置
    public int[] MakeArray() => new[] { default(int) };
}
