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
}
