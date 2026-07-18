using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StringCollectionTypeMappingTests
{
    [Fact]
    public void StringCollection_FieldAndReturn_MappedToCSharpListOfString()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using System.Collections.Specialized;

class Sample
{
    private StringCollection _warnings;

    public StringCollection GetWarnings()
    {
        if (_warnings == null)
        {
            _warnings = new StringCollection();
        }
        _warnings.Add("warning");
        return _warnings;
    }
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("StringCollection", code, StringComparison.Ordinal);
        Assert.Contains("CSharpList<String>", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpList;", code, StringComparison.Ordinal);
    }
}
