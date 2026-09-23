using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the C# non-generic IList.Add(object) → Java CSharpIList.add(Object)
/// return type bridge. C# IList.Add returns int (insertion index), but the
/// Java compat CSharpGenericIList.add() returns boolean. When a class implements
/// CSharpIList and delegates to a CSharpGenericIList, the generated method body
/// must convert boolean → int (return insertion index).
/// </summary>
public class NonGenericIListAddReturnTypeTests
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
