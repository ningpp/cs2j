using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regressions found while converting the AGL solution: generic collection
/// types that had no compat mapping, ported dotnet.xml property spellings, and
/// the two-argument Math helpers that Java's Math does not expose.
/// </summary>
public class AglRound1MappingTests
{
    [Fact]
    public void GenericQueueLinkedListAndSortedDictionary_MapToCompatTypes()
    {
        var result = Convert("""
using System.Collections.Generic;

public class Demo
{
    public void Foo()
    {
        var q = new Queue<int>();
        q.Enqueue(1);
        var v = q.Dequeue();

        var list = new LinkedList<int>();
        var node = list.AddFirst(v);
        var x = node.Value;

        var sorted = new SortedDictionary<int, string>();
        sorted[1] = "a";
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("CSharpQueue<Integer>", code);
        Assert.Contains("CSharpLinkedList<Integer>", code);
        Assert.Contains("CSharpLinkedListNode", code);
        Assert.Contains("CSharpSortedDict<Integer, String>", code);
        Assert.Contains("io.github.ningpp.compat.CSharpQueue", code);
        Assert.Contains("io.github.ningpp.compat.CSharpLinkedList", code);
        Assert.Contains("io.github.ningpp.compat.CSharpSortedDict", code);
    }

    [Fact]
    public void XmlReaderProperties_UsePortedBeanGetters()
    {
        var result = Convert("""
using System.Xml;

public class Demo
{
    public bool Foo(XmlReader reader, XmlTextReader textReader, XmlReaderSettings settings)
    {
        settings.IgnoreComments = true;
        if (reader.NodeType == XmlNodeType.EndElement)
            return reader.IsEmptyElement;
        return reader.Name.Length == 0 && textReader.LineNumber > 0;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // XmlReaderSettings properties are member-desugar-expanded accessors.
        Assert.Contains("IgnoreComments(true)", code);
        // XmlReader/XmlTextReader properties are retained as bean getters in the port.
        Assert.Contains(".getNodeType()", code);
        Assert.Contains(".getName()", code);
        Assert.Contains(".getIsEmptyElement()", code);
        Assert.Contains(".getLineNumber()", code);
        Assert.DoesNotContain(".NodeType()", code);
        Assert.DoesNotContain(".Name()", code);
        Assert.DoesNotContain(".IsEmptyElement()", code);
        Assert.Contains("import dotnet.xml.XmlNodeType;", code);
    }

    [Fact]
    public void NonRetainedExternalProperty_KeepsMappedAccessor()
    {
        var result = Convert("""
using System.Text.RegularExpressions;

public class Demo
{
    public string Foo(Capture capture) => capture.Value;
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Capture.Value is neither virtual nor an interface implementation, so the
        // port expands it into the C11 accessor Value(); it must not become getValue().
        Assert.Contains(".Value()", code);
        Assert.DoesNotContain(".getValue()", code);
    }

    [Fact]
    public void TwoArgumentMathHelpers_MapToMathHelper()
    {
        var result = Convert("""
using System;

public class Demo
{
    public double Foo()
    {
        return Math.Log(8, 2) + Math.Round(1.2345, 2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("MathHelper.log(8, 2)", code);
        Assert.Contains("MathHelper.round(1.2345, 2)", code);
    }

    [Fact]
    public void ArrayLinqTerminals_UseStreamTerminals()
    {
        var result = Convert("""
using System.Linq;

public class Demo
{
    public int Foo(int[] values)
    {
        if (values.Any())
            return values.First() + values.Last();
        return 0;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains(".anyMatch(", code);
        Assert.Contains(".findFirst()", code);
        Assert.Contains(".reduce((__cs2jA, __cs2jB) -> __cs2jB)", code);
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
