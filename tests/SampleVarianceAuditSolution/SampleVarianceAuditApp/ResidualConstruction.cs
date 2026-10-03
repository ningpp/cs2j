namespace SampleVarianceAuditApp;

public interface INamed
{
    string Name { get; set; }
}

public sealed class NamedThing : INamed
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// E10 rewrites only a parameterless, initializer-free <c>new T()</c>; the initializer
/// form below is left as-is, so the raw-tree audit reports it (VA023, design D6).
/// </summary>
public static class ResidualConstruction
{
    public static T Build<T>() where T : INamed, new() => new T { Name = "built" };
}