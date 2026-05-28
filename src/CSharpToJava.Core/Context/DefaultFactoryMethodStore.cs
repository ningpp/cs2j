namespace CSharpToJava.Core.Context;

/// <summary>
/// Shared store for tracking which generic classes need a protected factory method
/// for creating default values of unconstrained type parameters (Unknown binding).
/// Shared across all project conversions within a session so that factory methods
/// registered during base-class project conversion are visible when converting
/// subclass projects.
/// </summary>
public sealed class DefaultFactoryMethodStore
{
    private readonly Dictionary<string, HashSet<string>> _methods = new();

    /// <summary>
    /// Register that a class needs a factory method for the given type parameter.
    /// Key: full metadata name of the generic class (original definition).
    /// Value: type parameter name that needs a factory method.
    /// </summary>
    public void Register(string classFullMetadataName, string typeParamName)
    {
        if (!_methods.TryGetValue(classFullMetadataName, out var set))
        {
            set = new HashSet<string>();
            _methods[classFullMetadataName] = set;
        }
        set.Add(typeParamName);
    }

    /// <summary>
    /// Get the set of type parameter names that have factory methods for the given class.
    /// Returns null if the class has no registered factory methods.
    /// </summary>
    public IReadOnlySet<string>? GetForClass(string classFullMetadataName)
    {
        return _methods.TryGetValue(classFullMetadataName, out var set) ? set : null;
    }
}
