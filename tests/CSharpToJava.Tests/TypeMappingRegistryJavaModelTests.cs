using CSharpToJava.TypeMapping;
using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Phase B: TypeMappingRegistry integration with JavaLibraryIndex.
/// Covers auto-deduction of method name mappings (PascalCase → camelCase),
/// validation APIs, and backward compatibility.
/// </summary>
public class TypeMappingRegistryJavaModelTests
{
    private static readonly string JavaConfigDir = TestPaths.JavaConfigDir;

    /// <summary>
    /// Creates a <see cref="TypeMappingRegistry"/> with the default TypeMappings.json
    /// configuration and optionally injects the Java metadata index.
    /// </summary>
    private static TypeMappingRegistry CreateRegistry(bool withJavaLibrary = true)
    {
        // Build a minimal config that maps common C# collection types
        var config = new TypeMappingConfig
        {
            TypeMappings = new List<TypeMappingEntry>
            {
                new() { CSharpType = "System.Collections.Generic.List`1", JavaType = "ArrayList", Imports = ["java.util.ArrayList"] },
                new() { CSharpType = "System.String", JavaType = "java.lang.String", Imports = [] },
                new() { CSharpType = "System.Collections.Generic.Dictionary`2", JavaType = "HashMap", Imports = ["java.util.HashMap"] },
                new() { CSharpType = "System.Collections.Generic.HashSet`1", JavaType = "HashSet", Imports = ["java.util.HashSet"] },
            },
            MethodMappings = new List<MethodMappingEntry>
            {
                new() { TypeName = "System.Collections.Generic.List`1", MethodName = "Add", JavaMethodName = "add" },
                new() { TypeName = "System.String", MethodName = "Contains", JavaMethodName = "contains" },
            },
        };

        var registry = new TypeMappingRegistry(config);
        if (withJavaLibrary)
        {
            registry.SetJavaLibraryIndex(new JavaLibraryIndex(JavaConfigDir));
        }
        return registry;
    }

    // -----------------------------------------------------------------------
    // PascalToCamelCase
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("Add", "add")]
    [InlineData("Contains", "contains")]
    [InlineData("GetHashCode", "getHashCode")]
    [InlineData("ToString", "toString")]
    [InlineData("isEmpty", "isEmpty")]  // already camelCase — unchanged
    [InlineData("", "")]
    [InlineData("A", "a")]
    public void PascalToCamelCase_ConvertsCases(string input, string expected)
    {
        Assert.Equal(expected, TypeMappingRegistry.PascalToCamelCase(input));
    }

    // -----------------------------------------------------------------------
    // MapMethod: existing config mappings still work
    // -----------------------------------------------------------------------

    [Fact]
    public void MapMethod_ExistingConfigMapping_ReturnsConfigured()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.MapMethod("System.String", "Contains");
        Assert.Equal("contains", result);
    }

    [Fact]
    public void MapMethod_ExistingConfigMapping_WithoutJavaLibrary_ReturnsConfigured()
    {
        var registry = CreateRegistry(withJavaLibrary: false);
        var result = registry.MapMethod("System.String", "Contains");
        Assert.Equal("contains", result);
    }

    // -----------------------------------------------------------------------
    // MapMethod: auto-deduction via Java metadata
    // -----------------------------------------------------------------------

    [Fact]
    public void MapMethod_AutoDeduction_HashMap_ContainsKey()
    {
        // "ContainsKey" is NOT in the config but HashMap has "containsKey" in Java metadata.
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.MapMethod("System.Collections.Generic.Dictionary<string, int>", "ContainsKey");
        Assert.Equal("containsKey", result);
    }

    [Fact]
    public void MapMethod_AutoDeduction_ArrayList_Size()
    {
        // "Size" is NOT in the config but ArrayList has "size" in Java metadata.
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.MapMethod("System.Collections.Generic.List<string>", "Size");
        Assert.Equal("size", result);
    }

    [Fact]
    public void MapMethod_AutoDeduction_WithoutJavaLibrary_ReturnsNull()
    {
        var registry = CreateRegistry(withJavaLibrary: false);
        var result = registry.MapMethod("System.Collections.Generic.Dictionary<string, int>", "ContainsKey");
        Assert.Null(result); // No auto-deduction without metadata
    }

    [Fact]
    public void MapMethod_NonExistentMethod_ReturnsNull()
    {
        // "NoSuchMethodInJava" camelCase is "noSuchMethodInJava" which doesn't exist
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.MapMethod("System.Collections.Generic.List<int>", "NoSuchMethodInJava");
        Assert.Null(result);
    }

    [Fact]
    public void MapMethod_AlreadyCamelCase_NotAutoDeduced()
    {
        // If the method name is already camelCase, PascalToCamelCase returns the same string,
        // so auto-deduction is skipped (it only fires when conversion actually changes something).
        // The config maps "Add" → "add" but not "add" → anything.
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.MapMethod("System.Collections.Generic.List<int>", "add");
        // "add" is already camelCase — no config entry for lowercase "add", auto-deduction skips it
        Assert.Null(result);
    }

    // -----------------------------------------------------------------------
    // ValidateTypeExists
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateTypeExists_KnownType_ReturnsTrue()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        Assert.True(registry.ValidateTypeExists("java.util.ArrayList"));
    }

    [Fact]
    public void ValidateTypeExists_UnknownType_ReturnsFalse()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        Assert.False(registry.ValidateTypeExists("com.example.NoSuchClass"));
    }

    [Fact]
    public void ValidateTypeExists_WithoutJavaLibrary_ReturnsTrue()
    {
        var registry = CreateRegistry(withJavaLibrary: false);
        Assert.True(registry.ValidateTypeExists("com.example.NoSuchClass")); // Optimistic
    }

    // -----------------------------------------------------------------------
    // ValidateMethodExists
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateMethodExists_KnownMethod_ReturnsTrue()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        Assert.True(registry.ValidateMethodExists("java.util.ArrayList", "add"));
    }

    [Fact]
    public void ValidateMethodExists_UnknownMethod_ReturnsFalse()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        Assert.False(registry.ValidateMethodExists("java.util.ArrayList", "noSuchMethod"));
    }

    [Fact]
    public void ValidateMethodExists_WithoutJavaLibrary_ReturnsTrue()
    {
        var registry = CreateRegistry(withJavaLibrary: false);
        Assert.True(registry.ValidateMethodExists("java.util.ArrayList", "noSuchMethod")); // Optimistic
    }

    // -----------------------------------------------------------------------
    // ResolveJavaCanonicalName
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveJavaCanonicalName_DirectMapping_String()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.ResolveJavaCanonicalName("System.String");
        Assert.Equal("java.lang.String", result);
    }

    [Fact]
    public void ResolveJavaCanonicalName_BacktickMapping_List()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.ResolveJavaCanonicalName("System.Collections.Generic.List<string>");
        Assert.Equal("java.util.ArrayList", result);
    }

    [Fact]
    public void ResolveJavaCanonicalName_UnknownType_ReturnsNull()
    {
        var registry = CreateRegistry(withJavaLibrary: true);
        var result = registry.ResolveJavaCanonicalName("My.Custom.Type");
        Assert.Null(result);
    }

    // -----------------------------------------------------------------------
    // SetJavaLibraryIndex
    // -----------------------------------------------------------------------

    [Fact]
    public void SetJavaLibraryIndex_NullByDefault()
    {
        var registry = new TypeMappingRegistry(new TypeMappingConfig());
        Assert.Null(registry.JavaLibrary);
    }

    [Fact]
    public void SetJavaLibraryIndex_SetsProperty()
    {
        var registry = new TypeMappingRegistry(new TypeMappingConfig());
        var index = new JavaLibraryIndex(JavaConfigDir);
        registry.SetJavaLibraryIndex(index);
        Assert.Same(index, registry.JavaLibrary);
    }
}
