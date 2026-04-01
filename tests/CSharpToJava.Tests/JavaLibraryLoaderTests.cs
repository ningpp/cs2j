using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for <see cref="JavaLibraryLoader"/> — JSON loading and deserialization.
/// </summary>
public class JavaLibraryLoaderTests
{
    private static readonly string JavaConfigDir = TestPaths.JavaConfigDir;

    [Fact]
    public void DiscoverModuleNames_ReturnsAtLeastJavaBase()
    {
        var loader = new JavaLibraryLoader(JavaConfigDir);
        var modules = loader.DiscoverModuleNames();
        Assert.Contains("java.base", modules);
    }

    [Fact]
    public void LoadModule_JavaBase_ReturnsNonNull()
    {
        var loader = new JavaLibraryLoader(JavaConfigDir);
        var module = loader.LoadModule("java.base");
        Assert.NotNull(module);
        Assert.Equal("java.base", module.ModuleName);
    }

    [Fact]
    public void LoadModule_JavaBase_ContainsJavaLangString()
    {
        var loader = new JavaLibraryLoader(JavaConfigDir);
        var module = loader.LoadModule("java.base");
        Assert.NotNull(module);

        var stringType = module.FindType("java.lang.String");
        Assert.NotNull(stringType);
    }

    [Fact]
    public void LoadModule_JavaBase_StringHasExpectedFields()
    {
        var loader = new JavaLibraryLoader(JavaConfigDir);
        var module = loader.LoadModule("java.base");
        Assert.NotNull(module);

        var stringType = module.FindType("java.lang.String");
        Assert.NotNull(stringType);

        Assert.Equal("java.base", stringType.ModuleName);
        Assert.Equal("java.lang", stringType.PackageName);
        Assert.Equal("java.lang.String", stringType.BinaryName);
        Assert.Equal("java.lang.String", stringType.CanonicalName);
        Assert.Equal("String", stringType.SimpleName);
        Assert.Equal("class", stringType.Kind);
        Assert.Equal("java.lang.Object", stringType.SuperClass);
        Assert.Contains("java.io.Serializable", stringType.Interfaces);
        Assert.Contains("java.lang.Comparable", stringType.Interfaces);
        Assert.NotEmpty(stringType.DeclaredPublicConstructors);
        Assert.NotEmpty(stringType.DeclaredPublicMethods);
    }

    [Fact]
    public void LoadModule_JavaBase_StringHasParameterInfoOnConstructor()
    {
        var loader = new JavaLibraryLoader(JavaConfigDir);
        var module = loader.LoadModule("java.base");
        Assert.NotNull(module);

        var stringType = module.FindType("java.lang.String");
        Assert.NotNull(stringType);

        // At least one overload should have parameters
        var withParams = stringType.DeclaredPublicConstructors
            .FirstOrDefault(c => c.Parameters.Count > 0);
        Assert.NotNull(withParams);
        Assert.NotEmpty(withParams.Parameters[0].Type);
    }

    [Fact]
    public void LoadModule_NonExistentModule_ReturnsNull()
    {
        var loader = new JavaLibraryLoader(JavaConfigDir);
        var module = loader.LoadModule("no.such.module");
        Assert.Null(module);
    }

    [Fact]
    public void LoadAll_ReturnsLibraryWithAllModules()
    {
        var loader = new JavaLibraryLoader(JavaConfigDir);
        var library = loader.LoadAll();
        Assert.NotNull(library);
        Assert.NotEmpty(library.ModulesByName);
        Assert.True(library.ModulesByName.ContainsKey("java.base"));
    }

    [Fact]
    public void Constructor_EmptyRootDirectory_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new JavaLibraryLoader(""));
    }
}
