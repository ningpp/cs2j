using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a generic dictionary subclass with a hidden indexer produces
/// Java method signatures compatible with CSharpDictionary&lt;K,V&gt;.get(K).
/// </summary>
public class MyDictIndexerTests
{
    [Fact]
    public void DictionarySubclass_WithHiddenIndexer_GetUsesGenericKey()
    {
        var result = Convert(@"
using System.Collections.Generic;

namespace OLEDB.Test.ModuleCore
{
    public class MyDict<Type1, Type2> : Dictionary<Type1, Type2>
    {
        public new Type2 this[Type1 key]
        {
            get
            {
                if (ContainsKey(key))
                {
                    return base[key];
                }
                return default(Type2);
            }
            set
            {
                base[key] = value;
            }
        }
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // The subclass indexer must keep the generic key type so that it overrides
        // CSharpDictionary<K,V>.get(K) after type erasure.
        Assert.Contains("public Type2 get(Type1 key)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Type2 set(Type1 key, Type2 value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super.get(key)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public Type2 get(Object key)", result.GeneratedCode, StringComparison.Ordinal);
    }

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
