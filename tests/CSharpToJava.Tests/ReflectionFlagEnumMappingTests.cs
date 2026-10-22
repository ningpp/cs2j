using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that reflection flag enums (MethodAttributes, CallingConventions,
/// TypeAttributes) map to the compat int-constants classes so bitwise usage
/// compiles. Reproduces RegexAssemblyCompiler: javac "无法取消引用int" when the
/// generated code calls TypeAttributes.Class.getValue() on compat int constants.
/// </summary>
public class ReflectionFlagEnumMappingTests
{
    [Fact]
    public void ReflectionFlagEnums_MapToCompatIntConstants()
    {
        var result = Convert(@"
class Emit
{
    const System.Reflection.MethodAttributes Attribs =
        System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static;
    const System.Reflection.CallingConventions Conventions = System.Reflection.CallingConventions.Standard;

    int TypeAttrs(bool isPublic)
    {
        System.Reflection.TypeAttributes attrs = System.Reflection.TypeAttributes.Class
            | (isPublic ? System.Reflection.TypeAttributes.Public : System.Reflection.TypeAttributes.NotPublic);
        if (isPublic)
        {
            attrs |= System.Reflection.TypeAttributes.Sealed;
        }
        return (int)attrs;
    }

    System.Reflection.MethodAttributes MethodFlags()
    {
        return System.Reflection.MethodAttributes.Family | System.Reflection.MethodAttributes.Virtual;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("System.Reflection.MethodAttributes", code);
        Assert.DoesNotContain("System.Reflection.CallingConventions", code);
        Assert.DoesNotContain("System.Reflection.TypeAttributes", code);
        Assert.DoesNotContain(".getValue()", code);
        Assert.DoesNotContain(".fromValue(", code);
        Assert.Contains("import io.github.ningpp.compat.TypeAttributes", code);
        // Const flag-enum fields are folded to literals; values must be correct
        // (Public=6 | Static=16 = 22; Standard = 1).
        Assert.Contains("Attribs = 22", code);
        Assert.Contains("Conventions = 1", code);
        // Non-const flag enums must support bitwise composition → int constants style.
        Assert.Contains("TypeAttributes.Class | ((isPublic ? TypeAttributes.Public : TypeAttributes.NotPublic))", code);
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
