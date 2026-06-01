using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CreateDelegateMappingTests
{
    [Fact]
    public void CreateDelegate_WithActionType_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate(typeof(Action));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Runnable.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("mi.createDelegate(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_WithFuncType_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Compute"");
        var func = mi.CreateDelegate(typeof(Func<int, string>));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Function.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_WithTarget_UsesReflectionHelperWithTarget()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test(Sample obj)
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate(typeof(Action), obj);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, obj, Runnable.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

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
            System.IO.File.AppendAllText(@"d:\code\cs2j\debug_output.txt", $"=== {name} ===\n{result.GeneratedCode}\n\n");
        }
    }

    [Fact]
    public void CreateDelegate_GenericNoArgs_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate<Action>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Runnable.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_GenericWithTarget_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test(Sample obj)
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate<Action>(obj);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, obj, Runnable.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_CustomDelegateType_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public delegate void MyCallback(int x);

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var callback = mi.CreateDelegate(typeof(MyCallback));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, MyCallback.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_ThenInvoke_MapsToSamMethod()
    {
        var result = Convert(@"
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
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Runnable.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".run()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_WithNullTarget_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate(typeof(Action), null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, null, Runnable.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_GenericFuncType_UsesReflectionHelper()
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
        Assert.Contains("ReflectionHelper.createDelegate(mi, Function.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
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
    public void CreateDelegate_GenericPredicateType_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Check"");
        var pred = mi.CreateDelegate<Predicate<int>>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Predicate.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_GenericFuncTwoArgs_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Compute"");
        var func = mi.CreateDelegate<Func<int, int, string>>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, BiFunction.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_GenericThenInvoke_MapsToSamMethod()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate<Action>();
        action();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Runnable.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".run()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_FuncReturnType_InvokeMapsToApply()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Compute"");
        var func = mi.CreateDelegate(typeof(Func<int, string>));
        string s = func(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Function.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".apply(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_GenericFunc_InvokeMapsToApply()
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
        string s = func(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Function.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".apply(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_WithPredicateType_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Check"");
        var pred = mi.CreateDelegate(typeof(Predicate<int>));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Predicate.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
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
    public void CreateDelegate_GenericAction_DoesNotProduceActionClass()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""DoWork"");
        var action = mi.CreateDelegate<Action>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("Action.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Runnable.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_WithTargetAndFuncType_UsesReflectionHelperWithTarget()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test(Sample obj)
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Compute"");
        var func = mi.CreateDelegate(typeof(Func<int, string>), obj);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, obj, Function.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_GenericFuncWithTarget_UsesReflectionHelperWithTarget()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test(Sample obj)
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Compute"");
        var func = mi.CreateDelegate<Func<int, string>>(obj);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, obj, Function.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_DelegateInvokeWithArgs_MapsToAccept()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Handle"");
        var action = mi.CreateDelegate(typeof(Action<int>));
        action(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Consumer.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".accept(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDelegate_GenericCustomDelegate_UsesReflectionHelper()
    {
        var result = Convert(@"
using System;
using System.Reflection;

public delegate int Transformer(string input);

public class Sample
{
    public static void Test()
    {
        MethodInfo mi = typeof(Sample).GetMethod(""Transform"");
        var t = mi.CreateDelegate<Transformer>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ReflectionHelper.createDelegate(mi, Transformer.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
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
