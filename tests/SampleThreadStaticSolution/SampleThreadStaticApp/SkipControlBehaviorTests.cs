using SampleThreadStaticLib;
using Xunit;

namespace SampleThreadStaticApp;

/// <summary>
/// Cases 6/7 behavior: the two skip controls keep their attribute and normal
/// semantics. The rewritten intermediate tree must still compile and run here.
/// </summary>
public sealed class SkipControlBehaviorTests
{
    [Fact]
    public void GenericContainerControl_RetainsNormalPerThreadAndPerClosedTypeSemantics()
    {
        var firstValue = 0;
        var secondValue = 0;
        var t1 = new Thread(() =>
        {
            GenericBox<object>.Bump();
            firstValue = GenericBox<object>.Current;
        });
        var t2 = new Thread(() =>
        {
            GenericBox<object>.Bump();
            GenericBox<object>.Bump();
            secondValue = GenericBox<object>.Current;
        });
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();

        Assert.Equal(1, firstValue);
        Assert.Equal(2, secondValue);

        var closedTypeOther = GenericBox<string>.Current;
        Assert.Equal(0, closedTypeOther);

        var observed = -1;
        var observer = new Thread(() => observed = GenericBox<object>.Current);
        observer.Start();
        observer.Join();
        Assert.Equal(0, observed);
    }

    [Fact]
    public void InstanceControl_BehavesOnSameThreadAndInstance()
    {
        var box = new InstanceSkip();
        box.Set(7);
        Assert.Equal(7, box.Get());

        var other = new InstanceSkip();
        Assert.Equal(0, other.Get());
    }
}
