using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// D13 (C45 M3): self/mutually recursive delegate lambdas ride the field
/// lowering (<c>fact</c> → <c>_cs2jCap_fact</c>) only when every delegate of
/// the recursion group is provably stored before the group can be entered;
/// unprovable groups trip CS2J4508 and the file stays verbatim. Shares the
/// process-serial <see cref="ClosureLiftTestCollection"/> with the other
/// in-process closure-lift classes.
/// </summary>
[Collection(ClosureLiftTestCollection.Name)]
public sealed class ClosureLiftRecursiveLambdaUnitTests
{
    [Fact]
    public void M3_RecursiveLambda_Factorial_FieldBackedAndCounted()
    {
        var src = @"
class C {
    public int Fact(int n) {
        System.Func<int, int> fact = null;
        fact = x => x <= 1 ? 1 : x * fact(x - 1);
        return fact(n);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains(
            "internal int Lambda_0(int x) => x <= 1 ? 1 : x * _cs2jCap_fact(x - 1);",
            r.OutputCode);
        Assert.Equal(1, r.Statistics.RecursiveLambdas);
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_RecursiveLambda_Fibonacci_TwoSelfCallsFieldBacked()
    {
        var src = @"
class C {
    public int Fib(int n) {
        System.Func<int, int> fib = null;
        fib = x => x <= 1 ? x : fib(x - 1) + fib(x - 2);
        return fib(n);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains(
            "internal int Lambda_0(int x) => x <= 1 ? x : _cs2jCap_fib(x - 1) + _cs2jCap_fib(x - 2);",
            r.OutputCode);
        Assert.Equal(1, r.Statistics.RecursiveLambdas);
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_RecursiveLambda_RecursiveAction_FieldBacked()
    {
        var src = @"
class C {
    public void Countdown() {
        System.Action f = null;
        int i = 3;
        f = () => { if (i > 0) { i--; f(); } };
        f();
        System.Console.WriteLine(i);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("_cs2jCap_f();", r.OutputCode);
        Assert.Equal(1, r.Statistics.RecursiveLambdas);
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_MutualRecursion_AllSitesAssignedBeforeCall_Admitted()
    {
        var src = @"
class C {
    public void Run() {
        System.Action a = null;
        System.Action b = null;
        a = () => b();
        b = () => a();
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("internal void Lambda_0() => _cs2jCap_b();", r.OutputCode);
        Assert.Contains("internal void Lambda_1() => _cs2jCap_a();", r.OutputCode);
        Assert.Equal(2, r.Statistics.RecursiveLambdas);
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_MutualRecursion_CallBeforeAllAssigned_CS2J4508_FileVerbatim()
    {
        var src = @"
class C {
    public void Run() {
        System.Action a = null;
        System.Action b = null;
        a = () => b();
        a();
        b = () => a();
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J4508" && d.Severity == ClosureLiftSeverity.Error);
        Assert.Equal(0, r.Statistics.RecursiveLambdas);
    }

    [Fact]
    public void M3_RecursiveLambda_DeclarationInitializerShape_Admitted()
    {
        // C# definite-assignment shape `Func f = x => …f…`: the single storage
        // is the declaration statement itself; the read-only capture path
        // hosts the cell and ordering is provable (design §7.1-9).
        var src = @"
class C {
    public int Fact(int n) {
        System.Func<int, int> fact = x => x <= 1 ? 1 : x * fact(x - 1);
        return fact(n);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("_cs2jCap_fact(x - 1)", r.OutputCode);
        Assert.Equal(1, r.Statistics.RecursiveLambdas);
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_RecursiveLambda_StorageNestedUnderControlFlow_CS2J4508_FileVerbatim()
    {
        // The single storage sits under an if-statement: whether the delegate
        // cell exists before the call depends on runtime control flow, so v1
        // refuses to prove ordering.
        var src = @"
class C {
    public void Run(bool cond) {
        System.Action a = null;
        if (cond) {
            a = () => a();
        }
        a();
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J4508"));
        Assert.Equal(ClosureLiftSeverity.Error, row.Severity);
        Assert.Equal(0, r.Statistics.RecursiveLambdas);
    }

    [Fact]
    public void M3_MutualRecursion_Unprovable_NonStrict_DowngradedToWarning()
    {
        var src = @"
class C {
    public void Run() {
        System.Action a = null;
        System.Action b = null;
        a = () => b();
        a();
        b = () => a();
    }
}";
        var r = Transform(src, new ClosureLiftOptions(Strict: false));

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J4508"));
        Assert.Equal(ClosureLiftSeverity.Warning, row.Severity);
        Assert.Equal(0, r.Statistics.RecursiveLambdas);
    }

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
            "closure-m3-recursion-test",
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
            "closure-m3-recursion-transformed",
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
