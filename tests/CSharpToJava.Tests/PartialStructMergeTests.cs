using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for partial struct merging. The struct dispatch in TypeGroupResolver used
/// only the first syntax node, silently dropping members of partial structs split
/// across files (e.g. ref partial struct Utf8JsonReader with TryGet.cs methods).
/// </summary>
public class PartialStructMergeTests
{
    [Fact]
    public async Task PartialStruct_AllFilesMerged()
    {
        var treeA = CSharpSyntaxTree.ParseText(@"
public partial struct Part
{
    public int First() { return 1; }
}");
        var treeB = CSharpSyntaxTree.ParseText(@"
public partial struct Part
{
    public int Second() { return 2; }
}");

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { treeA, treeB },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(compilation);

        var result = results.FirstOrDefault(r => r.FileName == "Part.java");
        Assert.NotNull(result);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int first()", result.GeneratedCode);
        Assert.Contains("int second()", result.GeneratedCode);
    }

    /// <summary>
    /// partial 嵌套类型的第二段成员必须在外层类作用域之外发射：擦除冲突改名按
    /// CurrentType 判定，留在外层会把与外层同名同签名的实现方法误改成 _2
    /// （真实链 XmlTextReaderImpl.DtdParserProxy.ParsePIAsync 不再覆盖
    /// IDtdParserAdapter.parsePIAsync）。
    /// </summary>
    [Fact]
    public void NestedPartial_SecondPartMembers_AreScopedToNestedType()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Text;
using System.Threading.Tasks;

internal interface IDtdParserAdapter
{
    Task ParsePIAsync(StringBuilder sb);
}

internal class Outer
{
    private Task<bool> ParsePIAsync(StringBuilder sb)
    {
        return Task.FromResult(true);
    }

    internal partial class Proxy : IDtdParserAdapter
    {
        public Task<int> ReadDataAsync()
        {
            return Task.FromResult(0);
        }
    }

    internal partial class Proxy
    {
        public Task ParsePIAsync(StringBuilder sb)
        {
            return Task.CompletedTask;
        }
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("public CompletableFuture parsePIAsync(StringBuilder sb)", code);
        Assert.DoesNotContain("parsePIAsync_2", code);
    }
}
