using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the cross-project addToMap<TS,T,TC> pattern where TC has a
/// new() constraint and is used as `new TC()` in the method body.
/// The converter adds a trailing Class<?> tCClass parameter to the
/// method signature AND appends a Class literal at call sites via
/// GetRuntimeClassArguments. However, GetClassTypeTokensForCall was
/// ALSO emitting a leading Class token for TC because the cross-project
/// GetRequiredTypeParameters returned empty (no syntax tree access),
/// so runtimeArrayTypeParameterNames didn't include TC. This caused
/// call sites to emit both a leading and trailing Class literal,
/// producing an arity mismatch.
///
/// This test verifies the method definition generates exactly one
/// Class<?> parameter (trailing), not a leading Class<T> + trailing Class<?>.
/// </summary>
public class AddToMapClassTokenDuplicationTests
{
    [Fact]
    public void GenericMethodWithNewTConstraint_HasExactlyOneClassParameter()
    {
        var result = Convert("""
using System.Collections.Generic;

class CollectionUtilities {
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new() {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The method signature should have exactly one Class<?> parameter (trailing),
        // not both a leading Class<T> _cs2j_TC and a trailing Class<?> tCClass.
        Assert.Contains("Class<?>", code, StringComparison.Ordinal);

        // Count Class<?> parameters in the method signature
        var classParamCount = System.Text.RegularExpressions.Regex
            .Matches(code, @"Class<\?>").Count;
        Assert.True(classParamCount == 1,
            $"Expected exactly 1 Class<?> parameter, got {classParamCount}.\nFull code:\n{code}");
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
