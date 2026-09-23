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

    [Fact]
    public void NewFunc_FromCustomDelegate_GeneratesLambdaWrapper()
    {
        var result = Convert(@"
using System;

public delegate CTestModule ModuleGenerator();

public class CTestModule { }

public class Sample
{
    public static Func<CTestModule> Test(ModuleGenerator gen)
    {
        return new Func<CTestModule>(gen);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("() -> gen.get()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return gen;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NewDelegate_LambdaInObjectArray_CastsToDelegateType()
    {
        var result = Convert(@"
using System;

public delegate string MatchEvaluator(string match);

public class Sample
{
    public static void Test()
    {
        object[] data = new object[] { ""a"", new MatchEvaluator(m => ""uhoh""), 0 };
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The cast to MatchEvaluator must be present so the lambda has a target type
        // when stored in Object[].
        Assert.Matches(@"\(MatchEvaluator\)\s*\(m\s*->", result.GeneratedCode);
    }

    [Fact]
    public void NewDelegate_MethodGroupInObjectArray_CastsToDelegateType()
    {
        var result = Convert(@"
using System;

public delegate string MatchEvaluator(string match);

public class Sample
{
    public static string Eval(string m) => m;

    public static void Test()
    {
        object[] data = new object[] { ""a"", new MatchEvaluator(Eval), 0 };
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Matches(@"\(MatchEvaluator\)\s*\(Sample::eval", result.GeneratedCode);
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
