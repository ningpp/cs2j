using System.Text.Json.Serialization;

namespace CSharpToJava.TypeMapping.JavaModel;

/// <summary>
/// Describes a single parameter of a Java constructor or method.
/// </summary>
public sealed record JavaParameterInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("genericType")]
    public string GenericType { get; init; } = string.Empty;

    [JsonPropertyName("implicit")]
    public bool Implicit { get; init; }

    [JsonPropertyName("synthetic")]
    public bool Synthetic { get; init; }
}

/// <summary>
/// Describes a public constructor of a Java type.
/// </summary>
public sealed record JavaConstructorInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("modifiers")]
    public string Modifiers { get; init; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public IReadOnlyList<JavaParameterInfo> Parameters { get; init; } = [];

    [JsonPropertyName("exceptions")]
    public IReadOnlyList<string> Exceptions { get; init; } = [];

    [JsonPropertyName("varArgs")]
    public bool VarArgs { get; init; }
}

/// <summary>
/// Describes a public method of a Java type.
/// </summary>
public sealed record JavaMethodInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("modifiers")]
    public string Modifiers { get; init; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = string.Empty;

    [JsonPropertyName("returnType")]
    public string ReturnType { get; init; } = string.Empty;

    [JsonPropertyName("genericReturnType")]
    public string GenericReturnType { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public IReadOnlyList<JavaParameterInfo> Parameters { get; init; } = [];

    [JsonPropertyName("exceptions")]
    public IReadOnlyList<string> Exceptions { get; init; } = [];

    [JsonPropertyName("staticMethod")]
    public bool StaticMethod { get; init; }

    [JsonPropertyName("abstractMethod")]
    public bool AbstractMethod { get; init; }

    [JsonPropertyName("defaultMethod")]
    public bool DefaultMethod { get; init; }

    [JsonPropertyName("varArgs")]
    public bool VarArgs { get; init; }
}

/// <summary>
/// Describes a Java type (class, interface, enum, or record) loaded from metadata JSON.
/// </summary>
public sealed record JavaTypeInfo
{
    [JsonPropertyName("moduleName")]
    public string ModuleName { get; init; } = string.Empty;

    [JsonPropertyName("packageName")]
    public string PackageName { get; init; } = string.Empty;

    [JsonPropertyName("binaryName")]
    public string BinaryName { get; init; } = string.Empty;

    [JsonPropertyName("canonicalName")]
    public string CanonicalName { get; init; } = string.Empty;

    [JsonPropertyName("simpleName")]
    public string SimpleName { get; init; } = string.Empty;

    /// <summary>
    /// Type kind: "class", "interface", "enum", or "record".
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("modifiers")]
    public string Modifiers { get; init; } = string.Empty;

    [JsonPropertyName("superClass")]
    public string? SuperClass { get; init; }

    [JsonPropertyName("declaringType")]
    public string? DeclaringType { get; init; }

    [JsonPropertyName("interfaces")]
    public IReadOnlyList<string> Interfaces { get; init; } = [];

    [JsonPropertyName("declaredPublicConstructors")]
    public IReadOnlyList<JavaConstructorInfo> DeclaredPublicConstructors { get; init; } = [];

    [JsonPropertyName("declaredPublicMethods")]
    public IReadOnlyList<JavaMethodInfo> DeclaredPublicMethods { get; init; } = [];
}

/// <summary>
/// Represents a Java module (e.g. java.base, java.sql) and the types it contains.
/// </summary>
public sealed class JavaModule
{
    public string ModuleName { get; }
    private readonly Dictionary<string, JavaTypeInfo> _typesByCanonicalName;

    public JavaModule(string moduleName, IEnumerable<JavaTypeInfo> types)
    {
        ModuleName = moduleName;
        _typesByCanonicalName = types.ToDictionary(t => t.CanonicalName, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, JavaTypeInfo> TypesByCanonicalName => _typesByCanonicalName;

    public JavaTypeInfo? FindType(string canonicalName)
        => _typesByCanonicalName.TryGetValue(canonicalName, out var t) ? t : null;
}

/// <summary>
/// Root container for all loaded Java standard-library metadata.
/// </summary>
public sealed class JavaLibrary
{
    private readonly Dictionary<string, JavaModule> _modulesByName;

    public JavaLibrary(IEnumerable<JavaModule> modules)
    {
        _modulesByName = modules.ToDictionary(m => m.ModuleName, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, JavaModule> ModulesByName => _modulesByName;
}
