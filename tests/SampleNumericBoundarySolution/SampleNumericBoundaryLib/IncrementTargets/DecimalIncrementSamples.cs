namespace SampleNumericBoundaryLib.IncrementTargets;

/// <summary>
/// C17 M2 IT-7 runtime instrumentation: decimal ++/-- is expanded to
/// 'old ± 1m' and the Java method surface is owned by C34. The counters here
/// prove the expanded read-modify-write still evaluates a property exactly
/// once per get/set and an array index exactly once, while the precision
/// probe pins the smallest task-book literal (1e-28m) end to end.
/// </summary>
public sealed class DecimalIncrementSamples
{
    private decimal _decimalProp;
    private readonly decimal[] _decimals = new decimal[4];

    public int DecimalPropGets;
    public int DecimalPropSets;

    public decimal DecimalProp
    {
        get { DecimalPropGets++; return _decimalProp; }
        set { DecimalPropSets++; _decimalProp = value; }
    }

    public int DecimalIndexEvaluations;

    public int DecimalIndex()
    {
        DecimalIndexEvaluations++;
        return 1;
    }

    // IT-7: standalone decimal property postfix — one get, one set, value +1m.
    public decimal DecimalPropertyIncrement()
    {
        DecimalProp++;
        return _decimalProp;
    }

    public decimal DecimalPropertyDecrement()
    {
        // Seed the backing field directly so the decrement below is the only
        // get/set pair this method contributes.
        _decimalProp = 3m;
        DecimalProp--;
        return _decimalProp;
    }

    // IT-7: decimal array element — the index expression is hoisted and
    // evaluated once for the read and the write.
    public void DecimalArrayIncrementOnce()
    {
        _decimals[DecimalIndex()]++;
    }

    public decimal DecimalArrayStoredValue() => _decimals[1];

    // Task-book precision probe: 27 zeros then 1, i.e. 1e-28m (28 fractional
    // digits), the smallest decimal literal the milestone exercises. A
    // 2026-10-01 probe verified 1e-29m collapses to zero; this one must not.
    public static bool PrecisionTinySurvives()
    {
        decimal tiny = 0.0000000000000000000000000001m;
        decimal one = 1m;
        decimal sum = one + tiny;
        return sum - one == tiny && sum != one;
    }

    public static decimal PrecisionIncrement()
    {
        decimal one = 1m;
        one++;
        return one;
    }
}
