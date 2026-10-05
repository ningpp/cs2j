using SampleLinqLib;

var things = new[]
{
    new Thing(1, "alpha", true),
    new Thing(2, "beta", false),
    new Thing(3, "gamma", true),
};

foreach (var name in LinqTools.ActiveNames(things))
{
    Console.WriteLine(name);
}

IThingRepository repository = new InMemoryThingRepository(things);
var activeCount = repository.QueryActive().Count();
Console.WriteLine($"active: {activeCount}");
