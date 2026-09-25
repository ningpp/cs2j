namespace SampleRecordLib;

// RC-1/RC-2：record class set → init
public record class Person(string Name)
{
    public int Age { get; set; }
}

// RC-3：公共字段 → init 属性
public record class Configuration
{
    public string Key = "default";
}

// RC-4：私有字段保留可变
public record class Cache
{
    private int _cache;
    public string Key { get; set; }
    public int Compute() { _cache = 42; return _cache; }
}

// RC-6：继承层次一致
public record class Shape { public double Area { get; set; } }
public record class Circle : Shape { public double Radius { get; set; } }

// RC-8：层次不一致（事件阻断）
public record class BadBase { public event System.Action? Ev; public int X { get; set; } }
public record class BadDerived : BadBase { public int Y { get; set; } }
