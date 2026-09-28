using SampleRefOutInV2Lib;
using Xunit;

namespace SampleRefOutInV2Tests;

public class ByRefSemanticsTests
{
    [Fact]
    public void Swap_WritesBackBothSides()
    {
        int a = 1, b = 2;
        Calculator.Swap(ref a, ref b);
        Assert.Equal(2, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void TryGet_WritesOutParam()
    {
        var calc = new Calculator();
        Assert.True(calc.TryGet(out var result));
        Assert.Equal(42, result);
    }

    [Fact]
    public void TryParse_DeclarationOut()
    {
        Assert.True(Calculator.TryParse("x", out var p));
        Assert.Equal(0, p.X);
    }

    [Fact]
    public void UpdateField_WritesArrayElement()
    {
        var calc = new Calculator();
        int[] arr = { 5, 6 };
        calc.UpdateField(ref arr[0]);
        Assert.Equal(6, arr[0]);
    }

    [Fact]
    public void RefReturn_PreservesAliasing()
    {
        var calc = new Calculator();
        int[] arr = { 7, 8 };
        ref int slot = ref calc.GetRef(arr, 1);
        slot = 100;
        Assert.Equal(100, arr[1]);
    }

    [Fact]
    public void GenericHolder_RefAndOutRoundTrip()
    {
        var holder = new GenericHolder<int>();
        holder.Value = 10;
        holder.Update(ref holder.Value);
        Assert.Equal(10, holder.Value);
        Assert.True(holder.TryGet(out var v));
        Assert.Equal(10, v);
    }
}
