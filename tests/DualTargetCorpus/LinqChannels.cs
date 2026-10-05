using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace DualTarget
{
    // C26 M3 linq-channel routing samples. The Enumerable chain is the
    // shape the preprocessing channel expands to procedural form (route E);
    // the Queryable/Expression shapes freeze verbatim (routes Q/D7) so that
    // expression-tree semantics survive the dual-target pipeline.
    public static class LinqChannels
    {
        // Route E (EnumerableChainsExpanded): LINQ-to-Objects chain on an
        // in-memory source — the channel rewrites this to the procedural
        // helper form.
        public static List<int> PositiveDoubles(IEnumerable<int> values)
        {
            return values
                .Where(v => v > 0)
                .Select(v => v * 2)
                .ToList();
        }

        // Route Q (QueryableChainsPreserved): the predicate reaches a real
        // IQueryable provider as an Expression; the call must stay frozen.
        public static IList<Thing> ActiveThings(IQueryable<Thing> things)
        {
            return things
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .Take(50)
                .ToList();
        }

        // Expression-argument site (ExpressionArgumentSites): the lambda is
        // quoted because the consuming parameter is Expression<...>; no node
        // inside the quote may be rewritten even when it looks enumerable.
        public static IQueryable<Thing> ApplyNameFilter(
            IQueryable<Thing> things,
            Expression<System.Func<Thing, bool>> predicate)
        {
            return things.Where(predicate);
        }

        // Route D7 (MixedChainsPreserved): a Queryable segment followed by an
        // Enumerable segment crosses the expression/object boundary; the
        // whole chain freezes so the provider-side part is not desugared.
        public static List<string> ActiveNamesPage(IQueryable<Thing> things)
        {
            return things
                .Where(t => t.IsActive)
                .AsEnumerable()
                .Select(t => t.Name)
                .Take(20)
                .ToList();
        }
    }

    public class Thing
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }
}
