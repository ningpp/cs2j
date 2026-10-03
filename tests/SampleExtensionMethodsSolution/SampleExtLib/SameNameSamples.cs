namespace SampleExtLib.Shapes;

public sealed class Counter
{
    /// <summary>Applicable instance member of the same name as the extension.</summary>
    public int Report() => 1;
}

public static class CounterExtensions
{
    /// <summary>
    /// Same name as <see cref="Counter.Report()"/> but a different arity: the
    /// instance method does not apply to <c>counter.Report(bonus)</c>, so
    /// overload resolution falls back to this extension.
    /// </summary>
    public static int Report(this Counter counter, int bonus) => 2 + bonus;
}
