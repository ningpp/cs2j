using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the Regex test-module conversion error clusters:
/// 1. Array.IndexOf on enum arrays emits CSharpArray.indexOf (compat must provide it).
/// 2. List&lt;char&gt;.ToArray() must not emit toArray(char[]::new) — Character[] + ArrayHelper.toCharArray.
/// 3. Assembly.GetExecutingAssembly() must emit AssemblyCompat.getExecutingAssembly() (not
///    AssemblyCompat.AssemblyCompat...).
/// 4. MethodInfo?.Invoke(null, null) must disambiguate to invoke(x, (Object[]) null).
/// 5. `data?.Select(obj => { ...; return obj; })` on IEnumerable&lt;object[]&gt; must keep the
///    map lambda returning the array element (no asListView wrap), so the surrounding
///    CSharpGenericIterable.from(...) stream pipeline type-checks.
/// 6. Lambda body value null (e.g. () => obj.Name = null) must declare a typed __result
///    (String __result = null;) instead of var __result = null;.
/// 7. Xunit Record.Exception(action) must emit XunitRecord.exception(...) (csharp.xunit.XunitRecord compat).
/// 8. `ex is T` in a hoisted local function must emit Class-token isInstance with the
///    Class&lt;T&gt; parameters injected into the hoisted method signature and at call sites.
/// 9. typeof(AssemblyCompanyAttribute) must emit AssemblyCompanyAttribute.class (compat class).
/// </summary>
public class RegexTestClusterFixesTests
{
    [Fact]
    public void ArrayIndexOfOnEnumArray_EmitsCSharpArrayIndexOf()
    {
        var result = Convert(@"
using System;
using System.Globalization;
class C
{
    static int M(UnicodeCategory[] categories, char c)
    {
        return Array.IndexOf(categories, char.GetUnicodeCategory(c));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("CSharpArray.indexOf(categories,", code);
        Assert.DoesNotContain("= Array.indexOf(categories", code);
        Assert.DoesNotContain("CSharpArray.CSharpArray", code);
    }

    [Fact]
    public void ListOfCharToArray_UsesCharacterArrayAndToCharArray()
    {
        var result = Convert(@"
using System.Linq;
class C
{
    static char[] M()
    {
        return Enumerable.Range(0, 10).Select(i => (char)i).ToArray();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("toArray(char[]::new)", code);
        Assert.Contains("toArray(Character[]::new)", code);
        Assert.Contains("ArrayHelper.toCharArray(", code);
    }

    [Fact]
    public void AssemblyGetExecutingAssembly_SingleAssemblyCompatPrefix()
    {
        var result = Convert(@"
using System.Reflection;
class C
{
    static object M()
    {
        return Assembly.GetExecutingAssembly().GetTypes();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("AssemblyCompat.getExecutingAssembly()", code);
        Assert.DoesNotContain("AssemblyCompat.AssemblyCompat", code);
    }

    [Fact]
    public void MethodInfoInvokeWithNullArgs_CastsSecondArgumentToObjectArray()
    {
        var result = Convert(@"
using System.Reflection;
class C
{
    static object M(MethodInfo mi)
    {
        return mi?.Invoke(null, null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("invoke(null, (Object[]) null)", code);
        Assert.DoesNotContain("invoke(null, null)", code);
    }

    [Fact]
    public void SelectMapLambdaReturningArray_KeepsArrayElement()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
class C
{
    private static IEnumerable<object[]> M(MethodInfo mi)
    {
        var data = mi?.Invoke(null, null) as IEnumerable<object[]>;
        return data?.Select(obj =>
        {
            obj[0] = 1;
            return obj;
        });
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains(".map(obj -> {", code);
        Assert.Contains("return obj;", code);
        Assert.DoesNotContain("asListView(obj)", code);
        Assert.Contains("CSharpGenericIterable.from(", code);
    }

    [Fact]
    public void LambdaNullAssignmentValue_UsesTypedResultVariable()
    {
        var result = Convert(@"
using System;
class C
{
    public string Name { get; set; }
    static void M(C c)
    {
        Func<string> f = () => c.Name = null;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("var __result = null;", code);
        Assert.Contains("String __result = null;", code);
    }

    [Fact]
    public void RecordException_EmitsRecordExceptionCall()
    {
        var result = Convert(@"
using Xunit;
class C
{
    static void M()
    {
        Exception e = Record.Exception(() => throw new Exception());
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("XunitRecord.exception(", code);
    }

    [Fact]
    public void LocalFunctionIsGenericTypeCheck_InjectsClassTokens()
    {
        var result = Convert(@"
using System;
class C
{
    static void M()
    {
        AssertThrows<ArgumentException, OutOfMemoryException>(() => throw new ArgumentException());
        static void AssertThrows<TException1, TException2>(Action action)
        {
            Exception e;
            try { action(); e = null; }
            catch (Exception ex) { e = ex; }
            if (e is TException1 || e is TException2) { }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("private static <TException1, TException2> void assertThrows(", code);
        Assert.Contains("Class<TException1> _cs2j_TException1", code);
        Assert.Contains("Class<TException2> _cs2j_TException2", code);
        Assert.Contains("_cs2j_TException1.isInstance(e)", code);
        Assert.Contains("_cs2j_TException2.isInstance(e)", code);
        Assert.Contains("assertThrows(ArgumentException.class, OutOfMemoryException.class,", code);
    }

    [Fact]
    public void LocalFunctionIsGenericTypeCheck_InjectsClassTokens_WhenSymbolsDegraded()
    {
        var result = Convert(@"
using System;
class C
{
    static void M()
    {
        AssertThrows<ArgumentException, OutOfMemoryException>(() => UndefinedType.Foo(new object[] { null }));
        static void AssertThrows<TException1, TException2>(Action action)
        {
            Exception e = null;
            try { action(); }
            catch (Exception ex) { e = ex; }
            if (e is TException1 || e is TException2) { }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Class<TException1> _cs2j_TException1", code);
        Assert.Contains("Class<TException2> _cs2j_TException2", code);
        Assert.Contains("assertThrows(ArgumentException.class, OutOfMemoryException.class,", code);
    }

    [Fact]
    public void AssemblyCompanyAttributeTypeOf_EmitsClassLiteral()
    {
        var result = Convert(@"
using System.Reflection;
class C
{
    static void M()
    {
        var cab = new CustomAttributeBuilder(
            typeof(AssemblyCompanyAttribute).GetConstructor(new[] { typeof(string) }),
            new[] { ""TestCompany"" });
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("AssemblyCompanyAttribute.class", code);
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
