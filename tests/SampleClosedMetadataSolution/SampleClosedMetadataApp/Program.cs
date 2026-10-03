using System.Text.Json;
using SampleClosedMetadataLib;

var dto = new NestedListDto
{
    Matrix = { new() { 1, 2 }, new() { 3 } },
    Index = { { "a", new List<int> { 1, 2 } } },
};
var json = JsonSerializer.Serialize(dto);
var roundTripped = JsonSerializer.Deserialize<NestedListDto>(json);
if (roundTripped is null || roundTripped.Matrix.Count != 2 || roundTripped.Matrix[0][1] != 2) return 1;
if (roundTripped.Index["a"].Count != 2) return 2;

var shared = JsonSerializer.Deserialize<SharedClosureDtoA>(
    JsonSerializer.Serialize(new SharedClosureDtoB { Values = { new() { 7 } } }));
if (shared is null || shared.Values[0][0] != 7) return 3;

var bag = JsonSerializer.Deserialize<Bag<string>>(
    JsonSerializer.Serialize(new Bag<string> { Items = { "x", "y" } }));
if (bag is null || bag.Items.Count != 2 || bag.Items[1] != "y") return 4;

return 0;
