namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Host identity constants for the C26 three-way LINQ route gate
/// (design §4.1/§4.2, D1/D2). Routing keys off the bound method symbol's
/// containing static class — never off syntax names.
/// </summary>
/// <remarks>
/// v1 deliberately keeps a second copy of the two protected LINQ host strings
/// (design Q2: shared extraction happens with the C19 follow-up; cross-references
/// to the existing C19 copies are below). Do not widen these sets from the
/// channel side.
/// </remarks>
public static class LinqHosts
{
    /// <summary>Containing type of all client-side (IEnumerable) LINQ operators.</summary>
    public const string EnumerableHost = "System.Linq.Enumerable";

    /// <summary>Containing type of all server-shaped (IQueryable) LINQ operators.</summary>
    public const string QueryableHost = "System.Linq.Queryable";

    /// <summary>Namespace of the third route, <c>System.Linq.Expressions.Expression&lt;T&gt;</c>.</summary>
    public const string ExpressionNamespace = "System.Linq.Expressions";

    // Reduced ThenBy/ThenByDescending carry their receiver interfaces in reduced
    // display strings (M0 probe measured "System.Linq.IOrderedQueryable<TSource>"
    // and "System.Linq.IOrderedEnumerable<TSource>"). The classifier's primary
    // host identity is ReducedFrom.ContainingType (the static class), but any
    // receiver-derived host check must normalize these ordered interfaces too.
    private const string EnumerableInterfacePrefix = "System.Collections.Generic.IEnumerable<";
    private const string OrderedEnumerableInterfacePrefix = "System.Linq.IOrderedEnumerable<";
    private const string QueryableInterfacePrefix = "System.Linq.IQueryable<";
    private const string OrderedQueryableInterfacePrefix = "System.Linq.IOrderedQueryable<";

    /// <summary>
    /// Maps a containing-type (or, defensively, a receiver-interface) display
    /// string to one of the two static LINQ hosts. Returns null for non-LINQ
    /// hosts. Ordered interfaces are normalized to their base operator host
    /// (IOrderedEnumerable → Enumerable, IOrderedQueryable → Queryable).
    /// </summary>
    public static string? NormalizeToStaticHost(string? hostDisplay)
    {
        if (string.IsNullOrEmpty(hostDisplay))
        {
            return null;
        }

        if (hostDisplay == EnumerableHost)
        {
            return EnumerableHost;
        }

        if (hostDisplay == QueryableHost)
        {
            return QueryableHost;
        }

        if (hostDisplay.StartsWith(EnumerableInterfacePrefix, StringComparison.Ordinal)
            || hostDisplay.StartsWith(OrderedEnumerableInterfacePrefix, StringComparison.Ordinal))
        {
            return EnumerableHost;
        }

        if (hostDisplay.StartsWith(QueryableInterfacePrefix, StringComparison.Ordinal)
            || hostDisplay.StartsWith(OrderedQueryableInterfacePrefix, StringComparison.Ordinal))
        {
            return QueryableHost;
        }

        return null;
    }

    //
    // C19 cross-references (Q2 dual-copy contract — update all sites together
    // when the shared Core/Analysis/LinqHostFacts.cs extraction happens):
    //   * ExtensionMethodResolve.ExtensionHostOverrides.ProtectedLinqHosts
    //   * ExtensionMethodResolve.ExtensionMethodResolveOptions.LinqHostSet
    //
}
