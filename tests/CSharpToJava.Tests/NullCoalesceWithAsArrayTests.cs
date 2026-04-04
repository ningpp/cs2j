using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class NullCoalesceWithAsArrayTests
{
    [Fact]
    public void IEnumerableAsArrayCoalesceToArray_SimplifiesToFallback()
    {
        var csharp = @"
using System.Collections.Generic;
using System.Linq;

public class C
{
    int[] items;
    public void M(IEnumerable<int> values)
    {
        items = values as int[] ?? values.ToArray();
    }
}";
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharp,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            },
        });
        Assert.True(result.Success, result.GeneratedCode);
        // Should NOT contain 'var _coalesce = null' pattern
        Assert.DoesNotContain("_coalesce", result.GeneratedCode);
        // Should NOT contain 'var' with null assignment
        Assert.DoesNotContain("= null;", result.GeneratedCode);
    }
}
