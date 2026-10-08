using SampleStructValueSemanticsLib;
using Xunit;

namespace SampleStructValueSemanticsTests;

/// <summary>
/// 中间树 verify 阶段执行：struct-value-semantics 改写后值语义保持
/// （防御性 clone 是行为等价变换，这些断言在改写前后都必须成立）。
/// </summary>
public class ValueSemanticsTests
{
    [Fact]
    public void LocalAssignment_IsolatesMutation()
    {
        var a = new Point(1, 2);
        var b = a;
        b.Translate(10, 10);
        Assert.Equal(1, a.X);
        Assert.Equal(2, a.Y);
    }

    [Fact]
    public void ValueArgument_DoesNotWriteBack()
    {
        var p = new Point(4, 4);
        Geometry.MutateCopy(p);
        Assert.Equal(4, p.X);
    }

    [Fact]
    public void InterfaceCall_DoesNotMutateOriginal()
    {
        var p = new Point(1, 1);
        Geometry.Move(p);
        Assert.Equal(1, p.X);
    }

    [Fact]
    public void ArrayElementLoad_DoesNotWriteBackThroughHelper()
    {
        var points = new[] { new Point(2, 2) };
        Geometry.MutateArrayFirst(points);
        Assert.Equal(2, points[0].X);
    }

    [Fact]
    public void BoxedCopy_IsIsolated()
    {
        var p = new Point(7, 7);
        object boxed = Geometry.Box(p);
        var unboxed = Geometry.Unbox(boxed);
        unboxed.Translate(-7, -7);
        Assert.Equal(7, ((Point)boxed).X);
    }

    [Fact]
    public void NestedStructField_CloneIsDeep()
    {
        var line = new Line(new Point(1, 1), new Point(2, 2));
        var copy = line;
        copy.Start.Translate(10, 10);
        Assert.Equal(1, line.Start.X);
        Assert.Equal(2, line.End.X);
    }

    [Fact]
    public void WithRetries_LeavesOriginalUntouched()
    {
        var settings = new BuildSettings { Retries = 1 };
        var next = settings.WithRetries(8);
        Assert.Equal(1, settings.Retries);
        Assert.Equal(8, next.Retries);
    }

    [Fact]
    public void ExistingClone_IsReused()
    {
        var r1 = new Reusable { V = 3 };
        var r2 = r1;
        Assert.Equal(3, r2.V);
    }

    [Fact]
    public void ShiftAll_WritesBackExplicitly()
    {
        var poly = new Polygon(new[] { new Point(0, 0) });
        poly.ShiftAll(1, 2);
        Assert.Equal(1, poly.Vertices[0].X);
        Assert.Equal(2, poly.Vertices[0].Y);
    }
}

/// <summary>
/// ref struct：SV011 Info，交由 C01/C06 处理。该类型 Java 不可表示（UF050：
/// stack-only、不可装箱），按 unsafe-ffm 的残余契约放在测试工程——测试工程是
/// .NET 侧行为预言机、不产出 Java，其内的 UF050 降级为 Warning，源文件原样
/// 通过 C# verify（生产工程中的用户 ref struct 仍是硬门错误）。
/// </summary>
public ref struct StackBuf
{
    public Span<byte> Bytes;

    public StackBuf(Span<byte> bytes)
    {
        Bytes = bytes;
    }

    public int Length => Bytes.Length;
}
