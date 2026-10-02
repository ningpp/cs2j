namespace SamplePartialLib;

/// <summary>C#13 partial event: field-style defining declaration.</summary>
public partial class PartialEvent
{
    public partial event System.Action? Changed;
}

/// <summary>C#13 partial event: add/remove implementing declaration.</summary>
public partial class PartialEvent
{
    private System.Action? _changed;

    public partial event System.Action? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public void Raise()
    {
        _changed?.Invoke();
    }
}
