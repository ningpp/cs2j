using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ExpressionBodiedThrowMethodTests
{
    [Fact]
    public void VoidExpressionBodiedThrowMethods_EmitThrowStatements()
    {
        var result = Convert("""
public class KeyCollection<TKey>
{
    public void Add(TKey item) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public void add(TKey item) {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("throw new UnsupportedOperationException();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public void clear() {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("java.util.function.Supplier", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("NotSupportedException", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "KeyCollection.cs",
            Options = new ConversionOptions(),
        });
    }
}
