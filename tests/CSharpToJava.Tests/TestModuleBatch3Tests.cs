using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Test-module batch 3:
/// 1. var with a null literal initializer must not emit Java var (uninferable).
/// 2. Nullable&lt;T&gt;.HasValue on a boxed wrapper must emit != null, not an unbox call.
/// 3. PropertyInfo.GetGetMethod/GetSetMethod must map to getGetMethod/getSetMethod
///    (the camelCase name collides with the GetMethod property accessor guard).
/// Regression: tests/TestUtilities FileCleanupTestBase + DebuggerAttributes.
/// </summary>
public class TestModuleBatch3Tests
{
    [Fact]
    public void VarWithNullInitializer_EmitObject()
    {
        var result = Convert(@"
class C
{
    object M()
    {
        var _last = null;
        return _last;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Object _last = null;", code);
        Assert.DoesNotContain("var _last = null;", code);
    }

    [Fact]
    public void VarWithDefaultReferenceTypeInitializer_KeepsResolvedType()
    {
        var result = Convert(@"
class C
{
    object M()
    {
        var _last = default(string);
        return _last;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("String _last = null;", code);
        Assert.DoesNotContain("var _last = null;", code);
    }

    [Fact]
    public void SingleOrDefaultRewrittenLast_NullInitializerTyped()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class C
{
    static string Pick(IEnumerable<string> attrs)
    {
        return attrs.SingleOrDefault(a => a.Length > 0);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("var _last = null;", code);
        Assert.Contains("_last = null;", code);
    }

    [Fact]
    public void NullableHasValue_EmitNullCheck()
    {
        var result = Convert(@"
using System;
class C
{
    static string Pick(int? index)
    {
        return index.HasValue ? ""a"" : ""b"";
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("index != null", code);
        Assert.DoesNotContain("index.intValue() ?", code);
    }

    [Fact]
    public void PropertyInfoGetGetMethod_MapsToCamelCase()
    {
        var result = Convert(@"
using System.Reflection;
class C
{
    static bool HasGetter(PropertyInfo pi)
    {
        return pi.GetGetMethod() != null && pi.GetSetMethod() != null;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("pi.getGetMethod()", code);
        Assert.Contains("pi.getSetMethod()", code);
        Assert.DoesNotContain("pi.GetGetMethod()", code);
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
