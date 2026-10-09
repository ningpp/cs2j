namespace SampleIndexRangeLib;

/// <summary>
/// C31 index/range 语义矩阵：覆盖数组范围切片（compat ArrayHelper.slice
/// 的全部 9 个重载域：8 个基元数组 + 引用数组）、四种边界形态、hat 单索引、
/// 字符串范围切片。预处理后全部归一为 (offset, count) 整型调用。
/// </summary>
public static class SliceOps
{
    // ---- 每个 slice 重载域一个范围切片站点 ----

    public static int[] IntMiddle(int[] a) => a[1..^1];

    public static long[] LongMiddle(long[] a) => a[1..^1];

    public static short[] ShortMiddle(short[] a) => a[1..^1];

    public static byte[] ByteMiddle(byte[] a) => a[1..^1];

    public static double[] DoubleMiddle(double[] a) => a[1..^1];

    public static float[] FloatMiddle(float[] a) => a[1..^1];

    public static bool[] BoolMiddle(bool[] a) => a[1..^1];

    public static char[] CharMiddle(char[] a) => a[1..^1];

    public static string[] RefMiddle(string[] a) => a[1..^1];

    // ---- 边界形态 ----

    public static int[] OpenLeft(int[] a) => a[1..];

    public static int[] OpenRight(int[] a) => a[..3];

    public static int[] Full(int[] a) => a[..];

    public static int[] BothFromEnd(int[] a) => a[^2..^1];

    /// <summary>hat-left + 开右区间：count = len - (len-2)，右复合操作数必须带括号。</summary>
    public static int[] OpenLeftFromEnd(int[] a) => a[^2..];

    // ---- hat 单索引（纯算术，不走 slice） ----

    public static int LastInt(int[] a) => a[^1];

    public static char LastChar(string s) => s[^1];

    /// <summary>复合 hat 操作数：a[^(n+1)] 必须归一为 a.Length - (n+1)。</summary>
    public static int HatCompound(int[] a, int n) => a[^(n + 1)];

    // ---- 字符串范围切片 -> Substring ----

    public static string StrMiddle(string s) => s[1..^1];

    public static string StrTail(string s) => s[1..];

    public static string StrHead(string s) => s[..3];

    public static string StrFromEnd(string s) => s[^2..];

    /// <summary>
    /// C35+C31 联合站点：<c>a?[^1]</c> 先由 NullConditionalDesugar 去糖为
    /// 含普通元素访问的三元，index-range 链位在后接力降级（隔离跑 C31
    /// 时保持 IR130 指路）。
    /// </summary>
    public static int? LastOrNull(int[]? a) => a?[^1];
}
