namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 22 M2 验收（L3 冒烟编译，spec §5.5）：M1+M2 全部特性样本（类/字段/控制流/
/// 字面量/泛型/接口/属性/枚举/异常/插值/数值/可空）合并为一个多文件工程，整体
/// tsc --noEmit 编译——验证 compat 导入生成与 NodeNext 模块解析。每样本为独立 C#
/// 源 → 独立 .ts 文件（跨文件相对导入归 M5 NpmProjectPlan，本阶段文件自包含）；
/// 全部样本复用既有测试已验证的产物形态。无 Node 环境时基类空转通过。
/// </summary>
public class TsSmokeCompileTests : TsConversionTestBase
{
    [Fact]
    public void M1_M2_Feature_Samples_Compile_As_MultiFile_Project()
    {
        var generated = new List<(string FileName, string Code)>();
        foreach (var (fileName, source) in Samples)
        {
            var result = Convert(source);
            Assert.True(result.Success, $"{fileName} conversion failed:\n{result.GeneratedCode}");
            generated.Add((fileName, result.GeneratedCode));
        }

        // 首文件经主入口写入，其余经 additionalFiles 写入同一工程（tsconfig include *.ts）
        var additional = new List<string>();
        foreach (var (fileName, code) in generated.Skip(1))
        {
            additional.Add(fileName);
            additional.Add(code);
        }
        AssertCompilesWithCompat(generated[0].Code, additional.ToArray());
    }

    private static readonly (string FileName, string Source)[] Samples = new[]
    {
        // ─── M1：类/字段/方法/字面量/控制流 ───────────────────────────
        ("SmokeBasics.ts", """
class SmokeBasics
{
    int count = 3;
    string name = "cs2j";
    double ratio = 1.5;
    bool done = false;
    long big = 1234567890123L;
    char letter = 'A';

    int Sum(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i = i + 1)
        {
            total = total + i;
        }
        int guard = 0;
        while (guard < 10)
        {
            guard = guard + 1;
        }
        do
        {
            total = total + 1;
        } while (total < 100);
        return total;
    }

    string Describe(int n)
    {
        switch (n)
        {
            case 1:
                return "one";
            case 2:
                return "two";
            default:
                return "other";
        }
    }

    void Report(int n)
    {
        if (n > 0) { Console.WriteLine("positive"); }
        else { Console.WriteLine("non-positive"); }
    }

    bool IsEven(int n) { return n % 2 == 0; }
}
"""),

        // ─── M2：泛型 ────────────────────────────────────────────────
        ("SmokeGenerics.ts", """
class Container<T>
{
    T value;
}

class Pair<K, V>
{
    K key;
    V value;
}

class Utils
{
    T Identity<T>(T x)
    {
        return x;
    }

    T Max<T>(T a, T b) where T : IComparable
    {
        return a;
    }
}
"""),

        // ─── M2：接口 ────────────────────────────────────────────────
        ("SmokeInterfaces.ts", """
interface IShape
{
    double Area();
    string Name { get; }
}

class Circle : IShape
{
    double radius;

    public Circle(double r) { this.radius = r; }

    public double Area() { return 3.14159 * this.radius * this.radius; }

    public string Name { get { return "circle"; } }
}
"""),

        // ─── M2：属性 ────────────────────────────────────────────────
        ("SmokeProperties.ts", """
class SmokePoint
{
    int X { get; set; }
    int Y { get; }
    int Z { get; private set; }

    public SmokePoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public void Move(int dx) { X = X + dx; }
}
"""),

        // ─── M2：枚举（普通 + Flags） ────────────────────────────────
        ("SmokeEnums.ts", """
enum SmokeColor { Red = 1, Green = 2, Blue = 4 }

[System.Flags]
enum SmokeStyle { None = 0, Bold = 1, Italic = 2 }

class SmokeEnumUser
{
    SmokeColor c = SmokeColor.Red;
    SmokeStyle s = SmokeStyle.Bold | SmokeStyle.Italic;

    bool Has(SmokeStyle f) { return this.s.HasFlag(f); }
}
"""),

        // ─── M2：异常（compat 类 + instanceof 链） ────────────────────
        ("SmokeExceptions.ts", """
class SmokeGuard
{
    int Parse(string s)
    {
        try
        {
            if (s == null) { throw new ArgumentException("empty"); }
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            return -1;
        }
        catch (Exception ex)
        {
            return -2;
        }
        finally
        {
        }
    }

    void Fail(string p)
    {
        if (p == null) { throw new ArgumentNullException("p"); }
    }
}
"""),

        // ─── M2：字符串插值 / String.Format ──────────────────────────
        ("SmokeStrings.ts", """
class SmokeStrings
{
    string Greet(string name, int n) { return $"hello {name}, count={n}"; }

    string Format(string name, int n) { return string.Format("{0} = {1}", name, n); }
}
"""),

        // ─── M2：数值语义 ────────────────────────────────────────────
        ("SmokeNumerics.ts", """
class SmokeNumerics
{
    int Div(int a, int b) { return a / b; }

    double Half(double d) { return d / 2; }

    byte LowByte(int v) { return (byte)v; }

    long ToLong(int v) { return (long)v; }

    long Big() { return 7L + 1; }
}
"""),

        // ─── M2：可空值类型 ──────────────────────────────────────────
        ("SmokeNullables.ts", """
class SmokeNullables
{
    int? a = null;
    int? b = 5;
    long? d = 7;

    int M(int? p)
    {
        if (p.HasValue) { return p.Value; }
        return 0;
    }

    int? Coalesce(int? x, int? y) { return x ?? y; }

    void Assign(int? x) { x ??= 5; }
}
"""),
    };
}
