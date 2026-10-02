using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

public sealed class ClosureLiftUnitTests
{
    // ── M1 invariant: never rewrite ──

    [Fact]
    public void Audit_NoLambda_FastPath_IsCleanAndUnchanged()
    {
        var src = "class C { void M() { int x = 1; } }";
        var r = Audit(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Equal(1, r.Statistics.FilesScanned);
        Assert.Equal(1, r.Statistics.FilesSkippedClean);
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void Audit_ReadOnlyCapture_NoRow_Unchanged()
    {
        var src = @"
class C {
    int M() {
        int x = 41;
        System.Func<int> f = () => x + 1;
        return f();
    }
}";
        var r = Audit(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3207");
        Assert.Equal(1, r.Statistics.FilesSkippedClean);
    }

    // ── D2 trigger reasons ──

    [Fact]
    public void Audit_ExternalWrite_ReasonAndProspectiveNames()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { System.Console.WriteLine(x); };
        x = 1;
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(ClosureLiftSeverity.Info, row.Severity);
        Assert.Equal("x", row.SymbolName);
        Assert.Equal(TriggerReason.ExternalWrite, row.Reason);
        Assert.Contains("Cs2jDisplayClass_M_0", row.Message);
        Assert.Contains("__cs2jClosure0", row.Message);
        Assert.Contains("_cs2jCap_x", row.Message);
        Assert.Equal(1, r.Statistics.CapturesByReason[TriggerReason.ExternalWrite]);
        Assert.Equal(0, r.Statistics.FilesTransformed);
    }

    [Fact]
    public void Audit_InsideLambdaWrite_Reason()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(TriggerReason.InsideLambdaWrite, row.Reason);
        Assert.Contains("shared", row.Message);
        Assert.Contains("sites=1", row.Message);
    }

    [Fact]
    public void Audit_RefOutArgument_ReasonTakesPrecedence()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { int.TryParse(""1"", out x); };
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(TriggerReason.RefOutArgument, row.Reason);
    }

    [Fact]
    public void Audit_ForeachIteration_PerIterationShapeAndStat()
    {
        var src = @"
class C {
    void M() {
        foreach (var x in new[] { 1, 2, 3 })
        {
            System.Action a = () => { System.Console.WriteLine(x); };
        }
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal(TriggerReason.ForeachIteration, row.Reason);
        Assert.Contains("foreach-per-iteration", row.Message);
        Assert.Equal(1, r.Statistics.ForeachPerIterationGroups);
    }

    [Fact]
    public void Audit_ParameterWithExternalWrite_RowForParameter()
    {
        var src = @"
class C {
    void M(int p) {
        System.Action a = () => { System.Console.WriteLine(p); };
        p = 2;
    }
}";
        var r = Audit(src);

        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal("p", row.SymbolName);
        Assert.Equal(TriggerReason.ExternalWrite, row.Reason);
        Assert.Contains("_cs2jCap_p", row.Message);
    }

    // ── grouping / naming ──

    [Fact]
    public void Audit_TwoSymbolsSameScope_OneHolderTwoFields()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        int y = 0;
        System.Action a = () => { x++; y++; };
    }
}";
        var r = Audit(src);

        var rows = r.Diagnostics.Where(d => d.Code == "CS2J3207").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, d => Assert.Contains("Cs2jDisplayClass_M_0", d.Message));
        Assert.All(rows, d => Assert.Contains("__cs2jClosure0", d.Message));
        Assert.Contains(rows, d => d.Message.Contains("_cs2jCap_x"));
        Assert.Contains(rows, d => d.Message.Contains("_cs2jCap_y"));
        Assert.Equal(2, r.Statistics.CapturesByReason[TriggerReason.InsideLambdaWrite]);
    }

    [Fact]
    public void Audit_CrossFileNaming_SameHostContinuesIndex_DifferentHostRestarts()
    {
        var pp = new ClosureLiftPreprocessor();

        var a1 = @"
class A {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
    }
}";
        var a2 = @"
class A {
    void M() {
        int y = 0;
        System.Action b = () => { y++; };
    }
}";
        var b1 = @"
class B {
    void M() {
        int z = 0;
        System.Action c = () => { z++; };
    }
}";

        var r1 = Audit(pp, a1);
        var r2 = Audit(pp, a2);
        var r3 = Audit(pp, b1);

        Assert.Contains(r1.Diagnostics, d => d.Message.Contains("Cs2jDisplayClass_M_0"));
        Assert.Contains(r2.Diagnostics, d => d.Message.Contains("Cs2jDisplayClass_M_1"));
        Assert.Contains(r3.Diagnostics, d => d.Message.Contains("Cs2jDisplayClass_M_0"));
    }

    // ── CS2J3201 / 3203 / 3204 / 3205 / 3206 diagnostics ──

    [Fact]
    public void Audit_ByRefParameter_CS2J3201_ErrorInStrict()
    {
        var src = @"
class C {
    void M(ref int p) {
        System.Action a = () => { System.Console.WriteLine(p); };
    }
}";
        var r = Audit(src);

        var d = Assert.Single(r.Diagnostics.Where(x => x.Code == "CS2J3201"));
        Assert.Equal(ClosureLiftSeverity.Error, d.Severity);
        Assert.Equal("p", d.SymbolName);
        Assert.DoesNotContain(r.Diagnostics, x => x.Code == "CS2J3207");
        Assert.Equal(1, r.Statistics.SkippedByReason["CS2J3201"]);
    }

    [Fact]
    public void Audit_RefLocal_CS2J3201_ErrorInStrict()
    {
        var src = @"
class C {
    void M() {
        int y = 0;
        ref int p = ref y;
        System.Action a = () => { var z = p; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3201" && d.Severity == ClosureLiftSeverity.Error);
    }

    [Fact]
    public void Audit_NonStrict_RefCaptureDowngradedToWarning()
    {
        var src = @"
class C {
    void M(ref int p) {
        System.Action a = () => { System.Console.WriteLine(p); };
    }
}";
        var r = Audit(src, new ClosureLiftOptions(Strict: false));

        var d = Assert.Single(r.Diagnostics.Where(x => x.Code == "CS2J3201"));
        Assert.Equal(ClosureLiftSeverity.Warning, d.Severity);
    }

    [Fact]
    public void Audit_ResidualAnonymousMethod_CS2J3203()
    {
        var src = @"
class C {
    void M() {
        System.Comparison<int> c = delegate(int a, int b) { return a - b; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3203" && d.Severity == ClosureLiftSeverity.Error);
        Assert.False(r.Changed);
    }

    [Fact]
    public void Audit_LambdaInsideLocalFunction_CS2J3204()
    {
        var src = @"
class C {
    void M() {
        void Local() {
            System.Action a = () => { System.Console.WriteLine(1); };
        }
        Local();
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3204" && d.Severity == ClosureLiftSeverity.Error);
    }

    [Fact]
    public void Audit_LambdaInFieldInitializer_CS2J3204()
    {
        var src = @"
class C {
    System.Func<int> F = () => 1;
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3204" && d.Severity == ClosureLiftSeverity.Error);
    }

    [Fact]
    public void Audit_PointerCapture_CS2J3205()
    {
        var src = @"
unsafe class C {
    void M() {
        int* p = null;
        System.Action a = () => { var z = p; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3205" && d.Severity == ClosureLiftSeverity.Error);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3207");
    }

    [Fact]
    public void Audit_ExpressionTreeMix_CS2J3206()
    {
        var src = @"
using System;
using System.Linq.Expressions;
class C {
    void M() {
        int x = 0;
        Expression<Func<int>> q = () => x;
        Action a = () => { x++; };
    }
}";
        var r = Audit(src);

        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J3206" && d.Severity == ClosureLiftSeverity.Error);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3207");
        Assert.Equal(1, r.Statistics.SkippedByReason["CS2J3206"]);
    }

    // ── site filtering / idempotence / parse errors ──

    [Fact]
    public void Audit_NameOfSite_ExcludedFromSiteCount()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Func<string> f = () => nameof(x);
        x = 1;
    }
}";
        var r = Audit(src);

        // Only the outer assignment LVS counts; nameof(x) is a string literal site.
        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Contains("sites=1", row.Message);
    }

    [Fact]
    public void Audit_RepeatedRuns_ByteIdenticalAndDeterministic()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
    }
}";
        var pp = new ClosureLiftPreprocessor();
        var r1 = Audit(pp, src);
        var r2 = Audit(pp, src);
        var r3 = Audit(new ClosureLiftPreprocessor(), src);

        Assert.False(r1.Changed);
        Assert.False(r2.Changed);
        Assert.False(r3.Changed);
        Assert.Equal(src, r1.OutputCode);
        Assert.Equal(src, r2.OutputCode);
        Assert.Equal(src, r3.OutputCode);

        var m1 = string.Join("\n", r1.Diagnostics.Select(d => d.ToString()));
        var m2 = string.Join("\n", r2.Diagnostics.Select(d => d.ToString()));
        var m3 = string.Join("\n", r3.Diagnostics.Select(d => d.ToString()));
        Assert.Equal(m1, m2);
        Assert.Equal(m1, m3);
    }

    [Fact]
    public void Audit_StandaloneModel_ParseError_ReturnsUnchangedClean()
    {
        var broken = "class C { void M() { ";
        var r = new ClosureLiftPreprocessor().Preprocess(broken);

        Assert.False(r.Changed);
        Assert.Equal(broken, r.OutputCode);
        Assert.Equal(1, r.Statistics.FilesSkippedClean);
        Assert.Empty(r.Diagnostics);
    }

    // ── Helpers (mirror IteratorLoweringUnitTests L465-514) ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-audit-test",
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

    private static ClosureLiftResult Audit(string src, ClosureLiftOptions? options = null)
        => Audit(new ClosureLiftPreprocessor(), src, options);

    private static ClosureLiftResult Audit(
        ClosureLiftPreprocessor preprocessor,
        string src,
        ClosureLiftOptions? options = null)
        // M2: Audit() means audit mode. The M1 facade always audited regardless
        // of options; the M2 stage honors AuditOnly, so default it explicitly.
        // Explicit options (e.g. Strict:false) pass through untouched.
        => preprocessor.Preprocess(
            src, ParseWithSemantics(src), options ?? new ClosureLiftOptions(AuditOnly: true));
}
