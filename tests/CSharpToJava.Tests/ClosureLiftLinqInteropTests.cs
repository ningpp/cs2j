using System.IO;
using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C32 × E16 interop (design §7.3 tail): a mutably captured local referenced
/// inside a LINQ lambda is holder-lifted first; the holder FIELD access is an
/// ordinary member expression and must pass through procedural LINQ rewriting
/// transparently (holder instance captured as an extracted-method parameter).
/// </summary>
public sealed class ClosureLiftLinqInteropTests
{
    [Fact]
    public void LiftedWhereCapture_Proceduralizes_WithHolderField()
    {
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class C
{
    public List<int> Filter(List<int> items)
    {
        var threshold = 0;
        threshold = 5;
        return items.Where(v => v > threshold).ToList();
    }
}";

        var preResult = new ClosureLiftPreprocessor().Preprocess(
            csharp, model: null, new ClosureLiftOptions());
        Assert.True(preResult.Changed);
        Assert.Contains("__cs2jClosure", preResult.OutputCode);

        var result = ConvertProcedural(preResult.OutputCode);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode);
        Assert.Contains("_cs2jCap_threshold", result.GeneratedCode);
    }

    [Fact]
    public void LiftedAllCapture_Proceduralizes_WithHolderField()
    {
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class C
{
    public bool Check(List<int> items)
    {
        var limit = 0;
        limit = 3;
        return items.All(n => n > limit);
    }
}";

        var preResult = new ClosureLiftPreprocessor().Preprocess(
            csharp, model: null, new ClosureLiftOptions());
        Assert.True(preResult.Changed);

        var result = ConvertProcedural(preResult.OutputCode);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode);
        Assert.Contains("_cs2jCap_limit", result.GeneratedCode);
    }

    private static ConversionResult ConvertProcedural(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
            },
        });
    }
}
