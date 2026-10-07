using Xunit;

namespace SampleIndexRangeTests;

/// <summary>
/// 在 index-range 改写后的中间树上运行：固定 (offset,count) 归一化与
/// Cs2jArray.ArrayHelper.Slice / Substring 的 .NET 运行时语义。
/// </summary>
public sealed class SliceOpsTests
{
    [Fact]
    public void ArrayRanges_NormalizeToOffsetCount()
    {
        int[] a = [0, 1, 2, 3, 4];
        Assert.Equal(new[] { 1, 2, 3 }, SampleIndexRangeLib.SliceOps.IntMiddle(a));
        Assert.Equal(new[] { 1, 2, 3, 4 }, SampleIndexRangeLib.SliceOps.OpenLeft(a));
        Assert.Equal(new[] { 0, 1, 2 }, SampleIndexRangeLib.SliceOps.OpenRight(a));
        Assert.Equal(a, SampleIndexRangeLib.SliceOps.Full(a));
        Assert.Equal(new[] { 3 }, SampleIndexRangeLib.SliceOps.BothFromEnd(a));
        Assert.Equal(new[] { 3, 4 }, SampleIndexRangeLib.SliceOps.OpenLeftFromEnd(a));
        Assert.Equal(4, SampleIndexRangeLib.SliceOps.LastInt(a));
        Assert.Equal(2, SampleIndexRangeLib.SliceOps.HatCompound(a, 2));
    }

    [Fact]
    public void StringRanges_NormalizeToSubstring()
    {
        Assert.Equal("bc", SampleIndexRangeLib.SliceOps.StrMiddle("abcd"));
        Assert.Equal("bcd", SampleIndexRangeLib.SliceOps.StrTail("abcd"));
        Assert.Equal("abc", SampleIndexRangeLib.SliceOps.StrHead("abcd"));
        Assert.Equal("cd", SampleIndexRangeLib.SliceOps.StrFromEnd("abcd"));
        Assert.Equal('d', SampleIndexRangeLib.SliceOps.LastChar("abcd"));
    }

    [Fact]
    public void PrimitiveAndReferenceArrays_RouteToSliceOverloads()
    {
        Assert.Equal(new long[] { 2, 3 }, SampleIndexRangeLib.SliceOps.LongMiddle(new long[] { 1, 2, 3, 4 }));
        Assert.Equal(new byte[] { 2, 3 }, SampleIndexRangeLib.SliceOps.ByteMiddle(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(new[] { 2.0, 3.0 }, SampleIndexRangeLib.SliceOps.DoubleMiddle(new[] { 1.0, 2.0, 3.0, 4.0 }));
        Assert.Equal(new[] { 'b', 'c' }, SampleIndexRangeLib.SliceOps.CharMiddle(new[] { 'a', 'b', 'c', 'd' }));
        Assert.Equal(new[] { true, false }, SampleIndexRangeLib.SliceOps.BoolMiddle(new[] { false, true, false, true }));
        Assert.Equal(new[] { "x" }, SampleIndexRangeLib.SliceOps.RefMiddle(new[] { "w", "x", "y" }));
    }
}
