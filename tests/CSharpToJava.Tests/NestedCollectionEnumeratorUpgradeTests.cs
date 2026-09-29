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
