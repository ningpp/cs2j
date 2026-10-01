namespace SampleNumericBoundaryLib.DecimalOps;

/// <summary>
/// C34 DC-10/DC-11 fixture: simple compound assignments rewrite in place;
/// complex targets (properties/indexers off an instance receiver) hoist the
/// receiver and index arguments so each is evaluated exactly once.
/// </summary>
public static class DecimalCompoundSamples
{
    public static decimal SimpleCompounds(decimal x, decimal y)
    {
        x += y;
        x -= y;
        x *= y;
        x /= y;
        x %= y;
        return x;
    }

    public static decimal PropertyCompound(DecimalProbe probe, decimal x)
    {
        probe.P += x;
        return probe.P;
    }

    public static decimal FieldCompound(DecimalProbe probe, decimal x)
    {
        probe.F -= x;
        return probe.F;
    }

    public static void IndexerCompound(DecimalProbe probe, decimal x)
    {
        probe[probe.OneIndex()] *= x;
    }
}

/// <summary>
/// Runtime instrumentation: the rewrite must keep exactly-once evaluation of
/// the receiver, the property getter/setter and indexer arguments.
/// </summary>
public sealed class DecimalProbe
{
    public decimal F;

    private decimal _p;
    private readonly decimal[] _store = new decimal[2];

    public int PropertyGets;
    public int PropertySets;
    public int IndexerGets;
    public int IndexerSets;
    public int IndexArgEvaluations;
    public int ReceiverEvaluations;

    public decimal P
    {
        get
        {
            PropertyGets++;
            return _p;
        }
        set
        {
            PropertySets++;
            _p = value;
        }
    }

    public decimal this[int index]
    {
        get
        {
            IndexerGets++;
            return _store[index];
        }
        set
        {
            IndexerSets++;
            _store[index] = value;
        }
    }

    public int OneIndex()
    {
        IndexArgEvaluations++;
        return 1;
    }

    public DecimalProbe Self()
    {
        ReceiverEvaluations++;
        return this;
    }

    public void CompoundOnPropertyViaMethodReceiver(decimal x)
    {
        Self().P += x;
    }
}
