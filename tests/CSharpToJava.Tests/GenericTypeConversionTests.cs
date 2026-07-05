using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class GenericTypeConversionTests
{
    // ─── typeof() with generic types ───

    [Fact]
    public void TypeOf_IListDouble_ProducesListClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<double>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList.class", result.GeneratedCode);
        Assert.DoesNotContain("CSharpGenericIList<Double>.class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_IListString_ProducesListClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<string>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList.class", result.GeneratedCode);
        Assert.DoesNotContain("CSharpGenericIList<String>.class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_IListDateTime_ProducesListClass()
    {
        var result = Convert(@"
using System;
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<DateTime>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList.class", result.GeneratedCode);
        Assert.DoesNotContain("CSharpGenericIList<", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_IListByteArray_ProducesListClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<byte[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList[]>.class", code);
        Assert.DoesNotContain("CSharpGenericIList<byte[]>.class", code);
    }

    [Fact]
    public void TypeOf_IListIntArray_ProducesListClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<int[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList[]", code);
    }

    [Fact]
    public void TypeOf_IListStringArray_ProducesListClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<string[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList[]", code);
    }

    [Fact]
    public void TypeOf_DictionaryStringByteArray_ProducesLinkedHashMapClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(Dictionary<string, byte[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpDictionary.class", code);
        Assert.DoesNotContain("CSharpDictionary<", code);
    }

    [Fact]
    public void TypeOf_IListGenericArray_ProducesListArrayClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<int>[]); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList[].class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_IListStringArrayArray_ProducesListArrayClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<string>[]); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList[].class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_NestedGeneric_ProducesRawClass()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Type t = typeof(IList<IList<string>>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList.class", result.GeneratedCode);
        Assert.DoesNotContain("CSharpGenericIList<", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_MultipleGenericTypesInArray_AllStripped()
    {
        var result = Convert(@"
using System;
using System.Collections.Generic;
class Test {
    private static readonly Type[] s_types = {
        typeof(IList<double>),
        typeof(IList<string>),
        typeof(IList<DateTime>),
        typeof(IList<byte[]>)
    };
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList.class", code);
        Assert.DoesNotContain("CSharpGenericIList<", code);
        Assert.DoesNotContain("CSharpGenericIList[]>.class", code);
        Assert.DoesNotContain("IList", code);
    }

    [Fact]
    public void TypeOf_PrimitiveType_ProducesPrimitiveClass()
    {
        var result = Convert(@"
class Test {
    void M() { Type t = typeof(int); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("int.class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_PrimitiveArray_ProducesPrimitiveArrayClass()
    {
        var result = Convert(@"
class Test {
    void M() { Type t = typeof(byte[]); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("byte[].class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_FuncWithPrimitiveArgs_ProducesRawClass()
    {
        var result = Convert(@"
using System;
class Test {
    void M() { Type t = typeof(Func<int, string>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("Function.class", result.GeneratedCode);
        Assert.DoesNotContain("Function<", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_ActionWithArrayArg_ProducesRawClass()
    {
        var result = Convert(@"
using System;
class Test {
    void M() { Type t = typeof(Action<byte[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Consumer.class", code);
        Assert.DoesNotContain("Consumer<", code);
        Assert.DoesNotContain("[]>.class", code);
    }

    // ─── instanceof with generic types ───

    [Fact]
    public void InstanceOf_GenericType_ProducesRawInstanceof()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { bool b = obj is IList<string>; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof CSharpGenericIList", result.GeneratedCode);
        Assert.DoesNotContain("instanceof CSharpGenericIList<", result.GeneratedCode);
    }

    [Fact]
    public void InstanceOf_GenericWithArrayArg_ProducesRawInstanceof()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { bool b = obj is IList<byte[]>; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("instanceof CSharpGenericIList", code);
        Assert.DoesNotContain("instanceof CSharpGenericIList<", code);
        Assert.DoesNotContain("instanceof CSharpGenericIList[]", code);
    }

    [Fact]
    public void InstanceOf_GenericArray_ProducesArrayInstanceof()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { bool b = obj is IList<string>[]; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof CSharpGenericIList[]", result.GeneratedCode);
    }

    [Fact]
    public void InstanceOf_NestedGenericArray_ProducesArrayInstanceof()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { bool b = obj is IList<IList<string>>[]; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof CSharpGenericIList[]", result.GeneratedCode);
    }

    [Fact]
    public void InstanceOf_DictionaryWithArrayArg_ProducesRawInstanceof()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { bool b = obj is Dictionary<string, byte[]>; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("instanceof CSharpDictionary", code);
        Assert.DoesNotContain("instanceof CSharpDictionary<", code);
    }

    [Fact]
    public void InstanceOf_Primitive_ProducesBoxedInstanceof()
    {
        var result = Convert(@"
class Test {
    void M(object obj) { bool b = obj is int; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof Integer", result.GeneratedCode);
    }

    [Fact]
    public void InstanceOf_PrimitiveArray_ProducesPrimitiveArrayInstanceof()
    {
        var result = Convert(@"
class Test {
    void M(object obj) { bool b = obj is byte[]; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof byte[]", result.GeneratedCode);
    }

    // ─── as with generic types ───

    [Fact]
    public void As_GenericType_ProducesInstanceofAndCast()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { var x = obj as IList<string>; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("instanceof CSharpGenericIList", code);
        Assert.DoesNotContain("instanceof CSharpGenericIList<", code);
    }

    [Fact]
    public void As_GenericWithArrayArg_ProducesInstanceofAndCast()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { var x = obj as IList<byte[]>; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("instanceof CSharpGenericIList", code);
        Assert.DoesNotContain("instanceof CSharpGenericIList<", code);
        Assert.DoesNotContain("instanceof CSharpGenericIList[]", code);
    }

    [Fact]
    public void As_GenericArray_ProducesArrayInstanceofAndCast()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { var x = obj as IList<string>[]; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof CSharpGenericIList[]", result.GeneratedCode);
    }

    // ─── cast with generic types ───

    [Fact]
    public void Cast_ToGenericWithArrayArg_ProducesCast()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { var x = (IList<byte[]>)obj; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList<byte[]>", code);
    }

    [Fact]
    public void Cast_ToGenericArray_ProducesArrayCast()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(object obj) { var x = (IList<string>[])obj; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList<String>[]", code);
    }

    // ─── Variable declarations with generic types ───

    [Fact]
    public void Variable_IListByteArray_ProducesCorrectJavaType()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { IList<byte[]> list = null; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<byte[]>", result.GeneratedCode);
    }

    [Fact]
    public void Variable_IListDouble_ProducesBoxedGenericArg()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { IList<double> list = null; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<Double>", result.GeneratedCode);
    }

    [Fact]
    public void Variable_IListInt_ProducesBoxedGenericArg()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { IList<int> list = null; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<Integer>", result.GeneratedCode);
    }

    [Fact]
    public void Variable_DictionaryStringByteArray_ProducesCorrectJavaType()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { Dictionary<string, byte[]> dict = null; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpDictionary<String, byte[]>", result.GeneratedCode);
    }

    [Fact]
    public void Variable_NestedGeneric_ProducesCorrectJavaType()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { IList<IList<string>> list = null; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<CSharpGenericIList<String>>", result.GeneratedCode);
    }

    // ─── Method parameters with generic types ───

    [Fact]
    public void Parameter_IListByteArray_ProducesCorrectJavaType()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(IList<byte[]> list) {}
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<byte[]>", result.GeneratedCode);
    }

    [Fact]
    public void Parameter_IListDouble_ProducesBoxedGenericArg()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(IList<double> list) {}
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<Double>", result.GeneratedCode);
    }

    // ─── Return types with generic types ───

    [Fact]
    public void ReturnType_IListByteArray_ProducesCorrectJavaType()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    IList<byte[]> GetList() => null;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<byte[]>", result.GeneratedCode);
    }

    [Fact]
    public void ReturnType_IListDouble_ProducesBoxedGenericArg()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    IList<double> GetList() => null;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<Double>", result.GeneratedCode);
    }

    // ─── Field declarations with generic types ───

    [Fact]
    public void Field_IListByteArray_ProducesCorrectJavaType()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    IList<byte[]> list;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<byte[]>", result.GeneratedCode);
    }

    [Fact]
    public void Field_IListDouble_ProducesBoxedGenericArg()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    IList<double> list;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("CSharpGenericIList<Double>", result.GeneratedCode);
    }

    // ─── default() with generic types ───

    [Fact]
    public void Default_GenericType_ProducesNull()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { var x = default(IList<string>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("null", result.GeneratedCode);
    }

    [Fact]
    public void Default_GenericWithArrayArg_ProducesNull()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { var x = default(IList<byte[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("null", result.GeneratedCode);
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
