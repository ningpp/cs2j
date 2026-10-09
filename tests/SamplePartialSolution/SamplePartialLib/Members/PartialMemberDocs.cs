namespace SamplePartialLib;

[System.AttributeUsage(System.AttributeTargets.Method)]
internal sealed class SourceSideAttribute : System.Attribute
{
}

[System.AttributeUsage(System.AttributeTargets.Method)]
internal sealed class ImplementationSideAttribute : System.Attribute
{
}

/// <summary>M3 member-level documentation/attribute split: definition part.</summary>
public partial class PartialMemberDocs
{
    /// <summary>Definition-side documentation.</summary>
    [SourceSide]
    public partial int Compute(int input);
}

/// <summary>M3 member-level documentation/attribute split: implementation part.</summary>
public partial class PartialMemberDocs
{
    /// <summary>Implementation-side documentation.</summary>
    [ImplementationSide]
    public partial int Compute(int input) => input + 1;
}
