using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that catch clauses are reordered so that project-specific exceptions
/// (which extend RuntimeException in Java) precede the System.Exception catch-all
/// that maps to RuntimeException.
/// </summary>
public class ExceptionCatchOrderTests
{
    [Fact]
    public void CustomExceptionCatch_PrecedesRuntimeExceptionCatch()
    {
        var result = Convert(@"
using System;

public class MyException : Exception { }

public class Test
{
    public void M()
    {
        try { }
        catch (MyException e) { }
        catch (Exception e) { }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        int customIndex = result.GeneratedCode.IndexOf("catch (MyException", StringComparison.Ordinal);
        int runtimeIndex = result.GeneratedCode.IndexOf("catch (RuntimeException", StringComparison.Ordinal);

        Assert.True(customIndex >= 0, "Expected catch (MyException) in generated code.\n" + result.GeneratedCode);
        Assert.True(runtimeIndex >= 0, "Expected catch (RuntimeException) in generated code.\n" + result.GeneratedCode);
        Assert.True(customIndex < runtimeIndex,
            "Project-specific exception catch must precede RuntimeException catch.\n" + result.GeneratedCode);
    }

    [Fact]
    public void TypeParameterCatch_UsesConstraintTypeWithRuntimeGuard()
    {
        var result = Convert(@"
using System;

public class Test
{
    public static T Throws<T>(Action testCode) where T : ArgumentException
    {
        try
        {
            testCode();
        }
        catch (T ex)
        {
            return ex;
        }
        throw new ArgumentException(""oops"");
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Java cannot catch a type variable: catch the constraint type + runtime guard.
        Assert.DoesNotContain("catch (T ex)", code);
        Assert.Contains("catch (ArgumentException _cs2j_exCatch)", code);
        Assert.Contains("_cs2j_T.isInstance(_cs2j_exCatch)", code);
        Assert.Contains("T ex = (T) _cs2j_exCatch;", code);
        Assert.Contains("return ex;", code);
        Assert.Contains("Class<T> _cs2j_T", code);
    }

    [Fact]
    public void ArgumentOutOfRangeException_Pattern_KeepsParamNameApi()
    {
        // C# 'ex is ArgumentOutOfRangeException aore && aore.ParamName == paramName'.
        // ArgumentOutOfRangeException previously mapped to java.lang.IllegalArgumentException,
        // which has no getParamName() — javac fails. It must map to the compat class that
        // preserves the .NET hierarchy (ArgumentOutOfRangeException -> ArgumentException)
        // so ParamName keeps working.
        var result = Convert("""
using System;
class C {
    static string Match(Exception ex, string paramName) {
        if (ex is ArgumentOutOfRangeException aore && aore.ParamName == paramName)
            return aore.ParamName;
        return null;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("instanceof ArgumentOutOfRangeException", code);
        Assert.DoesNotContain("instanceof IllegalArgumentException", code);
        Assert.Contains("aore.getParamName()", code);
    }

    [Fact]
    public void IsTypeParameterPattern_UsesRuntimeClassToken()
    {
        // C# 'ex is T1' with a method type parameter: Java forbids instanceof on type
        // variables. Must emit _cs2j_T1.isInstance(ex) using the Class<T> token.
        var result = Convert("""
using System;
class C
{
    static bool Any<T1, T2>(Exception ex)
        where T1 : Exception
        where T2 : Exception
    {
        return ex is T1 || ex is T2;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("_cs2j_T1.isInstance(ex)", code);
        Assert.Contains("_cs2j_T2.isInstance(ex)", code);
        Assert.DoesNotContain("instanceof T1", code);
        Assert.DoesNotContain("instanceof T2", code);
        Assert.Contains("Class<T1> _cs2j_T1", code);
        Assert.Contains("Class<T2> _cs2j_T2", code);
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
