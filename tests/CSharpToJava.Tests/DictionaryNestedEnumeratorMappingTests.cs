using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that nested-type references (Dictionary&lt;K,V&gt;.Enumerator) resolve
/// the outer type through the type mapping table — the compat nested class is
/// CSharpDictionary.Enumerator, not an unqualified "Dictionary" which javac
/// cannot find. Reproduces DictionaryOfTKeyTValueConverter.OnWriteResume.
/// </summary>
public class DictionaryNestedEnumeratorMappingTests
{

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
