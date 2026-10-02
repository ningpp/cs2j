namespace SamplePartialLib;

/// <summary>C#13 partial constructor: defining declaration.</summary>
public partial class PartialConstructor
{
    public partial PartialConstructor(int id);

    private readonly int _id;
}

/// <summary>C#13 partial constructor: implementing declaration.</summary>
public partial class PartialConstructor
{
    public PartialConstructor()
        : this(0)
    {
    }

    public partial PartialConstructor(int id)
    {
        _id = id;
    }

    public int Id => _id;
}
