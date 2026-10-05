// C26 M0 probe ④: feasibility of the M3 RecordingQueryProvider fixture
// (design §7.5). A hand-written minimal IQueryable<T> + IQueryProvider records
// every expression tree handed to CreateQuery, proving that WITHOUT any channel
// in the loop the provider-visible expression tree can be inspected node by
// node and that its node sequence is stable. M3 will assert channel output
// produces the IDENTICAL sequence.

using System.Collections;
using System.Linq.Expressions;

namespace CSharpToJava.Tests.LinqChannel.Probe;

public sealed class RecordingQueryProviderProbeTests
{
    [Fact]
    public void Provider_Receives_StableQuotedExpressionNodeSequence()
    {
        var provider = new RecordingQueryProvider();
        IQueryable<int> source = new RecordingQueryable<int>(provider);

        // Build WITHOUT enumerating: Queryable operators create new queries via
        // IQueryProvider.CreateQuery, nothing is executed.
        var query = source
            .Where(x => x > 1)
            .Select(x => x * 2)
            .OrderBy(x => x)
            .Take(10);

        Assert.Equal(4, provider.RecordedExpressions.Count);

        var shapes = provider.RecordedExpressions
            .Select(DescribeTopNode)
            .ToList();

        Assert.Equal(
            new[]
            {
                "Call:Queryable.Where:2",
                "Call:Queryable.Select:2",
                "Call:Queryable.OrderBy:2",
                "Call:Queryable.Take:2",
            },
            shapes);

        // Every lambda argument arrives as a QUOTED expression (UnaryExpression
        // wrapping the LambdaExpression) — this is the shape the channel must
        // never unquote or rewrite (risk 8 / D2).
        foreach (var expression in provider.RecordedExpressions)
        {
            var call = Assert.IsAssignableFrom<MethodCallExpression>(expression);
            foreach (var argument in call.Arguments)
            {
                if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
                {
                    Assert.IsAssignableFrom<LambdaExpression>(quote.Operand);
                }
            }
        }

        // Chain linkage: each recorded expression's first argument is the
        // preceding recorded expression (the outermost is last) — stable
        // node-level identity for M3 before/after comparison.
        for (var i = 1; i < provider.RecordedExpressions.Count; i++)
        {
            var call = (MethodCallExpression)provider.RecordedExpressions[i];
            Assert.Same(provider.RecordedExpressions[i - 1], call.Arguments[0]);
        }

        // The root expression of the outermost query is the constant holding
        // the source queryable — standard LINQ provider contract.
        var outer = (MethodCallExpression)provider.RecordedExpressions[^1];
        var rootConstant = Assert.IsType<ConstantExpression>(WalkToSource(outer));
        Assert.Same(source, rootConstant.Value);

        // Stability pin: building the same query again yields the same node
        // description sequence (different tree instances, identical shape).
        var provider2 = new RecordingQueryProvider();
        IQueryable<int> source2 = new RecordingQueryable<int>(provider2);
        var query2 = source2
            .Where(x => x > 1)
            .Select(x => x * 2)
            .OrderBy(x => x)
            .Take(10);
        var shapes2 = provider2.RecordedExpressions.Select(DescribeTopNode).ToList();
        Assert.Equal(shapes, shapes2);
        Assert.NotNull(query);
        Assert.NotNull(query2);
    }

    private static Expression WalkToSource(MethodCallExpression call)
    {
        var current = call.Arguments[0];
        while (current is MethodCallExpression inner)
            current = inner.Arguments[0];
        return current;
    }

    private static string DescribeTopNode(Expression expression)
    {
        if (expression is MethodCallExpression call)
            return $"{expression.NodeType}:{call.Method.DeclaringType?.Name}.{call.Method.Name}:{call.Arguments.Count}";
        return $"{expression.NodeType}:{expression.Type.Name}";
    }

    private sealed class RecordingQueryProvider : IQueryProvider
    {
        public List<Expression> RecordedExpressions { get; } = new();

        public IQueryable CreateQuery(Expression expression)
        {
            var elementType = GetEnumerableElementType(expression.Type);
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

        private static Type GetEnumerableElementType(Type queryableType)
            => queryableType.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .GetGenericArguments()[0];
    }

    private sealed class RecordingQueryable<T> : IOrderedQueryable<T>
    {
        public RecordingQueryable(IQueryProvider provider)
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

        public IEnumerator<T> GetEnumerator() =>
            throw new NotSupportedException("M0 recording fixture never enumerates.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
