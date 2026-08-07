using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that TryGetValue lowering casts the map get() result when the out
/// variable is a Java primitive. Reproduces Regex.java:[275,96] "不兼容的类型:
/// Object 无法转换为 int" — C# 'caps.TryGetValue(i, out i)' (Hashtable extension,
/// int out reused) lowered to 'i = caps.get(i)', but get() returns Object and
/// cannot be assigned to an int without an unboxing cast.
/// </summary>
public class TryGetValuePrimitiveOutCastTests
{
    [Fact]
    public void TryGetValue_IntOut_CastsGetResult()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections;
public static class HashtableExtensions
{
    public static bool TryGetValue<T>(this Hashtable table, object key, out T value)
    {
        if (table.ContainsKey(key)) { value = (T)table[key]; return true; }
        value = default; return false;
    }
}
public class C
{
    public string M(Hashtable caps, int i)
    {
        return caps.TryGetValue(i, out i) ? ""y"" : ""n"";
    }
}",
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The raw unboxed assignment is invalid Java (Object -> int); a cast must be present.
        Assert.DoesNotContain("(i = caps.get(i))", code);
        Assert.Contains("(Integer) caps.get(i)", code);
    }
}
