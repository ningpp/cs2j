using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class ConsoleAndSerializationMappingTests
{
    [Fact]
    public void ConsoleErrorWriteLine_MapsToSystemErr()
    {
        var result = Convert("""
using System;

class Test
{
    void M()
    {
        Console.Error.Write("state");
        Console.Error.WriteLine("{0}", 42);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("System.err.print(\"state\");", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("System.err.println(String.format(\"%s\", 42));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.getError()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionSerializationConstructor_IsOmitted()
    {
        var result = Convert("""
using System;
using System.Runtime.Serialization;

class AcceptException : Exception
{
    internal AcceptException()
    {
    }

    protected AcceptException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("AcceptException()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SerializationInfo", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("StreamingContext", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("super(info, context)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringLength_MapsToLengthMethod_NotArrayField()
    {
        var result = Convert("""
class Test
{
    int M(string value)
    {
        return value.Length;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value.length()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("value.length;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqToXmlTypes_MapToCompatClasses()
    {
        var result = Convert("""
using System.Xml.Linq;

class Test
{
    string M(string xml)
    {
        XDocument doc = XDocument.Parse(xml);
        foreach (XElement element in doc.Descendants())
        {
            if (element.Name.LocalName == "Node")
            {
                return element.Attribute("Id").Value;
            }
        }
        return null;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("XDocument doc = XDocument.parse(xml);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("doc.descendants()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("element.getName()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("element.attribute(\"Id\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("XDocument.Parse", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("doc.Descendants", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringSplit_RemoveEmptyEntries_EscapesControlSeparators()
    {
        var result = Convert("""
using System;

class Test
{
    string[] M(string text)
    {
        return text.Split(new char[] { ' ', ',', '\n', '\r', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("text.split(\"[ ,\\n\\r;\\t]\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("text.split(\"[ ,\n", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(";\t]\")", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorBaseInitializer_WithNull_IsEmittedAsSuperCall()
    {
        var result = Convert("""
class Base
{
    public Base(object value)
    {
    }
}

class Parser : Base
{
    public Parser() : base(null)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public Parser() {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super(null);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public Parser();", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ReflectionFieldInfo_NullComparison_UsesJavaNullOperator()
    {
        var result = Convert("""
using System;
using System.Reflection;

class Tokens
{
    public const int maxParseToken = 10;
}

class Scanner
{
    int GetMaxToken()
    {
        FieldInfo f = typeof(Tokens).GetField("maxParseToken");
        return f == null ? int.MaxValue : (int)f.GetValue(null);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import java.lang.reflect.Field;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Field f =", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("f == null ? Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("f.get(null)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Field.valueEquals", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticAndInstanceParameterlessConstructors_AreBothEmitted()
    {
        var result = Convert("""
abstract class Base
{
    protected Base(object scanner)
    {
    }
}

class Parser : Base
{
    static Parser()
    {
        int x = 0;
    }

    public Parser() : base(null)
    {
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("static {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Parser() {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("super(null);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatExceptionCatch_DoesNotDuplicateArgumentExceptionCatch()
    {
        var result = Convert("""
using System;

class Test
{
    int M(string text)
    {
        try
        {
            return int.Parse(text);
        }
        catch (FormatException)
        {
            return -1;
        }
        catch (ArgumentException)
        {
            return -2;
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("catch (NumberFormatException _ex)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("catch (IllegalArgumentException _ex)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = CreateOptions(),
        });
    }

    private static ConversionOptions CreateOptions()
        => new()
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
}
