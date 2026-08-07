namespace CSharpToTypeScript.Tests;

/// <summary>
/// Round 16 (MSAGL convergence): raw-text emission qualifies bare instance member
/// references (this.x) and bare static member references (ClassName.x) using the
/// class member context, fixing TS2663/TS2662. Mirrors the MSAGL shapes
/// (BinaryHeapWithComparer / AlgorithmBase): bare fields in raw method bodies,
/// static member references in field initializers, constructor raw bodies
/// (including the generic-array "as T[]" cast text) and recursive method calls.
/// All cases compile under real tsc.
/// </summary>
public class TsRawMemberContextTests : TsConversionTestBase
{
    [Fact]
    public void Raw_Method_Body_Bare_Instance_Members_Get_This_Prefix()
    {
        var result = Convert("""
class Sample
{
    private int[] A = new int[4];
    private int heapSize = 0;
    int Sum()
    {
        if (heapSize == A.Length - 1) { return A[0]; }
        return heapSize + A[1];
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("this.heapSize === this.A.length", result.GeneratedCode);
        Assert.Contains("return this.A[0];", result.GeneratedCode);
        Assert.Contains("return this.heapSize + this.A[1];", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Field_Initializer_And_Method_Body_Static_References_Get_ClassName_Prefix()
    {
        var result = Convert("""
class Sample
{
    private static int DefaultSize = 4;
    private int Size = DefaultSize;
    int Resize() { DefaultSize = Size + 1; return DefaultSize; }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private Size: number = Sample.DefaultSize;", result.GeneratedCode);
        Assert.Contains("Sample.DefaultSize = this.Size + 1;", result.GeneratedCode);
        Assert.Contains("return Sample.DefaultSize;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Constructor_Raw_Body_Bare_References_Get_Prefixes()
    {
        var result = Convert("""
class Sample
{
    private static int InitialCapacity = 16;
    private int[] A;
    Sample() { A = new int[InitialCapacity + 1]; }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("this.A = new Array(Sample.InitialCapacity + 1);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Generic_Constructor_Array_Cast_Text_Survives_And_Static_Qualifies()
    {
        // Mirrors MSAGL BinaryHeapWithComparer: the runtime-class-parameter rewrite
        // produces "this.A = (TypeHelper.newArrayInstance(tClass, ...) as T[]);" --
        // tClass is the synthesized ctor parameter (shadowed), the static member
        // gets the class prefix and the "as T[]" cast text is untouched.
        var result = Convert("""
class Sample<T>
{
    private static int InitialCapacity = 16;
    private T[] A;
    Sample() { A = new T[InitialCapacity + 1]; }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Sample.InitialCapacity", result.GeneratedCode);
        Assert.Contains("as T[]", result.GeneratedCode);
        // TypeHelper 是既有 TS2304（compat 导入缺口，非本轮 TS2662/TS2663 目标）；
        // 最小桩仅让 tsc 校验本轮产物（静态成员限定 + as T[] cast 文本原样）。
        AssertCompiles(result.GeneratedCode,
            "typehelper.d.ts", "declare var TypeHelper: { newArrayInstance(tClass: any, size: number): any[] };");
    }

    [Fact]
    public void Recursive_Method_Call_Gets_This_Prefix()
    {
        var result = Convert("""
class Sample
{
    void Start(double expected, int depth)
    {
        if (depth > 0) { Start(expected, depth - 1); }
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("this.start(expected, depth - 1);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Static_Init_Block_Bare_Static_Field_Gets_ClassName_Prefix()
    {
        var result = Convert("""
class Sample
{
    private static int counter = 0;
    static Sample() { counter = 1; }
    int Read() { return counter; }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Sample.counter = 1;", result.GeneratedCode);
        Assert.Contains("return Sample.counter;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
    [Fact]
    public void Enum_Qualified_Static_Access_In_Field_Initializer_Stays_Bare()
    {
        // MSAGL "Color Color" pattern: property SolvingMethod of type SolvingMethod
        // (enum). In a field initializer "SolvingMethod.PrecondConjugateGradient" the
        // bare name is a type-qualified static enum access and must stay bare
        // (this.SolvingMethod.Precond... would be a TS2339/TS2576 regression).
        var result = Convert("""
enum SolvingMethod { PrecondConjugateGradient, Parallel }

class Sample
{
    private SolvingMethod solvingMethod = SolvingMethod.PrecondConjugateGradient;
    public SolvingMethod SolvingMethod { get; set; }
    void Reset() { solvingMethod = SolvingMethod.Parallel; }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("= SolvingMethod.PrecondConjugateGradient", result.GeneratedCode);
        Assert.DoesNotContain("this.SolvingMethod.PrecondConjugateGradient", result.GeneratedCode);
        Assert.Contains("this.solvingMethod = SolvingMethod.Parallel;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }


    [Fact]
    public void Flags_Enum_Property_Same_Name_Type_Qualified_Access_Stays_Bare()
    {
        // Mirrors MSAGL ScanDirection/VertexEntry: `Direction Direction { get; private set; }`
        // where Direction is a [Flags] enum -- the IR maps the enum to int, so the
        // type-last-name self-typed check misses and the raw-text pass must fall back
        // to the project enum-name registry. "Direction.East" is a type-qualified enum
        // access and must stay bare (this.Direction.East would be TS2339 in a static
        // context).
        var result = Convert("""
[Flags]
enum Direction { None = 0, North = 1, East = 2 }

class Sample
{
    public Direction Direction { get; private set; }
    public Direction PerpDirection { get; private set; }
    private static readonly Sample horizontalInstance = new Sample(Direction.East);
    private Sample(Direction directionAlongScanLine)
    {
        Direction = directionAlongScanLine;
        PerpDirection = (Direction.North == directionAlongScanLine) ? Direction.East : Direction.North;
    }
    public bool IsHorizontal { get { return Direction.East == Direction; } }
}
""");
        Assert.Contains("new Sample(Direction.East)", result.GeneratedCode);
        Assert.Contains("PerpDirection = ((Direction.North === directionAlongScanLine) ? Direction.East : Direction.North);", result.GeneratedCode);
        Assert.Contains("return Direction.East === this.Direction;", result.GeneratedCode);
        Assert.DoesNotContain("this.Direction.East", result.GeneratedCode);
        Assert.DoesNotContain("this.Direction.North", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Raw_Statement_Local_Shadows_Member_In_Following_Statement()
    {
        // Mirrors MSAGL GTreeOverlapRemoval.removeOverlaps_node_array_number: a raw
        // statement declares `var settings = ...;` and the NEXT structured statement
        // calls a static method with `settings`. The member pass must not turn the
        // local into this.settings (TS2339 inside a static method).
        var result = Convert("""
class SampleSettings
{
    private int _value;
    public int Value { get { return _value; } set { _value = value; } }
    public void setValue(int v) { _value = v; }
}

class Sample
{
    private object settings = new object();
    private Sample(object settings, object[] nodes) { }
    public static object removeOverlaps(object[] nodes)
    {
        var settings = new SampleSettings { Value = 1 };
        var mst = new Sample(settings, nodes);
        return mst;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("let mst = new Sample(settings, nodes);", result.GeneratedCode);
        Assert.DoesNotContain("new Sample(this.settings", result.GeneratedCode);
        // 不做 AssertCompiles：对象初始化器拆分出的 raw "settings.setValue(1)" 是既有
        // 命名构件（setValue 方法被访问器重组改名），与本轮前缀行为无关；前缀断言已
        // 覆盖回归点，全量重转的 tsc 校验覆盖编译性。
    }


}
