using System;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that nested collection classes implementing ICollection<Object> have their
/// iterator() return type upgraded to CSharpGenericEnumerator<Object> and their
/// nested enumerator class also upgraded to implement CSharpGenericEnumerator<Object>.
/// Reproduces the XmlSchemaObjectTable pattern where the outer class does NOT implement
/// IEnumerable itself, but its nested NamesCollection/ValuesCollection classes do.
/// </summary>
public class NestedCollectionEnumeratorUpgradeTests
{

    [Fact(Skip = "Pending: yield method timing — iterator() not yet created when UpgradeIteratorReturnTypeForGenericIterable runs")]
    public void CollectionBaseInheritingClass_IteratorReturnTypeUpgraded()
    {
        // Reproduces XmlSchemas : CollectionBase, IEnumerable<XmlSchema> pattern
        // where the Java class extends CSharpCollectionBase (which implements CSharpIList)
        // but does not directly list CSharpICollection<Object> in its ImplementedTypes.
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class MySchemas : CollectionBase, IEnumerable<MySchema>
{
    public void Add(MySchema schema)
    {
        InnerList.Add(schema);
    }

    public new IEnumerator<MySchema> GetEnumerator()
    {
        return new MySchemaEnumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class MySchema { }

public class MySchemaEnumerator : IEnumerator<MySchema>
{
    private MySchemas _col;
    private int _index = -1;

    public MySchemaEnumerator(MySchemas col) { _col = col; }
    public MySchema Current => (MySchema)_col.InnerList[_index];
    object IEnumerator.Current => Current;
    public bool MoveNext() => ++_index < _col.Count;
    public void Reset() => _index = -1;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // iterator() return type should be CSharpGenericEnumerator, not CSharpEnumerator
        Assert.DoesNotContain("CSharpEnumerator iterator()", code);
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
