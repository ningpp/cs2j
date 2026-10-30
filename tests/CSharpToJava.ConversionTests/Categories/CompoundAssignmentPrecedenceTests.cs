using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Regression for the C# compound-assignment grouping contract:
/// <c>x op= rhs</c> means <c>x = x op (rhs)</c>. Targets that need the Java
/// get/set rewrite (ByRef&lt;T&gt;.Value holders, properties, dictionary
/// indexers) are expanded to <c>set(get() op rhs)</c>; a non-atomic RHS must
/// keep its own parentheses or Java re-associates the expression.
///
/// Real chain (SDK Java tests dotnet.system.PrivateUri.Tests.UriTests
/// <c>testCtor_BackwardSlashInPath</c> [2]/[4]): System.Private.Uri's
/// UnescapeOnly does <c>end.Value -= pchIndex - pnew;</c>. It was emitted as
/// <c>end.set(end.getValue() - pchIndex - pnew)</c>, i.e.
/// <c>(end - pchIndex) - pnew</c> instead of <c>end - (pchIndex - pnew)</c>.
/// With end=20, pchIndex=20, pnew=18 the path length became -18 instead of 18,
/// so Compress computed <c>(ushort)(-18) - 1 = 65517</c> and indexed a
/// 248-character array -> ArrayIndexOutOfBoundsException.
/// </summary>
public class CompoundAssignmentPrecedenceTests : ConversionTestBase
{
    [Fact]
    public void ByRefValue_SubtractBinaryRhs_KeepsCSharpGrouping()
    {
        var result = Convert(@"
namespace Cs2jByRef { public abstract class ByRef<T> { public abstract T Value { get; set; } } }
public class Calc
{
    public static void Shrink(Cs2jByRef.ByRef<int> end, int pchIndex, int pnew)
    {
        end.Value -= pchIndex - pnew;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "end.set(end.getValue() - (pchIndex - pnew))",
            "compound holder write must evaluate the whole C# RHS as the subtrahend");
        AssertJavaDoesNotContain(result, "end.getValue() - pchIndex - pnew",
            "unparenthesized RHS re-associates (end - pchIndex) - pnew");
    }

    [Fact]
    public void Property_SubtractBinaryRhs_KeepsCSharpGrouping()
    {
        var result = Convert(@"
public class Calc
{
    public int Count { get; set; }
    public void Shrink(int pchIndex, int pnew)
    {
        Count -= pchIndex - pnew;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "setCount(getCount() - (pchIndex - pnew))",
            "compound property write must evaluate the whole C# RHS as the subtrahend");
        AssertJavaDoesNotContain(result, "getCount() - pchIndex - pnew",
            "unparenthesized RHS re-associates (Count - pchIndex) - pnew");
    }

    [Fact]
    public void Indexer_DivideByBinaryRhs_KeepsCSharpGrouping()
    {
        var result = Convert(@"
using System.Collections.Generic;
public class Calc
{
    public static void Scale(Dictionary<string, int> d, string key, int a, int b)
    {
        d[key] /= a + b;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "d.put(key, d.get(key) / (a + b))",
            "compound indexer write must evaluate the whole C# RHS as the divisor");
        AssertJavaDoesNotContain(result, "d.get(key) / a + b",
            "unparenthesized RHS re-associates (d[key] / a) + b");
    }

    [Fact]
    public void CompoundWrites_AtomicRhs_KeepUnparenthesizedShape()
    {
        // The precedence guard must not wrap leaf-like operands: existing emitted
        // shapes such as `v.set(v.getValue() + delta)` stay untouched.
        var result = Convert(@"
namespace Cs2jByRef { public abstract class ByRef<T> { public abstract T Value { get; set; } } }
public class Calc
{
    public int Count { get; set; }
    public static void Bump(Cs2jByRef.ByRef<int> v, int delta)
    {
        v.Value += delta;
    }
    public void Grow()
    {
        Count += 1;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "v.set(v.getValue() + delta)",
            "atomic RHS keeps the existing unparenthesized shape");
        AssertJavaContains(result, "setCount(getCount() + 1)",
            "literal RHS keeps the existing unparenthesized shape");
        AssertJavaDoesNotContain(result, "v.set(v.getValue() + (delta))",
            "leaf operands must not gain redundant parentheses");
    }
}
