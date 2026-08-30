using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces RTree.GetAllIntersecting pattern where ToArray() appears in
/// a conditional expression alongside `new T[0]`. The generated
/// TypeHelper.listToArray(...) returns Object, which is incompatible with
/// the T[] from the other branch in a ternary — the converter must wrap
/// the listToArray call in a cast.
/// </summary>
public class ListToArrayConditionalExpressionTests
{
    [Fact]
    public void ToArrayInConditionalExpression_WithNewTArray_WrapsListToArrayInCast()
    {
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;

class RTree<T>
{
    private List<T> _items = new List<T>();
    private int _count;

    public T[] GetAllIntersecting(int query)
    {
        return _count == 0 ? new T[0] : _items.ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The listToArray call must be wrapped in a cast so the conditional
        // expression type is consistent (both branches resolve to T[]).
        // If listToArray appears bare (no cast), the conditional fails:
        // "Object cannot be converted to T[]"
        Assert.Contains("TypeHelper.listToArray", code, StringComparison.Ordinal);

        // listToArray must be preceded by a cast (e.g. (T[]) (Object) TypeHelper.listToArray)
        // so the conditional expression type is consistent.
        var listToArrayIndex = code.IndexOf("TypeHelper.listToArray", StringComparison.Ordinal);
        Assert.True(listToArrayIndex > 0, "TypeHelper.listToArray not found. Generated:\n" + code);
        var beforeListToArray = code.Substring(0, listToArrayIndex);
        // The cast should end with ) right before TypeHelper
        Assert.True(
            beforeListToArray.TrimEnd().EndsWith(")"),
            "listToArray must be preceded by a cast. Generated:\n" + code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
