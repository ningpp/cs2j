namespace SamplePartialLib;

/// <summary>Extended partial method: defining declaration.</summary>
public partial class ExtendedPartial
{
    private partial string GetName();

    public string Name => GetName();
}

/// <summary>Extended partial method: implementing declaration.</summary>
public partial class ExtendedPartial
{
    private partial string GetName() => "extended";
}
