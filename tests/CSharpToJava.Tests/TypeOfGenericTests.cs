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
    public void TypeOf_GenericType_StripsTypeArguments()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    Type GetListType() => typeof(IList<string>);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList<String>.class", code);
        Assert.DoesNotContain("IList", code);
    }

    [Fact]
    public void TypeOf_GenericTypeWithPrimitiveArg_StripsAndBoxes()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    Type GetListType() => typeof(IList<int>);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList<Integer>.class", code);
    }

    [Fact]
    public void TypeOf_NestedGeneric_StripsAllTypeArguments()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    Type GetDictType() => typeof(Dictionary<string, List<int>>);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpDictionary.class", code);
        Assert.DoesNotContain("CSharpDictionary<", code);
    }

    [Fact]
    public void TypeOf_GenericTypeInArrayInitializer_StripsTypeArguments()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    private static readonly Type[] s_types = {
        typeof(IList<string>),
        typeof(IList<int>),
        typeof(string)
    };
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.Contains("String.class", code);
        Assert.DoesNotContain("CSharpGenericIList<String>.class", code);
        Assert.DoesNotContain("CSharpGenericIList<Integer>.class", code);
    }

    [Fact]
    public void TypeOf_GenericTypeAssignedToVariable_StripsTypeArguments()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() {
        Type t = typeof(IList<string>);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList<String>.class", code);
    }

    [Fact]
    public void TypeOf_PrimitiveType_ProducesPrimitiveClassLiteral()
    {
        var result = Convert(@"
class Test {
    Type GetIntType() => typeof(int);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("int.class", code);
    }

    [Fact]
    public void TypeOf_ArrayOfGenericElementType_StripsTypeArguments()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    Type GetArrayType() => typeof(IList<string>[]);
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList[].class", code);
        Assert.DoesNotContain("CSharpGenericIList<String>[].class", code);
    }

    [Fact]
    public void TypeOf_MultipleGenericTypesInFieldArray_AllStripped()
    {
        var result = Convert(@"
using System.Collections.Generic;
class XmlILTypeHelper {
    private static readonly Type[] s_typeCodeToCachedStorage = {
        typeof(IList<string>),
        typeof(IList<byte[]>),
        typeof(IList<long>),
        typeof(IList<int>)
    };
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList<String>.class", code);
        Assert.DoesNotContain("CSharpGenericIList<byte[]>.class", code);
        Assert.DoesNotContain("CSharpGenericIList<Long>.class", code);
        Assert.DoesNotContain("CSharpGenericIList<Integer>.class", code);
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
    public void TypeOf_GenericTypeWithCustomTypeArg_StripsTypeArguments()
    {
        var result = Convert(@"
using System.Collections.Generic;
class XPathItem {}
class XPathNavigator {}
class Test {
    void M() {
        Type t1 = typeof(IList<XPathItem>);
        Type t2 = typeof(IList<XPathNavigator>);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList<XPathItem>.class", code);
        Assert.DoesNotContain("CSharpGenericIList<XPathNavigator>.class", code);
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
