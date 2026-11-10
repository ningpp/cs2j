using System.Reflection;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the O(1) canonical-name lookup indexes in
/// <see cref="TypeMappingRegistry"/>. The legacy implementation scanned all type
/// mappings on every call; the indexed implementation must stay byte-for-byte
/// equivalent (including first-match ordering) while removing that scan.
/// </summary>
public class TypeMappingRegistryCanonicalIndexTests
{
    private static TypeMappingConfig CreateConfig() => new()
    {
        TypeMappings =
        [
            new() { CSharpType = "System.String", JavaType = "java.lang.String", Imports = [] },
            new() { CSharpType = "System.Collections.Generic.List`1", JavaType = "java.util.ArrayList", Imports = [] },
            new() { CSharpType = "System.Collections.Generic.List`2", JavaType = "java.util.LinkedList", Imports = [] },
            new() { CSharpType = "System.Collections.Generic.Dictionary`2", JavaType = "HashMap", Imports = ["other.HashMap", "java.util.HashMap"] },
            new() { CSharpType = "My.App.Widget", JavaType = "Widget", Imports = ["com.example.Widget", "other.Widget"] },
        ],
    };

    /// <summary>
    /// The exact pre-optimization algorithm, kept as an oracle. All JavaType values
    /// used in these tests are canonical (contain a dot) or resolved through the
    /// import index, so MapToCanonical's java.lang/java.util probes are irrelevant.
    /// </summary>
    private static string? ResolveLegacy(TypeMappingConfig config, string csharpTypeName)
    {
        var typeMappings = new Dictionary<string, TypeMappingEntry>();
        foreach (var mapping in config.TypeMappings)
            typeMappings[mapping.CSharpType] = mapping;

        var baseTypeName = csharpTypeName;
        var angleIdx = baseTypeName.IndexOf('<');
        if (angleIdx > 0)
            baseTypeName = baseTypeName.Substring(0, angleIdx);

        if (typeMappings.TryGetValue(csharpTypeName, out var entry))
            return MapToCanonicalLegacy(typeMappings, entry.JavaType);
        if (typeMappings.TryGetValue(baseTypeName, out entry))
            return MapToCanonicalLegacy(typeMappings, entry.JavaType);

        foreach (var kvp in typeMappings)
        {
            var backtickIdx = kvp.Key.LastIndexOf('`');
            if (backtickIdx < 0) continue;
            var keyBase = kvp.Key.Substring(0, backtickIdx);
            if (baseTypeName == keyBase || csharpTypeName.StartsWith(keyBase + "<"))
                return MapToCanonicalLegacy(typeMappings, kvp.Value.JavaType);
        }

        return null;
    }

    private static string? MapToCanonicalLegacy(
        Dictionary<string, TypeMappingEntry> typeMappings,
        string javaType)
    {
        if (javaType.Contains('.')) return javaType;

        foreach (var mapping in typeMappings.Values)
        {
            if (mapping.JavaType != javaType) continue;
            foreach (var import in mapping.Imports)
            {
                if (import.EndsWith("." + javaType))
                    return import;
            }
        }

        return null;
    }

    [Theory]
    [InlineData("System.String")]
    [InlineData("System.Collections.Generic.List`1")]
    [InlineData("System.Collections.Generic.List<string>")]
    [InlineData("System.Collections.Generic.List<int, string>")]
    [InlineData("System.Collections.Generic.List")]
    [InlineData("System.Collections.Generic.Dictionary<string, int>")]
    [InlineData("My.App.Widget")]
    [InlineData("My.App.Widget<int>")]
    [InlineData("My.App.Unknown")]
    [InlineData("My.App.Unknown<int>")]
    [InlineData("<anonymous>")]
    [InlineData("")]
    public void ResolveJavaCanonicalName_MatchesLegacyLinearScan(string csharpTypeName)
    {
        var config = CreateConfig();
        var registry = new TypeMappingRegistry(config);

        Assert.Equal(ResolveLegacy(config, csharpTypeName), registry.ResolveJavaCanonicalName(csharpTypeName));
    }

    [Fact]
    public void ResolveJavaCanonicalName_GenericBaseCollision_KeepsFirstInsertedMapping()
    {
        var config = CreateConfig();
        var registry = new TypeMappingRegistry(config);

        // List`1 is inserted before List`2; both share the base name.
        Assert.Equal("java.util.ArrayList", registry.ResolveJavaCanonicalName("System.Collections.Generic.List<int>"));
        Assert.Equal(ResolveLegacy(config, "System.Collections.Generic.List<int>"),
            registry.ResolveJavaCanonicalName("System.Collections.Generic.List<int>"));
    }

    [Fact]
    public void MapToCanonical_KeepsFirstMatchingImport()
    {
        var config = CreateConfig();
        var registry = new TypeMappingRegistry(config);

        // No JavaLibraryIndex is injected, so the import index is the only resolver.
        Assert.Equal("other.HashMap", registry.ResolveJavaCanonicalName("System.Collections.Generic.Dictionary`2"));
        Assert.Equal("com.example.Widget", registry.ResolveJavaCanonicalName("My.App.Widget"));
    }

    [Fact]
    public void Registry_PrebuildsCanonicalLookupIndexes()
    {
        var registry = new TypeMappingRegistry(CreateConfig());
        var registryType = typeof(TypeMappingRegistry);

        var genericIndex = registryType.GetField("_genericTypeBaseIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        var importIndex = registryType.GetField("_javaSimpleTypeImportIndex", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(genericIndex);
        Assert.NotNull(importIndex);

        var genericValues = (System.Collections.IDictionary)genericIndex!.GetValue(registry)!;
        var importValues = (System.Collections.IDictionary)importIndex!.GetValue(registry)!;

        // List`1/List`2 collapse to one base name, Dictionary`2 adds a second.
        Assert.Equal(2, genericValues.Count);
        Assert.Equal(2, importValues.Count);
    }
}
