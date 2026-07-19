using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# interface base interfaces are emitted as Java extends clauses.
/// </summary>
public class InterfaceInheritanceTests
{
    [Fact]
    public void InterfaceWithBaseInterface_EmitsExtendsClause()
    {
        var result = Convert(@"
public interface IBase
{
    int getValue();
}

public interface IDerived : IBase
{
    int getOther();
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public interface IDerived extends IBase", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InterfaceWithInternalBaseInterface_EmitsExtendsClause()
    {
        var result = Convert(@"
internal interface IBase
{
    int getValue();
}

internal interface IDerived : IBase
{
    int getOther();
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public interface IDerived extends IBase", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfaceWithBaseInterface_ProjectMerge_EmitsExtendsClause()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-interface-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "IBase.cs"), @"
namespace TestNs
{
    public interface IBase
    {
        int getValue();
    }
}
");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "IDerived.cs"), @"
namespace TestNs
{
    public interface IDerived : IBase
    {
        int getOther();
    }
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var derived = Assert.Single(results, r =>
                string.Equals(r.FileName, "IDerived.java", StringComparison.OrdinalIgnoreCase));
            Assert.True(derived.Success, string.Join("\n", derived.Diagnostics));
            Assert.Contains("public interface IDerived extends IBase", derived.GeneratedCode, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
