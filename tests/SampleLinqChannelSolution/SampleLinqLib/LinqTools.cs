namespace SampleLinqLib;

/// <summary>
/// Small domain type shared by the Enumerable tools and the IQueryable
/// repository skeleton.
/// </summary>
public sealed record Thing(int Id, string Name, bool Active);

/// <summary>
/// Enumerable-side LINQ utility methods. These chains bind
/// System.Linq.Enumerable and are the Expandable route of the C26 channel
/// (design §1.1): the embedded E16 rewriter lowers them procedurally and the
/// M1 channel must produce the same output.
/// </summary>
public static class LinqTools
{
    public static IEnumerable<Thing> ActiveThings(IEnumerable<Thing> things)
        => things
            .Where(t => t.Active)
            .OrderBy(t => t.Id)
            .Select(t => t);

    public static IEnumerable<string> ActiveNames(IEnumerable<Thing> things)
        => things
            .Where(t => t.Active)
            .Select(t => t.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

    public static int ActiveCount(IEnumerable<Thing> things)
        => things.Count(t => t.Active);

    public static List<IGrouping<bool, Thing>> ByActivity(IEnumerable<Thing> things)
        => things.GroupBy(t => t.Active).ToList();

    public static int[] DoubledEvens(IEnumerable<int> values)
        => values.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();
}
