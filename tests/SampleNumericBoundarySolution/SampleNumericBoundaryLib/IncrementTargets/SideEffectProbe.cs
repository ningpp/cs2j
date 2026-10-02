namespace SampleNumericBoundaryLib.IncrementTargets;

/// <summary>
/// C17 M1 IT-1/2/3/10/11 runtime instrumentation: every property/indexer
/// access, receiver factory call and index-argument evaluation is counted so
/// the sample app can prove each subexpression of an expanded ++/-- is
/// evaluated exactly once in source order.
/// </summary>
public sealed class SideEffectProbe
{
    private int _intProp;
    private byte _byteProp;
    private uint _uIntProp;
    private IncrementColor _colorProp;
    private readonly int[] _indexStore = new int[8];

    public int IntPropGets;
    public int IntPropSets;

    public int IntProp
    {
        get { IntPropGets++; return _intProp; }
        set { IntPropSets++; _intProp = value; }
    }

    public int BytePropGets;
    public int BytePropSets;

    public byte ByteProp
    {
        get { BytePropGets++; return _byteProp; }
        set { BytePropSets++; _byteProp = value; }
    }

    public uint UIntProp
    {
        get => _uIntProp;
        set => _uIntProp = value;
    }

    public int ColorPropGets;
    public int ColorPropSets;

    public IncrementColor ColorProp
    {
        get { ColorPropGets++; return _colorProp; }
        set { ColorPropSets++; _colorProp = value; }
    }

    public int IndexerGets;
    public int IndexerSets;
    public int IndexArgEvaluations;

    public int this[int index]
    {
        get { IndexerGets++; IndexArgEvaluations++; return _indexStore[index]; }
        set { IndexerSets++; IndexArgEvaluations++; _indexStore[index] = value; }
    }

    public int FactoryCalls;

    public SideEffectProbe GetObj()
    {
        FactoryCalls++;
        return this;
    }

    public int NextCalls;

    public int Next()
    {
        NextCalls++;
        return 1;
    }

    public int IndexStoredValue(int index) => _indexStore[index];

    // IT-1: standalone property postfix — one getter and one setter.
    public int PropertyStandaloneResult()
    {
        IntProp++;
        return _intProp;
    }

    // IT-11: postfix value is consumed — the old value is lifted once.
    public (int Old, int Now) PostfixConsumedProperty()
    {
        int old = IntProp++;
        return (old, _intProp);
    }

    // IT-10: prefix value is consumed by 'return' — the assignment is inlined
    // in parentheses.
    public int PrefixReturnedProperty()
    {
        return ++IntProp;
    }

    // IT-2: effectful receiver is evaluated once although the property is
    // read and written.
    public int EffectfulReceiverProperty()
    {
        GetObj().IntProp++;
        return _intProp;
    }

    public int PrefixEffectfulReceiver()
    {
        return ++GetObj().IntProp;
    }

    // IT-3: indexer arguments are evaluated once for both the get and set.
    public void IndexerIncrementOnce()
    {
        this[Next()]++;
    }

    // IT-5: uint property routes through AddUInt32; byte property through a
    // narrow cast.
    public uint UIntPropertyIncrement()
    {
        UIntProp++;
        return _uIntProp;
    }

    public byte BytePropertyIncrement()
    {
        ByteProp++;
        return _byteProp;
    }

    // Checked complex target owned by C17; the C15 sweep then helperizes the
    // normalized Int32 arithmetic while M1's outer enum cast survives.
    public IncrementColor CheckedColorPropertyIncrement()
    {
        checked
        {
            ColorProp++;
        }

        return _colorProp;
    }
}
