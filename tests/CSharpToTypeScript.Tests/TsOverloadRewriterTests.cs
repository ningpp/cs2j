namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 23（spec §5.4 TsOverloadRewriterTests）：方法重载类型后缀改名 + 调用点同步；
/// 构造器重载 → Create$N 静态工厂 + new 调用点改写；无歧义不改名；无法静态判定 → 诊断。
/// 全部经 Convert 端到端断言（syntax 层 rewriter 产物经共享 visitor 流入 IR/raw 文本）。
/// 注：方法体内未限定的实例成员引用不注入 this.（已知限制，Task 22 已登记），
/// 样本统一用显式 this./接收者形态（既有测试已验证形态）。
/// </summary>
public class TsOverloadRewriterTests : TsConversionTestBase
{
    [Fact]
    public void Two_Overloads_Renames_Second_With_Type_Suffix_And_Syncs_CallSites()
    {
        var result = Convert("""
class Calc
{
    public int Parse(string s) { return 0; }
    public int Parse(string s, int style) { return 0; }

    public int Use(string s, int style) { return this.Parse(s, style); }
    public int Simple(string s) { return this.Parse(s); }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        // 声明：首个（参数数升序）保留 Parse → parse；第二个追加类型后缀
        Assert.Contains("parse(s: string): number", result.GeneratedCode);
        Assert.Contains("parse_string_number(s: string, style: number): number", result.GeneratedCode);
        // 调用点同步改写（含跨方法）
        Assert.Contains("return this.parse_string_number(s, style);", result.GeneratedCode);
        Assert.Contains("return this.parse(s);", result.GeneratedCode);
    }

    [Fact]
    public void Same_Arity_Overloads_Disambiguate_By_Static_Argument_Type()
    {
        var result = Convert("""
class Conv
{
    public string Fmt(int x) { return "i"; }
    public string Fmt(string x) { return "s"; }

    public string A(int x) { return this.Fmt(x); }
    public string B(string s) { return this.Fmt(s); }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        // 同参数量：稳定序首个（Fmt(int)）保留，Fmt(string) → fmt_string
        Assert.Contains("fmt(x: number): string", result.GeneratedCode);
        Assert.Contains("fmt_string(x: string): string", result.GeneratedCode);
        Assert.Contains("return this.fmt(x);", result.GeneratedCode);
        Assert.Contains("return this.fmt_string(s);", result.GeneratedCode);
    }

    [Fact]
    public void Cross_Type_Instance_Call_Sites_Are_Rewritten()
    {
        var result = Convert("""
class Host
{
    public int Parse(int x) { return 0; }
    public int Parse(int x, int y) { return 0; }
}

class Caller
{
    public int Go(Host h) { return h.Parse(1, 2); }
    public int One(Host h) { return h.Parse(1); }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("return h.parse_number_number(1, 2);", result.GeneratedCode);
        Assert.Contains("return h.parse(1);", result.GeneratedCode);
    }

    [Fact]
    public void Generic_Method_Overload_Uses_Type_Parameter_Token()
    {
        var result = Convert("""
class Gen
{
    public T Pick<T>(T a) { return a; }
    public T Pick<T>(T a, T b) { return a; }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("pick<T>(a: T): T", result.GeneratedCode);
        Assert.Contains("pick_t_t<T>(a: T, b: T): T", result.GeneratedCode);
    }

    [Fact]
    public void Single_Implementation_Is_Not_Renamed()
    {
        var result = Convert("""
class Solo
{
    public int Sum(int a, int b) { return a + b; }
    public int Use() { return this.Sum(1, 2); }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("sum(a: number, b: number): number", result.GeneratedCode);
        Assert.Contains("return this.sum(1, 2);", result.GeneratedCode);
        Assert.DoesNotContain("sum_number", result.GeneratedCode);
    }

    [Fact]
    public void Ctor_Overloads_Become_CreateFactories_And_New_CallSites_Are_Rewritten()
    {
        var result = Convert("""
class Point
{
    int x = 1;
    int y = 2;

    public Point(int x) { this.x = x; }
    public Point(int x, int y) : this(x) { this.y = y; }
}

class User
{
    Point a = new Point(5);
    Point b = new Point(5, 6);

    Point Make(int x) { return new Point(x); }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        // 合成私有无参构造器（承接字段默认值提升）
        Assert.Contains("private constructor()", result.GeneratedCode);
        // 两个工厂：参数数升序 → Create$1 / Create$2
        Assert.Contains("static Create$1(x: number): Point {", result.GeneratedCode);
        Assert.Contains("static Create$2(x: number, y: number): Point {", result.GeneratedCode);
        Assert.Contains("const self = new Point();", result.GeneratedCode);
        // this(x) 委托内联：先委托体（self.x = x）后自身（self.y = y）
        var idxX = result.GeneratedCode.IndexOf("self.x = x;", StringComparison.Ordinal);
        var idxY = result.GeneratedCode.IndexOf("self.y = y;", StringComparison.Ordinal);
        Assert.True(idxX >= 0 && idxY > idxX, result.GeneratedCode);
        // new 调用点改写：字段初始化器 + 方法体内
        Assert.Contains("= Point.Create$1(5);", result.GeneratedCode);
        Assert.Contains("= Point.Create$2(5, 6);", result.GeneratedCode);
        Assert.Contains("return Point.Create$1(x);", result.GeneratedCode);
        // 不再出现裸 new Point(...)
        Assert.DoesNotContain("new Point(5", result.GeneratedCode);
        Assert.DoesNotContain("new Point(x)", result.GeneratedCode);
    }

    [Fact]
    public void Unresolvable_Overload_Call_Emits_Diagnostic_And_Keeps_Original_Name()
    {
        var result = Convert("""
class Amb
{
    public int M(int x) { return 0; }
    public int M(string x) { return 0; }

    public int Go(object o) { return M(o); }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        AssertDiagnostic(result, "无法静态判定");
        // 保留原名（camelCase 后为 m），调用点未改写为任一重载后缀名
        // Round 16：裸方法调用补 this. 前缀（与 TS2663 收敛一致）
        Assert.Contains("return this.m(o);", result.GeneratedCode);
        Assert.DoesNotContain("return m_number(o);", result.GeneratedCode);
        Assert.DoesNotContain("return m_string(o);", result.GeneratedCode);
    }

    [Fact]
    public void Overload_And_Factory_Output_Compiles_With_Tsc()
    {
        var result = Convert("""
class Calc
{
    public int Parse(string s) { return 0; }
    public int Parse(string s, int style) { return 0; }
    public int Use(string s, int style) { return this.Parse(s, style); }
}

class Point
{
    int x = 1;
    int y = 2;

    public Point(int x) { this.x = x; }
    public Point(int x, int y) : this(x) { this.y = y; }
}

class User
{
    Point a = new Point(5);
    Point b = new Point(5, 6);

    Point Make(int x) { return new Point(x); }
    int Go(Calc c, string s, int n) { return c.Parse(s, n); }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Ctor_Delegation_Replaces_Args_In_Value_Positions_Only()
    {
        // 镜像 MSAGL Rectangle(IEnumerable<Point>) : this(0, 0, new Point(-1, -1))：
        // 委托实参只替换值位（self.left = 0;），成员位（self.<name>）不得替换——
        // 此前整条语句退化成 self.0 = 0; / self.new Point(-1, -1) = ...。
        var result = Convert("""
class Point
{
    public Point(double x, double y) { }
}

class Box
{
    double left;
    double bottom;
    Point sizeF;

    public Box(Point[] points) : this(0, 0, new Point(-1, -1)) { }
    public Box(double left, double bottom, Point sizeF)
    {
        this.left = left;
        this.bottom = bottom;
        this.sizeF = sizeF;
    }
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.True(result.GeneratedCode.Contains("self.left = 0;", StringComparison.Ordinal), result.GeneratedCode);
        Assert.True(result.GeneratedCode.Contains("self.bottom = 0;", StringComparison.Ordinal), result.GeneratedCode);
        Assert.True(result.GeneratedCode.Contains("self.sizeF = new Point(-1, -1);", StringComparison.Ordinal), result.GeneratedCode);
        Assert.DoesNotContain("self.0 = 0;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Ctor_Delegation_With_Lambda_And_Object_Creation_Args()
    {
        // 镜像 MSAGL InitialLayoutByCluster : this(graph, anyCluster => defaultSettings)
        // 与 RBTree : this(new ComparerOnDelegate<T>(func))：lambda / new 表达式实参
        // 只进值位——此前成员位也被替换成 self.anyCluster => ... = ... 与
        // self.new Holder(func) = ...。
        var result = Convert("""
class Holder
{
    public Holder(System.Func<int, int> f) { }
}

class Tree
{
    Holder holder;

    public Tree(System.Func<int, int> func) : this(new Holder(func)) { }
    public Tree(Holder holder) { this.holder = holder; }
}

class Layout
{
    public Layout() : this(n => n + 1) { }
    public Layout(System.Func<int, int> settings) { this.settings = settings; }
    System.Func<int, int> settings;
}
""");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.True(result.GeneratedCode.Contains("self.holder = new Holder(func);", StringComparison.Ordinal), result.GeneratedCode);
        Assert.True(result.GeneratedCode.Contains("self.settings = n => n + 1;", StringComparison.Ordinal), result.GeneratedCode);
        Assert.DoesNotContain("self.new Holder", result.GeneratedCode);
        Assert.DoesNotContain("self.n => n + 1", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}

