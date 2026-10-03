using System.Text.Json;
using SampleClosedMetadataLib;
using Xunit;

namespace SampleClosedMetadataTests;

public class RuntimeBehaviorTests
{
    [Fact]
    public void NestedListDto_RoundTrips()
    {
        var dto = new NestedListDto
        {
            Matrix = { new() { 1, 2 }, new() { 3 } },
            Index = { { "a", new List<int> { 1, 2 } } },
        };

        var json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<NestedListDto>(json);

        Assert.NotNull(back);
        Assert.Equal(2, back!.Matrix.Count);
        Assert.Equal((short)2, back.Matrix[0][1]);
        Assert.Equal(new[] { 1, 2 }, back.Index["a"]);
    }

    [Fact]
    public void SharedClosure_RoundTripsAcrossDtoTypes()
    {
        var b = new SharedClosureDtoB { Values = { new() { 7, 8 } } };

        var json = JsonSerializer.Serialize(b);
        var a = JsonSerializer.Deserialize<SharedClosureDtoA>(json);

        Assert.NotNull(a);
        Assert.Equal((short)7, a!.Values[0][0]);
        Assert.Equal((short)8, a.Values[0][1]);
    }

    [Fact]
    public void Bag_OfConcrete_RoundTrips()
    {
        var bag = new Bag<string> { Items = { "x", "y" } };

        var json = JsonSerializer.Serialize(bag);
        var back = JsonSerializer.Deserialize<Bag<string>>(json);

        Assert.NotNull(back);
        Assert.Equal(new[] { "x", "y" }, back!.Items);
    }
}
