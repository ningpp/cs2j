using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for RegexCompiler.java compile errors:
/// 1. Conditional access on an uppercase-named FIELD (?._bm.CaseInsensitive) must stay
///    a direct field access, not synthesize a nonexistent getCaseInsensitive() getter.
/// 2. C# user-defined implicit conversion operators at call-site arguments
///    (implicit operator LocalBuilder(RentedLocalBuilder)) must be inserted as the
///    generated to&lt;Target&gt;() helper.
/// 3. System.Reflection.Emit.ILGenerator methods must keep their PascalCase Java
///    names (Emit/DefineLabel/MarkLabel/DeclareLocal), not be camelCased.
/// </summary>
public class RegexCompilerReflectionCompatTests
{
    private readonly ITestOutputHelper _out;

    public RegexCompilerReflectionCompatTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ConditionalAccessOnUppercaseField_StaysDirectFieldAccess()
    {
        var result = Convert("""
class BoyerMoore {
    public boolean CaseInsensitive;
}
class C {
    static bool NeedsCulture(BoyerMoore bm) {
        return bm?.CaseInsensitive == true;
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("bm.CaseInsensitive", code);
        Assert.DoesNotContain("getCaseInsensitive()", code);
    }

    [Fact]
    public void ImplicitConversionOperatorArgument_UsesGeneratedHelper()
    {
        var result = Convert("""
using System.Collections.Generic;
class LocalBuilder { }
class RentedLocalBuilder {
    private LocalBuilder _local;
    public RentedLocalBuilder(LocalBuilder local) { _local = local; }
    public static implicit operator LocalBuilder(RentedLocalBuilder r) => r._local;
}
class Emitter {
    private LocalBuilder _ilg;
    void Emit(LocalBuilder local) { }
    void Use(RentedLocalBuilder r) {
        Emit(r);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("toLocalBuilder(", code);
        Assert.DoesNotContain("emit(r)", code);
    }

    [Fact]
    public void ILGeneratorMethods_KeepPascalCaseNames()
    {
        var result = Convert("""
using System.Reflection.Emit;
class Compiler {
    private ILGenerator _ilg;
    void EmitIt() {
        Label l = _ilg.DefineLabel();
        _ilg.MarkLabel(l);
        _ilg.Emit(OpCodes.Ldstr, "x");
        LocalBuilder lb = _ilg.DeclareLocal(typeof(int));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("_ilg.DefineLabel()", code);
        Assert.Contains("_ilg.MarkLabel(", code);
        Assert.Contains("_ilg.Emit(OpCodes.Ldstr", code);
        Assert.Contains("_ilg.DeclareLocal(", code);
        Assert.DoesNotContain("_ilg.defineLabel()", code);
        Assert.DoesNotContain("_ilg.emit(OpCodes.Ldstr", code);
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
