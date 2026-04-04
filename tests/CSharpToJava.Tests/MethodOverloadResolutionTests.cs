using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Issue #5: Method overload resolution with parameter count awareness.
/// Verifies that MapMethod can disambiguate overloads using the signature field.
/// </summary>
public class MethodOverloadResolutionTests
{
    [Fact]
    public void MapMethod_WithoutSignature_ReturnsSingleEntry()
    {
        var config = new TypeMappingConfig
        {
            MethodMappings = new()
            {
                new MethodMappingEntry
                {
                    TypeName = "System.String",
                    MethodName = "Substring",
                    JavaMethodName = "substring",
                }
            }
        };
        var registry = new TypeMappingRegistry(config);

        Assert.Equal("substring", registry.MapMethod("System.String", "Substring"));
        Assert.Equal("substring", registry.MapMethod("System.String", "Substring", 1));
        Assert.Equal("substring", registry.MapMethod("System.String", "Substring", 2));
    }

    [Fact]
    public void MapMethod_WithSignature_DisambiguatesByParamCount()
    {
        var config = new TypeMappingConfig
        {
            MethodMappings = new()
            {
                new MethodMappingEntry
                {
                    TypeName = "MyLib.Converter",
                    MethodName = "Convert",
                    JavaMethodName = "convertSimple",
                    Signature = "1",
                },
                new MethodMappingEntry
                {
                    TypeName = "MyLib.Converter",
                    MethodName = "Convert",
                    JavaMethodName = "convertWithOptions",
                    Signature = "2",
                },
            }
        };
        var registry = new TypeMappingRegistry(config);

        // Exact signature match
        Assert.Equal("convertSimple", registry.MapMethod("MyLib.Converter", "Convert", 1));
        Assert.Equal("convertWithOptions", registry.MapMethod("MyLib.Converter", "Convert", 2));
    }

    [Fact]
    public void MapMethod_WithSignature_FallbackToNoSignature()
    {
        var config = new TypeMappingConfig
        {
            MethodMappings = new()
            {
                new MethodMappingEntry
                {
                    TypeName = "MyLib.Converter",
                    MethodName = "Convert",
                    JavaMethodName = "convertDefault",
                    // No signature — acts as default fallback
                },
                new MethodMappingEntry
                {
                    TypeName = "MyLib.Converter",
                    MethodName = "Convert",
                    JavaMethodName = "convertWithOptions",
                    Signature = "3",
                },
            }
        };
        var registry = new TypeMappingRegistry(config);

        // Exact match for 3 params
        Assert.Equal("convertWithOptions", registry.MapMethod("MyLib.Converter", "Convert", 3));
        // No match for 1 param — falls back to unsignatured entry
        Assert.Equal("convertDefault", registry.MapMethod("MyLib.Converter", "Convert", 1));
        // No paramCount specified — falls back to unsignatured entry
        Assert.Equal("convertDefault", registry.MapMethod("MyLib.Converter", "Convert"));
    }

    [Fact]
    public void MapMethod_BackwardCompatible_NoParamCount()
    {
        var config = new TypeMappingConfig
        {
            MethodMappings = new()
            {
                new MethodMappingEntry
                {
                    TypeName = "System.String",
                    MethodName = "Contains",
                    JavaMethodName = "contains",
                }
            }
        };
        var registry = new TypeMappingRegistry(config);

        // Old-style call without paramCount still works
        Assert.Equal("contains", registry.MapMethod("System.String", "Contains"));
    }

    [Fact]
    public void MapMethod_MultipleSignaturedEntries_NoParamCount_ReturnsFirst()
    {
        var config = new TypeMappingConfig
        {
            MethodMappings = new()
            {
                new MethodMappingEntry
                {
                    TypeName = "MyLib.Converter",
                    MethodName = "Convert",
                    JavaMethodName = "convertA",
                    Signature = "1",
                },
                new MethodMappingEntry
                {
                    TypeName = "MyLib.Converter",
                    MethodName = "Convert",
                    JavaMethodName = "convertB",
                    Signature = "2",
                },
            }
        };
        var registry = new TypeMappingRegistry(config);

        // No paramCount, no fallback entry → returns first entry
        Assert.Equal("convertA", registry.MapMethod("MyLib.Converter", "Convert"));
    }
}
