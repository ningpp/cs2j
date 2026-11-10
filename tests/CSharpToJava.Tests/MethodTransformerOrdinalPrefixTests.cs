using System.Globalization;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the ordinal get_/set_ prefix checks in
/// <see cref="MethodTransformer.GetJavaMethodName"/>. The prefix must be matched
/// independently of the current culture and without an ICU round-trip.
/// </summary>
public class MethodTransformerOrdinalPrefixTests
{
    private static ConversionContext CreateContext()
    {
        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
        return new ConversionContext(options, new TypeMappingRegistry(options.TypeMappingConfigPath));
    }

    [Theory]
    [InlineData("get_Length", "Length")]
    [InlineData("set_Length", "Length")]
    [InlineData("get_Item", "Item")]
    [InlineData("set_Item", "Item")]
    public void GetJavaMethodName_AccessorPrefix_IsMatchedOrdinally(string csharpName, string expected)
    {
        Assert.Equal(expected, MethodTransformer.GetJavaMethodName(csharpName, sym: null, CreateContext()));
    }

    [Fact]
    public void GetJavaMethodName_AccessorPrefix_IsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("Length", MethodTransformer.GetJavaMethodName("get_Length", sym: null, CreateContext()));
            Assert.Equal("Item", MethodTransformer.GetJavaMethodName("set_Item", sym: null, CreateContext()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
