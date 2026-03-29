namespace CSharpToJava.Core.Pipeline;

internal static class ProjectPassParallelism
{
    public static IReadOnlyList<TResult> RunDeterministic<TItem, TResult>(
        IReadOnlyList<TItem> items,
        bool enableParallel,
        Func<TItem, TResult> transform)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(transform);

        var results = new TResult[items.Count];

        if (enableParallel && items.Count > 1)
        {
            System.Threading.Tasks.Parallel.For(0, items.Count, index =>
            {
                results[index] = transform(items[index]);
            });
        }
        else
        {
            for (var index = 0; index < items.Count; index++)
            {
                results[index] = transform(items[index]);
            }
        }

        return results;
    }
}