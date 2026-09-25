namespace SampleRecordLib;

// RS-1：positional record struct → readonly record struct
public record struct Point(int X, int Y)
{
    // RS-3：自动属性 set → init
    public int Z { get; set; }
}

// RS-5：公共字段 → init 属性
public record struct WithField
{
    public WithField() { } // CS8983：带字段初始值设定项的 struct 需显式构造函数
    public int Value = 42;
}

// RS-18：已是 readonly → 跳过
public readonly record struct AlreadyImmutable(int X);

// RS-10：实例事件 → FB-1 跳过
public record struct WithEvent
{
    public event System.Action? Changed;
    public int X { get; set; }
}
