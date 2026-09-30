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
