namespace SampleLinqLib;

/// <summary>
/// Queryable-side skeleton: chains over <see cref="Things"/> bind
/// System.Linq.Queryable and take Expression&lt;T&gt; arguments, which is the
/// PreserveQueryable route of the C26 three-way gate (design §1.1). M1 only
/// observes/reports these sites; acceptance gating lands in M3.
/// </summary>
public interface IThingRepository
{
    IQueryable<Thing> Things { get; }

    IQueryable<Thing> QueryActive()
        => Things.Where(t => t.Active).OrderBy(t => t.Id);
}

/// <summary>In-memory repository used by the fixture tests (LINQ-to-Objects provider).</summary>
public sealed class InMemoryThingRepository : IThingRepository
{
    private readonly List<Thing> _things;

    public InMemoryThingRepository(IEnumerable<Thing> things)
        => _things = things.ToList();

    public IQueryable<Thing> Things => _things.AsQueryable();
}
