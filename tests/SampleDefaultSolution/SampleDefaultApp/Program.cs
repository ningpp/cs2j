using System.Collections.Generic;
using SampleDefaultLib;

namespace SampleDefaultApp;

public class Program
{
    // 字段初始化位置：基本类型与可空
    private int _i = default;
    private long _l = default;
    private short _s = default;
    private byte _b = default;
    private float _fl = default;
    private double _d = default;
    private bool _bo = default;
    private char _c = default;
    private decimal _dec = default;
    private string? _str = default;
    private int? _n = default;
    private Dictionary<int, string>? _map = default;
    private int[]? _arr = default;

    public static void Main() { }

    // 方法返回值位置
    public int GetInt() => default;
    public long GetLong() => default;
    public double GetDouble() => default;
    public bool GetBool() => default;
    public char GetChar() => default;
    public decimal GetDecimal() => default;
    public string? GetString() => default;
    public int? GetNullable() => default;
    public Dictionary<int, string>? GetMap() => default;

    // 方法参数默认值位置
    public void Run(int[] arr = default) { }

    // 数组元素位置
    public int[] MakeArray() => new int[] { default };
}
