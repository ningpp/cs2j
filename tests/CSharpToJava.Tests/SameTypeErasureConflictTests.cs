using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SameTypeErasureConflictTests
{

    [Fact]
    public void DifferentTypeParamCount_UsesExistingNtpSuffix()
    {
        // Existing behavior must stay unchanged
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    void M(object x) { }
    void M<T>(T x) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;
        // Method with 0 type params (fewer) gets _0tp
        Assert.Contains("void m_0tp", code);
        // Method with 1 type param (more) keeps original name without suffix.
        // Java syntax puts type parameters before the return type: <T> void m(T x)
        Assert.Contains("<T> void m", code);
    }

    [Fact]
    public async Task SameNameNestedDelegates_DifferentArity_BothSurviveWithRename()
    {
        // JsonClassInfo declares two delegates named ParameterizedConstructorDelegate
        // with different type-parameter counts. Java forbids two nested types with
        // the same simple name, so the later arity must be renamed (Name_arity) at
        // both the declaration and all reference sites.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-delegate-arity-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Sample.cs"), @"
class Sample
{
    public delegate T Del<T>(object[] args);
    public delegate T Del<T, TArg0, TArg1>(TArg0 arg0, TArg1 arg1);

    public Del<T> Create<T>() => null;
    public Del<T, TArg0, TArg1> Create2<T, TArg0, TArg1>() => null;
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);
            var sample = Assert.Single(results);
            Assert.True(sample.Success, string.Join("\n", sample.Diagnostics));

            var code = sample.GeneratedCode;

            // Both delegate interfaces must be emitted with distinct names.
            Assert.Contains("interface Del<T>", code);
            Assert.Contains("interface Del_3<T, TArg0, TArg1>", code);

            // The renamed delegate must be referenced under its Java name.
            Assert.Contains("Del_3<T, TArg0, TArg1> create2", code);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
