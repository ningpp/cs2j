using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that project-level partial interface merging preserves correct
/// System.Tuple&lt;T1, T2&gt; → Map.Entry mapping and does not incorrectly
/// wrap the second type argument in List&lt;&gt; (which is reserved for
/// IGrouping&lt;K, V&gt; → Map.Entry&lt;K, List&lt;V&gt;&gt;).
/// </summary>
public class PartialInterfaceTupleMappingTests
{
    [Fact]
    public async Task PartialInterface_MergedAsyncTupleReturn_MapsToMapEntryWithoutListWrapping()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-partial-tuple-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "IDtdParserAdapter.cs"), @"
namespace TestNs
{
    internal partial interface IDtdParserAdapter
    {
        int ReadData();
    }
}
");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "IDtdParserAdapterAsync.cs"), @"
using System;
using System.Threading.Tasks;

namespace TestNs
{
    internal partial interface IDtdParserAdapter
    {
        Task<Tuple<int, bool>> PushEntityAsync(IDtdEntityInfo entity);
    }

    internal interface IDtdEntityInfo { }
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var adapter = Assert.Single(results, r =>
                string.Equals(r.FileName, "IDtdParserAdapter.java", StringComparison.OrdinalIgnoreCase));
            Assert.True(adapter.Success, string.Join("\n", adapter.Diagnostics));
            Assert.Contains("Map.Entry<Integer, Boolean>", adapter.GeneratedCode, StringComparison.Ordinal);
            Assert.DoesNotContain("Map.Entry<Integer, List<Boolean>>", adapter.GeneratedCode, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
