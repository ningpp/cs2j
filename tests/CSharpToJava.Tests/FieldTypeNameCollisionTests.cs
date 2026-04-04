using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that when a field name differs from its type only in case (e.g., field "xmlTextReader"
/// of type "XmlTextReader"), instance method calls on the field use the field name, not the type name.
/// Regression: the member/type shadowing guard in InvocationExpressionTransformer replaced the
/// receiver with the type name, producing "XmlTextReader.close()" (static call) instead of
/// "xmlTextReader.close()" (instance call).
/// </summary>
public class FieldTypeNameCollisionTests
{
    [Fact]
    public void FieldWithSameNameAsType_MethodCall_UsesFieldName()
    {
        var source = @"
using System;

class XmlTextReader : IDisposable {
    public void Close() { }
    public void Dispose() { Close(); }
}

class GeometryGraphReader : IDisposable {
    readonly XmlTextReader xmlTextReader;

    public GeometryGraphReader(XmlTextReader reader) {
        xmlTextReader = reader;
    }

    protected virtual void Dispose(bool disposing) {
        if (disposing)
            xmlTextReader.Close();
    }

    public void Dispose() {
        Dispose(true);
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        var code = result.GeneratedCode!;

        // The field-based call should use lowercase "xmlTextReader.close()"
        Assert.Contains("xmlTextReader.close()", code);

        // Should NOT have uppercase "XmlTextReader.close()" which would be a static call
        Assert.DoesNotContain("XmlTextReader.close()", code);
    }

    [Fact]
    public void FieldWithSameNameAsType_PropertyAccess_UsesFieldName()
    {
        var source = @"
class TextReader {
    public int LineNumber { get; set; }
}

class Reader {
    readonly TextReader textReader;

    public Reader(TextReader r) { textReader = r; }

    int GetLine() {
        return textReader.LineNumber;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        var code = result.GeneratedCode!;

        // Should use field name "textReader", not type name "TextReader"
        Assert.Contains("textReader.getLineNumber()", code);
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
