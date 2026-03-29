using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class DeadCodeAfterThrowTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void ICollectionAdd_ThrowBody_NoDeadReturnTrue()
    {
        const string code = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            class ReadOnlySet : ICollection<string>
            {
                void ICollection<string>.Add(string item) { throw new NotImplementedException(); }
                public bool Contains(string item) => false;
                public bool Remove(string item) => false;
                public int Count => 0;
                public bool IsReadOnly => true;
                public void Clear() { throw new NotImplementedException(); }
                public void CopyTo(string[] array, int arrayIndex) { }
                public IEnumerator<string> GetEnumerator() => throw new NotImplementedException();
                IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // add() should contain throw but NOT a dead "return true" after it
        Assert.Contains("throw new UnsupportedOperationException()", result.GeneratedCode);
        Assert.DoesNotContain("throw new UnsupportedOperationException();\n        return true;", result.GeneratedCode);
    }

    [Fact]
    public void IListAdd_ThrowBody_NoDeadReturnTrue()
    {
        const string code = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            class ReadOnlyList : IList<int>
            {
                public int this[int index] { get => 0; set { throw new NotImplementedException(); } }
                public int Count => 0;
                public bool IsReadOnly => true;
                public void Add(int item) { throw new NotImplementedException(); }
                public void Clear() { throw new NotImplementedException(); }
                public bool Contains(int item) => false;
                public void CopyTo(int[] array, int arrayIndex) { }
                public IEnumerator<int> GetEnumerator() => throw new NotImplementedException();
                public int IndexOf(int item) => -1;
                public void Insert(int index, int item) { throw new NotImplementedException(); }
                public bool Remove(int item) => false;
                public void RemoveAt(int index) { throw new NotImplementedException(); }
                IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        Assert.DoesNotContain("throw new UnsupportedOperationException();\n        return true;", result.GeneratedCode);
    }

    [Fact]
    public void IListSet_ThrowBody_NoDeadReturnValue()
    {
        // set() body with throw → should NOT append return _setOldValue_
        const string code = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            class ReadOnlyList2 : IList<string>
            {
                public string this[int index] { get => ""; set { throw new NotImplementedException(); } }
                public int Count => 0;
                public bool IsReadOnly => true;
                public void Add(string item) { throw new NotImplementedException(); }
                public void Clear() { throw new NotImplementedException(); }
                public bool Contains(string item) => false;
                public void CopyTo(string[] array, int arrayIndex) { }
                public IEnumerator<string> GetEnumerator() => throw new NotImplementedException();
                public int IndexOf(string item) => -1;
                public void Insert(int index, string item) { throw new NotImplementedException(); }
                public bool Remove(string item) => false;
                public void RemoveAt(int index) { throw new NotImplementedException(); }
                IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        Assert.DoesNotContain("return _setOldValue_", result.GeneratedCode);
    }
}
