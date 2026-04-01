using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for <see cref="JavaLibraryIndex"/> — the query API over Java metadata.
/// </summary>
public class JavaLibraryIndexTests
{
    private static readonly string JavaConfigDir = TestPaths.JavaConfigDir;

    private JavaLibraryIndex CreateIndex() => new(JavaConfigDir);

    // -----------------------------------------------------------------------
    // FindType
    // -----------------------------------------------------------------------

    [Fact]
    public void FindType_KnownType_ReturnsTypeInfo()
    {
        var index = CreateIndex();
        var typeInfo = index.FindType("java.lang.String");
        Assert.NotNull(typeInfo);
        Assert.Equal("java.lang.String", typeInfo.CanonicalName);
        Assert.Equal("class", typeInfo.Kind);
    }

    [Fact]
    public void FindType_UnknownType_ReturnsNull()
    {
        var index = CreateIndex();
        var typeInfo = index.FindType("com.example.NoSuchClass");
        Assert.Null(typeInfo);
    }

    [Fact]
    public void FindType_EmptyString_ThrowsArgumentException()
    {
        var index = CreateIndex();
        Assert.Throws<ArgumentException>(() => index.FindType(""));
    }

    [Fact]
    public void FindType_ArrayList_ReturnsCorrectInfo()
    {
        var index = CreateIndex();
        var typeInfo = index.FindType("java.util.ArrayList");
        Assert.NotNull(typeInfo);
        Assert.Equal("java.util.ArrayList", typeInfo.CanonicalName);
        Assert.Equal("class", typeInfo.Kind);
        Assert.Equal("java.base", typeInfo.ModuleName);
    }

    // -----------------------------------------------------------------------
    // FindMethods
    // -----------------------------------------------------------------------

    [Fact]
    public void FindMethods_ArrayList_Add_ReturnsOverloads()
    {
        var index = CreateIndex();
        var methods = index.FindMethods("java.util.ArrayList", "add");
        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Equal("add", m.Name));
    }

    [Fact]
    public void FindMethods_String_IndexOf_ReturnsOverloads()
    {
        var index = CreateIndex();
        var methods = index.FindMethods("java.lang.String", "indexOf");
        Assert.NotEmpty(methods);
    }

    [Fact]
    public void FindMethods_UnknownType_ReturnsEmptyList()
    {
        var index = CreateIndex();
        var methods = index.FindMethods("com.example.NoSuchClass", "someMethod");
        Assert.Empty(methods);
    }

    [Fact]
    public void FindMethods_KnownTypeButNoSuchMethod_ReturnsEmptyList()
    {
        var index = CreateIndex();
        var methods = index.FindMethods("java.lang.String", "noSuchMethod");
        Assert.Empty(methods);
    }

    [Fact]
    public void FindMethods_MethodInfo_HasReturnType()
    {
        var index = CreateIndex();
        var methods = index.FindMethods("java.util.ArrayList", "size");
        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.False(string.IsNullOrEmpty(m.ReturnType)));
    }

    // -----------------------------------------------------------------------
    // FindConstructors
    // -----------------------------------------------------------------------

    [Fact]
    public void FindConstructors_ArrayList_ReturnsConstructors()
    {
        var index = CreateIndex();
        var ctors = index.FindConstructors("java.util.ArrayList");
        Assert.NotEmpty(ctors);
    }

    [Fact]
    public void FindConstructors_UnknownType_ReturnsEmptyList()
    {
        var index = CreateIndex();
        var ctors = index.FindConstructors("com.example.NoSuchClass");
        Assert.Empty(ctors);
    }

    // -----------------------------------------------------------------------
    // GetSupertypes
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSupertypes_ArrayList_ContainsExpectedSupertypes()
    {
        var index = CreateIndex();
        var supertypes = index.GetSupertypes("java.util.ArrayList");

        // ArrayList extends AbstractList implements List, RandomAccess, Cloneable, Serializable
        Assert.Contains("java.util.AbstractList", supertypes);
        Assert.Contains("java.util.List", supertypes);
        Assert.Contains("java.io.Serializable", supertypes);
        Assert.Contains("java.lang.Cloneable", supertypes);
    }

    [Fact]
    public void GetSupertypes_ArrayList_ReachesObjectTransitively()
    {
        var index = CreateIndex();
        var supertypes = index.GetSupertypes("java.util.ArrayList");

        // java.lang.Object is the root of the hierarchy
        Assert.Contains("java.lang.Object", supertypes);
    }

    [Fact]
    public void GetSupertypes_UnknownType_ReturnsEmptyList()
    {
        var index = CreateIndex();
        var supertypes = index.GetSupertypes("com.example.NoSuchClass");
        Assert.Empty(supertypes);
    }

    [Fact]
    public void GetSupertypes_DoesNotContainTypeItself()
    {
        var index = CreateIndex();
        var supertypes = index.GetSupertypes("java.util.ArrayList");
        Assert.DoesNotContain("java.util.ArrayList", supertypes);
    }

    // -----------------------------------------------------------------------
    // IsAssignableTo
    // -----------------------------------------------------------------------

    [Fact]
    public void IsAssignableTo_SameType_ReturnsTrue()
    {
        var index = CreateIndex();
        Assert.True(index.IsAssignableTo("java.util.ArrayList", "java.util.ArrayList"));
    }

    [Fact]
    public void IsAssignableTo_ArrayListToList_ReturnsTrue()
    {
        var index = CreateIndex();
        Assert.True(index.IsAssignableTo("java.util.ArrayList", "java.util.List"));
    }

    [Fact]
    public void IsAssignableTo_ArrayListToObject_ReturnsTrue()
    {
        var index = CreateIndex();
        Assert.True(index.IsAssignableTo("java.util.ArrayList", "java.lang.Object"));
    }

    [Fact]
    public void IsAssignableTo_UnrelatedTypes_ReturnsFalse()
    {
        var index = CreateIndex();
        Assert.False(index.IsAssignableTo("java.lang.String", "java.util.ArrayList"));
    }

    // -----------------------------------------------------------------------
    // GetModuleFor
    // -----------------------------------------------------------------------

    [Fact]
    public void GetModuleFor_KnownType_ReturnsModuleName()
    {
        var index = CreateIndex();
        var module = index.GetModuleFor("java.lang.String");
        Assert.Equal("java.base", module);
    }

    [Fact]
    public void GetModuleFor_UnknownType_ReturnsNull()
    {
        var index = CreateIndex();
        var module = index.GetModuleFor("com.example.NoSuchClass");
        Assert.Null(module);
    }

    // -----------------------------------------------------------------------
    // KnownModuleNames
    // -----------------------------------------------------------------------

    [Fact]
    public void KnownModuleNames_ContainsJavaBase()
    {
        var index = CreateIndex();
        Assert.Contains("java.base", index.KnownModuleNames);
    }
}
