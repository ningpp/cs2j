namespace CSharpToTypeScript.Tests;

/// <summary>
/// Round 19：无初始化器引用类型字段不再宽化 `T | null`——C# 非可空上下文的
/// 字段缺省非空（构造器赋值），发射 `= null!`（非空断言）保持类型 T 以满足
/// strictPropertyInitialization；显式 `= null` 初始化器（C# 真实可空）维持
/// `T | null` 宽化。根因：MSAGL 全库 TS2531（对象可能为 null，2922）+
/// TS18047（771）+ getter 内部 TS2322 均源自字段级 `| null` 宽化。
/// </summary>
public class TsNullAssertionRound19Tests : TsConversionTestBase
{
    [Fact]
    public void Field_Without_Initializer_Emits_NullAssertion_Instead_Of_Union_Null()
    {
        var result = Convert("""
public class Curve
{
    private readonly ICurve curve;
    public Curve(ICurve c) { this.curve = c; }
    public void Trim() { curve.Foo(); }
}
public interface ICurve { void Foo(); }
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private curve: ICurve = null!", result.GeneratedCode);
        Assert.DoesNotContain("ICurve | null", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Explicit_Null_Initializer_Keeps_Union_Null_Widening()
    {
        var result = Convert("""
public class Node
{
    public string Label = null;
    public Node Parent = null;
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Label: string | null = null", result.GeneratedCode);
        Assert.Contains("Parent: Node | null = null", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void ValueType_Defaults_Unchanged()
    {
        var result = Convert("""
public class Metrics
{
    private int Count;
    private bool Flag;
    private double Ratio;
    private int? Maybe;
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Count: number = 0", result.GeneratedCode);
        Assert.Contains("Flag: boolean = false", result.GeneratedCode);
        Assert.Contains("Ratio: number = 0", result.GeneratedCode);
        Assert.Contains("Maybe: number | null = null", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Property_Getter_Over_Assigned_Field_Compiles()
    {
        // Curve.segs 形态：C# 无初始化器只读字段，构造器赋值；getter 返回该字段。
        // （C# 索引器映射为 Java 风味 .get(i)，属下轮 CSharpList 兼容面，本用例
        // 只覆盖 getter 直返字段的 TS2531 内因。）
        var result = Convert("""
public class ShapeCollection
{
    private readonly Shape first;
    public ShapeCollection() { first = new Shape(); }
    public Shape First { get { return first; } }
}
public class Shape { }
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private first: Shape = null!", result.GeneratedCode);
        Assert.Contains("get First(): Shape", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void NullComparison_On_Assigned_Field_Compiles()
    {
        // null 比较在 TS 中恒合法（不触发 TS2367），字段非空后比较语义保持。
        var result = Convert("""
public class Holder
{
    private Node node;
    public void Reset(Node other) { node = other; }
    public bool Has() { return node != null; }
}
public class Node { }
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private node: Node = null!", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}
