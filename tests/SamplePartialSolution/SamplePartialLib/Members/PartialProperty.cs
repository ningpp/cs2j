namespace SamplePartialLib;

/// <summary>C#13 partial property: defining declaration.</summary>
public partial class PartialProperty
{
    public partial int Value { get; }
}

/// <summary>C#13 partial property: implementing declaration.</summary>
public partial class PartialProperty
{
    private int _value;

    public partial int Value
    {
        get => _value;
    }

    public void Set(int value)
    {
        _value = value;
    }
}
