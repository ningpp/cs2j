using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class CollectionInterfaceBridgeTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void ICollectionImplementation_UsesAbstractCollectionAndJavaCompatibleSignatures()
    {
        const string code = """
            using System.Collections;
            using System.Collections.Generic;

            class S : ICollection<string>
            {
                private readonly HashSet<string> _set = new HashSet<string>();

                void ICollection<string>.Add(string item) { _set.Add(item); }
                public bool Contains(string item) => _set.Contains(item);
                public bool Remove(string item) => _set.Remove(item);
                public int Count => _set.Count;
                public bool IsReadOnly => false;
                public void Clear() => _set.Clear();
                public void CopyTo(string[] array, int arrayIndex) { }
                public IEnumerator<string> GetEnumerator() => _set.GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => _set.GetEnumerator();
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("extends java.util.AbstractCollection<String>", result.GeneratedCode);
        Assert.Contains("public boolean add(String item)", result.GeneratedCode);
        Assert.Contains("public boolean contains(Object item)", result.GeneratedCode);
        Assert.Contains("public boolean remove(Object item)", result.GeneratedCode);
        Assert.Contains("public int size()", result.GeneratedCode);
    }
}
