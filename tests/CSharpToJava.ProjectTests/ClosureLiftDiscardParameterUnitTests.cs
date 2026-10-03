using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// D12 (C45 M3): lambda parameter normalization on both lift paths —
/// ① discard slots (unreferenced single `_` and C# 9 duplicated `_`) get
/// synthesized `_cs2jArg_&lt;i&gt;` names; ② implicit/unnamed typed slots get the
/// delegate-derived explicit type; ④ parameter attribute lists migrate onto
/// the named-method parameter. A referenced single `_` is a real C# 7/8
/// parameter name and is kept. Shares the process-serial
/// <see cref="ClosureLiftTestCollection"/> with the other in-process
/// closure-lift classes (planner test hook).
/// </summary>
[Collection(ClosureLiftTestCollection.Name)]
public sealed class ClosureLiftDiscardParameterUnitTests
{
    [Fact]
    public void M3_DiscardParameter_SingleUnderscore_StaticPath_SynthesizedName()
    {
        var src = @"
class C {
    void M() {
        System.Func<int, int> f = _ => 42;
        System.Console.WriteLine(f(1));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("new global::System.Func<int, int>(Cs2jLambda_M_0)", r.OutputCode);
        Assert.Contains(
            "private static int Cs2jLambda_M_0(int _cs2jArg_0) => 42;",
            r.OutputCode);
        Assert.Equal(1, r.Statistics.DiscardParametersNormalized);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_DiscardParameters_DoubleUnderscore_StaticPath_BothSynthesized()
    {
        var src = @"
class C {
    void M() {
        System.Action<int, int> f = (_, _) => System.Console.WriteLine(1);
        f(1, 2);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains(
            "private static void Cs2jLambda_M_0(int _cs2jArg_0, int _cs2jArg_1) => System.Console.WriteLine(1);",
            r.OutputCode);
        Assert.Equal(2, r.Statistics.DiscardParametersNormalized);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);

        // Second pass is a no-op: no lambda nodes, no renaming work.
        var second = Transform(r.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(0, second.Statistics.DiscardParametersNormalized);
    }

    [Fact]
    public void M3_DiscardParameter_MixedWithNamed_CapturingInstancePath()
    {
        var src = @"
class C {
    void M() {
        int x = 10;
        System.Func<int, int, int> f = (_, p) => x + p;
        System.Console.WriteLine(f(1, 2));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains(
            "new global::System.Func<int, int, int>(__cs2jClosure0.Lambda_0)",
            r.OutputCode);
        Assert.Contains(
            "internal int Lambda_0(int _cs2jArg_0, int p) => _cs2jCap_x + p;",
            r.OutputCode);
        Assert.Equal(1, r.Statistics.DiscardParametersNormalized);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_DiscardParameter_AttributeMigrates_StaticPath()
    {
        var src = @"
class MarkerAttribute : System.Attribute { }
class C {
    void M() {
        System.Func<int, int> f = ([Marker] int _) => 42;
        System.Console.WriteLine(f(1));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(1, r.Statistics.DiscardParametersNormalized);
        var parameter = AssertLiftedMethodParameter(r.OutputCode, "Cs2jLambda_M_0");
        Assert.Equal("_cs2jArg_0", parameter.Identifier.ValueText);
        Assert.Single(parameter.AttributeLists);
        var attribute = Assert.Single(parameter.AttributeLists[0].Attributes);
        Assert.Equal("Marker", attribute.Name.ToString());
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_DiscardParameter_AttributeMigrates_CapturingInstancePath()
    {
        var src = @"
class MarkerAttribute : System.Attribute { }
class C {
    void M() {
        int x = 9;
        System.Func<int, int> f = ([Marker] int _) => x;
        System.Console.WriteLine(f(1));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(1, r.Statistics.DiscardParametersNormalized);
        var parameter = AssertLiftedMethodParameter(r.OutputCode, "Lambda_0");
        Assert.Equal("_cs2jArg_0", parameter.Identifier.ValueText);
        var attribute = Assert.Single(Assert.Single(parameter.AttributeLists).Attributes);
        Assert.Equal("Marker", attribute.Name.ToString());
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_UnderscoreParameterReferenced_KeptAsRealName()
    {
        // A single `_` the own body reads is a legal C# 7/8 parameter name;
        // it is NOT a discard and must survive verbatim with an explicit type.
        var src = @"
class C {
    void M() {
        System.Func<int, int> f = _ => _ + 1;
        System.Console.WriteLine(f(1));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains(
            "private static int Cs2jLambda_M_0(int _) => _ + 1;",
            r.OutputCode);
        Assert.Equal(0, r.Statistics.DiscardParametersNormalized);
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_ImplicitlyTypedParameters_AllGetDelegateExplicitTypes()
    {
        // D12②: implicitly typed slots (E05-style `p0`/`p1` names included)
        // gain the delegate InvokeMethod types; names are retained.
        var src = @"
class C {
    void M() {
        System.Func<int, int, int> f = (p0, p1) => p0 + p1;
        System.Console.WriteLine(f(1, 2));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains(
            "private static int Cs2jLambda_M_0(int p0, int p1) => p0 + p1;",
            r.OutputCode);
        Assert.Equal(0, r.Statistics.DiscardParametersNormalized);
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    private static ParameterSyntax AssertLiftedMethodParameter(string code, string methodName)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        var method = Assert.Single(root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == methodName));
        return Assert.Single(method.ParameterList.Parameters);
    }

    private static IReadOnlyList<LambdaExpressionSyntax> LambdaNodes(string code)
        => CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot()
            .DescendantNodes()
            .OfType<LambdaExpressionSyntax>()
            .ToList();

    private static void AssertIdempotent(string output)
    {
        var r2 = new ClosureLiftPreprocessor().Preprocess(
            output, ParseWithSemantics(output), new ClosureLiftOptions());
        Assert.False(r2.Changed);
        Assert.Equal(output, r2.OutputCode);
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-m3-discard-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static ClosureLiftResult Transform(string src, ClosureLiftOptions? options = null)
        => new ClosureLiftPreprocessor().Preprocess(
            src, ParseWithSemantics(src), options ?? new ClosureLiftOptions());

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-m3-discard-transformed",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(
            errors.Count == 0,
            "Transformed output failed to compile:\n" + string.Join("\n", errors.Select(e => e.GetMessage())));
    }
}
