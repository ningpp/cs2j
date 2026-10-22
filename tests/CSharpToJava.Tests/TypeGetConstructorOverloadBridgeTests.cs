using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Type.GetConstructor(BindingFlags, Binder, Type[], ParameterModifier[]) has no
/// java.lang.Class counterpart of that arity; the bridge must lower it to the
/// compat helper TypeHelper.getConstructorInfo(type, bindingFlags, types) by
/// overload signature, dropping the always-null Binder and the COM-interop
/// ParameterModifier[]. Regression from dotnet-text-regularexpressions
/// RegexAssemblyCompiler.GenerateRegexDefaultCtor. The 1-arg
/// GetConstructor(Type[]) keeps its public-instance mapping.
/// </summary>
public class TypeGetConstructorOverloadBridgeTests
{
    private static async Task<string> ConvertAsync(string source)
    {
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[] { new SourceFile { FilePath = "Test.cs", Content = source } },
                projectName: "Demo");
        foreach (var failure in results.Where(r => !r.Success))
            throw new Xunit.Sdk.XunitException(
                string.Join("\n", failure.Diagnostics) + "\n---\n" + failure.GeneratedCode);
        return string.Join("\n", results.Select(r => r.GeneratedCode));
    }

    [Fact]
    public async Task FourArgGetConstructor_LowersToCompatHelper()
    {
        var code = await ConvertAsync("""
            using System;
            using System.Reflection;

            public class Test
            {
                public ConstructorInfo Find()
                {
                    return typeof(Test).GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, Array.Empty<ParameterModifier>())!;
                }
            }
            """);

        // (Class, int bindingFlags, Class[] types) — the compat helper that
        // implements the C# bindingFlags lookup.
        Assert.Contains("TypeHelper.getConstructorInfo(Test.class, ", code);
        Assert.Contains("new Class[0])", code);
        // The Binder (null) and ParameterModifier[] arguments must be dropped,
        // otherwise the call arity has no matching Java overload.
        Assert.DoesNotContain("CSharpArray.empty()", code);
        // The arity-shaped mapped name must not survive for the 4-arg overload.
        Assert.DoesNotContain("getConstructor(Test.class", code);
    }

    [Fact]
    public async Task FourArgGetConstructorNamedArguments_LowersToCompatHelper()
    {
        var code = await ConvertAsync("""
            using System;
            using System.Reflection;

            public class Test
            {
                public ConstructorInfo Find()
                {
                    return typeof(Test).GetConstructor(BindingFlags.Public | BindingFlags.Instance, binder: null, Type.EmptyTypes, modifiers: null)!;
                }
            }
            """);

        Assert.Contains("TypeHelper.getConstructorInfo(Test.class, ", code);
        Assert.DoesNotContain(", binder:", code);
        Assert.DoesNotContain(", modifiers:", code);
    }

    [Fact]
    public async Task OneArgGetConstructor_KeepsPublicInstanceMapping()
    {
        var code = await ConvertAsync("""
            using System;

            public class Test
            {
                public ConstructorInfo Find()
                {
                    return typeof(Test).GetConstructor(Type.EmptyTypes)!;
                }
            }
            """);

        Assert.Contains("TypeHelper.getConstructor(Test.class, new Class[0])", code);
    }
}
