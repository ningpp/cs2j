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

    // Inner join against an id sequence: the Join chain (lambdas on both
    // sides) lowers procedurally like every other Enumerable chain.
    public static IEnumerable<string> JoinNamesToIds(
        IEnumerable<Thing> things, IEnumerable<int> ids)
        => things.Join(ids, t => t.Id, id => id, (t, id) => t.Name);

    public static int TotalId(IEnumerable<Thing> things)
        => things.Select(t => t.Id).Sum();

    public static int[] DoubledEvens(IEnumerable<int> values)
        => values.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();
}
