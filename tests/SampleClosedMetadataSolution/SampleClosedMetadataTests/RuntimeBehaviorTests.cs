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

    [Fact]
    public void ConsumerSurfaceShapes_EmbeddedIfAndSwitch_RoundTrips()
    {
        // M4c surface: serializer entries in an embedded if-body (CM-LINK wraps it
        // in its own block) and inside a switch-expression arm (nearest statement is
        // the local declaration) must each get an adjacent guard and stay runtime-correct.
        NestedListDto? dto = null;
        if (dto is null)
        {
            dto = JsonSerializer.Deserialize<NestedListDto>(
                JsonSerializer.Serialize(new NestedListDto { Matrix = { new() { 9 } } }));
        }

        Assert.NotNull(dto);
        Assert.Equal((short)9, dto!.Matrix[0][0]);

        var kind = 1;
        var json = kind switch
        {
            1 => JsonSerializer.Serialize(new SharedClosureDtoB { Values = { new() { 5 } } }),
            _ => "{}",
        };
        var back = JsonSerializer.Deserialize<SharedClosureDtoA>(json);

        Assert.NotNull(back);
        Assert.Equal((short)5, back!.Values[0][0]);
    }
}
