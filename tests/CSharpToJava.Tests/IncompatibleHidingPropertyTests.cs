using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A C# `new` property whose type cannot override the base getter in Java
/// (unrelated return types) must be emitted under a disambiguated name
/// (getX_newSlot) annotated with @CompatPropertyName, and call sites on the
/// derived type must use the renamed getter while base-typed call sites keep
/// the base getter.
/// </summary>
public class IncompatibleHidingPropertyTests
{
    [Fact]
    public void IncompatibleHidingGetter_IsRenamedAndAnnotated()
    {
        var result = Convert("""
class Base {
    public int MyProp { get; set; }
}
class Derived : Base {
    public new decimal MyProp { get; set; }
}
class C {
    decimal M(Derived d) => d.MyProp;
    int N(Base b) => b.MyProp;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("@CompatPropertyName(\"MyProp\")", code, StringComparison.Ordinal);
        Assert.Contains("public Decimal getMyProp_newSlot()", code, StringComparison.Ordinal);
        Assert.Contains("d.getMyProp_newSlot()", code, StringComparison.Ordinal);
        Assert.Contains("b.getMyProp()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Decimal getMyProp()", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "IncompatibleHidingPropertyTests.cs",
            Options = new ConversionOptions(),
        });
    }
}
