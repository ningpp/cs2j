using System.Collections;
using System.Linq.Expressions;

namespace SampleLinqLib;

/// <summary>
/// M3 fixture surface for the C26 three-way gate: a custom
/// <see cref="IQueryProvider"/> that records the expression shapes pushed by
/// the Queryable operators (the runtime half of the gate — every lambda must
/// arrive as a quoted <see cref="Expression{TDelegate}"/>), plus mixed-host
/// chains, two-source query syntax and a hand-built expression tree. All of
/// these shapes are PRESERVED by the linq-channel and therefore behave
/// identically before and after the pass (the SampleLinqTests truth tests).
/// </summary>
public sealed class RecordingQueryProvider : IQueryProvider
{
    public List<Expression> RecordedExpressions { get; } = new();

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces()
            .First(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .GetGenericArguments()[0];
        var queryableType = typeof(RecordingQueryable<>).MakeGenericType(elementType);
        return (IQueryable)Activator.CreateInstance(queryableType, this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        RecordedExpressions.Add(expression);
        return new RecordingQueryable<TElement>(this, expression);
    }

    public object? Execute(Expression expression) => null;

    public TResult Execute<TResult>(Expression expression) => default!;
}

/// <summary>Queryable whose every operator call flows through <see cref="RecordingQueryProvider"/>.</summary>
public sealed class RecordingQueryable<T> : IOrderedQueryable<T>
{
    public RecordingQueryable(RecordingQueryProvider provider)
    {
        Provider = provider;
        Expression = Expression.Constant(this);
    }

    public RecordingQueryable(IQueryProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    public Type ElementType => typeof(T);

    public Expression Expression { get; }

    public IQueryProvider Provider { get; }

    public IEnumerator<T> GetEnumerator() => throw new NotSupportedException();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Queryable-side scenarios exercised by the M3 fixture truth tests.</summary>
public static class QueryableScenarios
{
    /// <summary>
    /// Builds Where/Select/OrderBy/Take over the recording provider and
    /// returns the recorded call shapes as "DeclaringType.Method:argCount".
    /// The channel must preserve the chain, so the sequence is identical in
    /// the baseline and the intermediate tree.
    /// </summary>
    public static List<string> RecordedOperatorShapes()
    {
        var provider = new RecordingQueryProvider();
        IQueryable<int> source = new RecordingQueryable<int>(provider);

        var query = source
            .Where(x => x > 1)
            .Select(x => x * 2)
            .OrderBy(x => x)
            .Take(10);
        _ = query;

        var shapes = new List<string>();
        foreach (var expression in provider.RecordedExpressions.OfType<MethodCallExpression>())
        {
            shapes.Add(expression.Method.DeclaringType!.Name
                + "." + expression.Method.Name + ":" + expression.Arguments.Count);
        }

        return shapes;
    }

    /// <summary>
    /// D7 mixed-host chain: Queryable filter crossing AsEnumerable into an
    /// Enumerable projection. Preserved wholesale by the gate; executes
    /// client-side over the LINQ-to-Objects provider.
    /// </summary>
    public static IEnumerable<string> MixedHostActiveNames(IEnumerable<Thing> things)
        => things.AsQueryable()
            .Where(t => t.Active)
            .AsEnumerable()
            .Select(t => t.Name);

    /// <summary>
    /// Two-source query syntax over Queryable receivers: stage 1 desugars to
    /// Queryable.SelectMany (quoted lambdas), stage 2 freezes the whole chain.
    /// </summary>
    public static IEnumerable<string> TwoSourceQuery(
        IQueryable<Thing> things, IQueryable<int> ids)
        => from a in things
           from b in ids
           where a.Id == b
           select a.Name;

    /// <summary>
    /// Hand-built expression tree (no lambda syntax at all). The third-route
    /// protection and the channel must leave the builder calls untouched.
    /// Returns a compiled predicate matching the supplied id.
    /// </summary>
    public static Func<Thing, bool> BuildIdPredicate(int id)
    {
        var parameter = Expression.Parameter(typeof(Thing), "t");
        var idProperty = Expression.Property(parameter, nameof(Thing.Id));
        var body = Expression.Equal(idProperty, Expression.Constant(id));
        var lambda = Expression.Lambda<Func<Thing, bool>>(body, parameter);
        return lambda.Compile();
    }
}
