using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// PropertyInfo.getValue wraps array results (ArrayPropertyWrapper emulates .NET's
/// Array.ToString), so a converted `p.GetValue(o) as T[]` must unwrap the wrapper
/// before its instanceof/cast — otherwise the cast site always answers null.
/// </summary>
public class PropertyGetValueAsArrayTests
{
    private static string Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success);
        return result.GeneratedCode;
    }

    [Fact]
    public void GetValueAsArray_UnwrapsWrapperBeforeCast()
    {
        var java = Convert("""
            using System.Reflection;
            public class Host {
                public int[] Read(PropertyInfo p) {
                    int[] items = p.GetValue(this) as int[];
                    return items;
                }
            }
            """);
        Assert.Contains("PropertyInfo.unwrapArrayValue(", java);
        // The unwrap feeds the instanceof/cast pair of the lowered `as`.
        var unwrapIndex = java.IndexOf("PropertyInfo.unwrapArrayValue(", StringComparison.Ordinal);
        var castIndex = java.IndexOf("instanceof int[]", StringComparison.Ordinal);
        Assert.True(unwrapIndex >= 0 && castIndex > unwrapIndex,
            $"expected the unwrap before the instanceof, got:{Environment.NewLine}{java}");
    }

    [Fact]
    public void PlainLocalAsArray_DoesNotUnwrap()
    {
        var java = Convert("""
            public class Host {
                public int[] Read(object value) {
                    int[] items = value as int[];
                    return items;
                }
            }
            """);
        Assert.DoesNotContain("unwrapArrayValue", java);
        Assert.Contains("instanceof int[]", java);
    }

    [Fact]
    public void GetValueAsNonArray_DoesNotUnwrap()
    {
        var java = Convert("""
            using System.Reflection;
            public class Host {
                public string Read(PropertyInfo p) {
                    string s = p.GetValue(this) as string;
                    return s;
                }
            }
            """);
        Assert.DoesNotContain("unwrapArrayValue", java);
    }
}
