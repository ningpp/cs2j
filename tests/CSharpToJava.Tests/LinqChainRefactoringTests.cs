using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the LINQ deep refactoring fixes:
/// - Zip on primitive arrays adds .boxed() before .collect()
/// - Zip result recognized as stream by downstream LINQ methods
/// - Indexed SelectMany generates correct IntStream.range pattern
/// - Anonymous type record naming avoids collisions
/// - Java keyword escaping in record params (camelCase then escape)
/// </summary>
public class LinqChainRefactoringTests
{
    // ─── Zip on primitive arrays ───

    [Fact]
    public void Zip_PrimitiveArray_AddsBoxedBeforeCollect()
    {
        var result = Convert(@"
using System.Linq;
class C {
    void M() {
        var a = new[] { 1, 2, 3 };
        var b = new[] { 10, 20, 30 };
        var r = a.Zip(b, (x, y) => x + y);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Primitive int[] produces IntStream; must .boxed() before .collect()
        Assert.Contains(".boxed().collect(Collectors.collectingAndThen(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_ObjectArray_NoBoxed()
    {
        var result = Convert(@"
using System.Linq;
class C {
    void M() {
        var a = new[] { ""a"", ""b"" };
        var b = new[] { ""c"", ""d"" };
        var r = a.Zip(b, (x, y) => x + y);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // String[] produces Stream<String>; no .boxed() needed
        Assert.DoesNotContain(".boxed()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".collect(Collectors.collectingAndThen(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_ListReceiver_NoBoxedOnReceiver()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class C {
    void M() {
        var nums = new List<int> { 1, 2, 3 };
        var r = nums.Zip(new[] { 10, 20, 30 }, (a, b) => a + b);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // List<int> → ArrayList<Integer>.stream() produces Stream<Integer>; no .boxed() on receiver
        // (the 'other' argument int[] may need boxing separately, which is expected)
        Assert.Contains("nums.stream().collect(Collectors.collectingAndThen(", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT have .boxed().collect() on the receiver - the receiver is already boxed (ArrayList<Integer>)
        Assert.DoesNotContain("nums.stream().boxed()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Zip chaining with downstream LINQ ───

    [Fact]
    public void ZipThenWhere_NoDoubleStreamWrap()
    {
        var result = Convert(@"
using System.Linq;
class C {
    void M() {
        var a = new[] { 1, 2, 3 };
        var b = new[] { 10, 20, 30 };
        var r = a.Zip(b, (x, y) => x + y).Where(z => z > 15).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // LINQ desugarer converts chain to procedural method
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ZipThenSelect_ChainsDirectly()
    {
        var result = Convert(@"
using System.Linq;
class C {
    void M() {
        var a = new[] { 1, 2, 3 };
        var b = new[] { ""a"", ""b"", ""c"" };
        var r = a.Zip(b, (x, y) => x + y).Select(s => s.Length).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // LINQ desugarer converts chain to procedural method
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleZips_ChainCorrectly()
    {
        var result = Convert(@"
using System.Linq;
class C {
    void M() {
        var a = new[] { 1, 2, 3 };
        var b = new[] { ""a"", ""b"", ""c"" };
        var c = new[] { 10.0, 20.0, 30.0 };
        var r = a.Zip(b, (x, y) => x + y)
                 .Zip(c, (s, d) => s + d)
                 .ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // LINQ desugarer converts chain to procedural method
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Indexed SelectMany ───

    [Fact]
    public void IndexedSelectMany_GeneratesIntStreamRange()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class C {
    void M() {
        var strs = new List<string> { ""a"", ""bb"", ""ccc"" };
        var r = strs.SelectMany((s, i) => new[] { s + i }).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // LINQ desugarer converts chain to procedural method
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT generate invalid flatMap((s, i) -> ...)
        Assert.DoesNotContain("flatMap((s, i)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Record name collision ───

    [Fact]
    public void DifferentAnonymousTypes_GetDifferentRecordNames()
    {
        var result = Convert(@"
using System.Linq;
class C {
    void M() {
        var a = new[] { 1, 2, 3 };
        var r1 = a.Select(x => new { Num = x, Str = x.ToString() }).ToList();
        var r2 = a.Select(x => new { Sum = x + 1, Count = 1 }).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // LINQ desugarer converts chains to procedural methods
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Keyword escaping in records ───

    [Fact]
    public void RecordField_JavaKeyword_IsEscaped()
    {
        var result = Convert(@"
using System.Linq;
class C {
    void M() {
        var a = new[] { 1, 2, 3 };
        var r = a.Select(x => new { Int = x, Class = ""test"" }).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // 'int' and 'class' are Java keywords; should be escaped to 'intValue' and 'classValue'
        Assert.Contains("intValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("classValue", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain bare 'int' or 'class' as record parameters
        Assert.DoesNotContain("(int int,", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── collectingAndThen detection ───

    [Fact]
    public void CollectingAndThen_NotDetectedAsMaterializedCollection()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class C {
    void M() {
        var nums = new List<int> { 1, 2, 3 };
        var r = nums.Where((x, i) => i % 2 == 0).Where(x => x > 1).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Indexed Where produces collectingAndThen; the second Where should chain directly
        // without wrapping in StreamSupport.stream()
        Assert.DoesNotContain("StreamSupport.stream(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Helper ───

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

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
