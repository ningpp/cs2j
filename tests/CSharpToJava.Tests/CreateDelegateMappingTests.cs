using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CreateDelegateMappingTests
{

    [Fact]
    public void Debug_OutputGeneratedCode()
    {
        var testCases = new (string Name, string Code)[]
        {
            ("Generic_Action", @"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate<Action>();
    }
}"),
            ("Generic_Action_WithTarget", @"
using System;
using System.Reflection;

public class Sample
{
    public static void Test(Sample obj)
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate<Action>(obj);
    }
}"),
            ("ThenInvoke", @"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate(typeof(Action));
        action();
    }
}"),
            ("Generic_Func", @"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Compute"");
        var func = mi.CreateDelegate<Func<int, string>>();
    }
}"),
        };

        foreach (var (name, code) in testCases)
        {
            var result = Convert(code);
            Assert.True(result.Success, $"Failed for {name}: {string.Join("\n", result.Diagnostics)}");
        }
    }

    [Fact]
    public void CreateDelegate_DoesNotAffectNonMethodInfoReceiver()
    {
        var result = Convert(@"
using System;

public class Sample
{
    public static void Test()
    {
        var x = ""hello"";
        x = x.ToUpper();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ReflectionHelper.createDelegate", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_DoesNotProduceInvalidJavaClassLiteral()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Compute"");
        var func = mi.CreateDelegate<Func<int, string>>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("Func<", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Func<int,", result.GeneratedCode, StringComparison.Ordinal);
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
