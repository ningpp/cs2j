using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Test-module reflection batch:
/// 1. Overrides of "protected internal" base methods must widen to public
///    (base "protected internal abstract" maps to public in Java).
/// 2. Type.GenericTypeArguments → TypeHelper.getGenericTypeArguments.
/// 3. proxy.MakeGenericType(args) on a variable receiver → TypeHelper.makeGenericType.
/// Regression: tests/TestUtilities + PrecompiledRegexScenarioTest.
/// </summary>
public class TestReflectionHelperTests
{
    [Fact]
    public void OverrideOfProtectedInternalBaseMethod_WidensToPublic()
    {
        var result = Convert(@"
class Base
{
    protected internal abstract int CreateInstance();
}
class Derived : Base
{
    protected override int CreateInstance() { return 1; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("public abstract int createInstance();", code);
        Assert.Contains("public int createInstance()", code);
        Assert.DoesNotContain("protected int createInstance()", code);
    }

    [Fact]
    public void GenericTypeArgumentsAndMakeGenericType_RouteThroughTypeHelper()
    {
        var result = Convert(@"
using System;
class C
{
    static Type GetProxyType(Type type, Type[] genericTypeArguments)
    {
        Type proxyType = type.GetTypeInfo().BaseType;
        if (genericTypeArguments.Length > 0)
        {
            proxyType = proxyType.MakeGenericType(genericTypeArguments);
        }
        return proxyType;
    }

    static Type[] GetArgs(Type type)
    {
        return type.GenericTypeArguments;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("TypeHelper.getGenericTypeArguments(type)", code);
        Assert.Contains("TypeHelper.makeGenericType(proxyType", code);
        Assert.DoesNotContain(".makeGenericType(genericTypeArguments)", code);
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
