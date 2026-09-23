using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ReadOnlyDictInterfaceTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void TryGetValueOutVarInt_OnHashtable_CastsGetResult()
    {
        var result = Convert(@"
using System.Collections;
class C {
    void Test(Hashtable table, int key) {
        if (table.TryGetValue(key, out int value)) {
            System.Console.WriteLine(value);
        }
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        // The out-variable declaration is hoisted before the if (C# out-variable
        // scope covers the whole block); the get() result is cast inside the then-branch.
        Assert.Contains("Integer value = null;", code, StringComparison.Ordinal);
        Assert.Contains("value = (Integer) ", code, StringComparison.Ordinal);
        Assert.Contains(".get(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicMethodCreateDelegate_UsesInstanceCall()
    {
        var result = Convert(@"
using System;
using System.Reflection.Emit;
class C {
    private DynamicMethod _method;
    private Action<string> _go;
    void Test() {
        _go = _method.CreateDelegate<Action<string>>();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("_method.createDelegate(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReflectionHelper.createDelegate", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValueOutVar_UsedAfterIf_DeclaredBeforeIf()
    {
        var result = Convert(@"
using System.Collections.Generic;
class C {
    RegexHolder _cache = new RegexHolder();
    object Get(string key) {
        if (!_cache.TryGet(key, out object regex)) {
            regex = new object();
            _cache.Add(key, regex);
        }
        return regex;
    }
}
class RegexHolder {
    public bool TryGet(string key, out object value) { value = null; return false; }
    public void Add(string key, object value) {}
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode ?? "";
        var ifIndex = code.IndexOf("if (");
        var declIndex = code.IndexOf("Object regex = ", StringComparison.Ordinal);
        Assert.True(declIndex >= 0 && declIndex < ifIndex,
            $"out-var declaration must precede the if statement. Generated code:\n{code}");
    }
}
