namespace SamplePartialLib;

/// <summary>C#13 partial indexer: defining declaration.</summary>
public partial class PartialIndexer
{
    public partial int this[int index] { get; }
}

/// <summary>C#13 partial indexer: implementing declaration.</summary>
public partial class PartialIndexer
{
    private readonly int[] _values = [10, 20, 30];

    public partial int this[int index]
    {
        get => _values[index];
    }

    public int Length => _values.Length;
}
