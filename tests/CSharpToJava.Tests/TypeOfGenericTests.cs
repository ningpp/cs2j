using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class TypeOfGenericTests
{
    [Fact]
    public void TypeOf_SimpleType_ProducesClassLiteral()
    {
        var result = Convert(@"
class Test {
    Type GetFieldType() => typeof(string);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("String.class", code);
    }

    [Fact]
    public void TypeOf_PrimitiveType_ProducesWrapperClassLiteral()
    {
        var result = Convert(@"
class Test {
    Type GetIntType() => typeof(int);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        // typeof(int) → Integer.class because Java's int.class creates primitive arrays
        // that cannot be used as Object[] in generic contexts (e.g. new T[] via reflection)
        Assert.Contains("Integer.class", code);
    }

    [Fact]
    public void TypeOf_NonGenericReferenceType_ProducesClassLiteral()
    {
        var result = Convert(@"
class XPathItem {}
class Test {
    Type GetItemType() => typeof(XPathItem);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("XPathItem.class", code);
    }

    [Fact]
    public void TypeOf_FuncGenericType_StripsTypeArguments()
    {
        var result = Convert(@"
using System;
class Test {
    Type GetFuncType() => typeof(Func<int, string>);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Function.class", code);
        Assert.DoesNotContain("Function<Integer, String>.class", code);
    }

    [Fact]
    public void TypeOf_ActionGenericType_StripsTypeArguments()
    {
        var result = Convert(@"
using System;
class Test {
    Type GetActionType() => typeof(Action<string>);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Consumer.class", code);
        Assert.DoesNotContain("Consumer<String>.class", code);
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
