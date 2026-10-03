using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

[Collection(ClosureLiftTestCollection.Name)]
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

    // ── M2 transform tests (design §7.1) ──

    [Fact]
    public void Transform_ReadOnlyCapture_NewDisplayClass_FieldSnapshot_ExternalLocalKept()
    {
        var src = @"
class C {
    int M() {
        int x = 41;
        System.Func<int> f = () => x + 1;
        return f();
    }
}";
        var r = Transform(src);

        // D5: a zero-write-site capture gets a fresh display class; the lambda
        // body reads the snapshot field while external sites keep the local.
        Assert.True(r.Changed);
        Assert.Equal(1, r.Statistics.DisplayClassesGenerated);
        Assert.Equal(1, r.Statistics.HolderInstancesGenerated);
        Assert.Equal(0, r.Statistics.CapturesLifted); // read-only is not a C32 mutable lift
        Assert.Contains("int x = 41;", r.OutputCode);
        Assert.Contains("var __cs2jClosure0 = new Cs2jDisplayClass_M_0();", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_x = x;", r.OutputCode);
        Assert.Contains("public int _cs2jCap_x;", r.OutputCode);
        Assert.Contains("System.Func<int> f = new global::System.Func<int>(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("internal int Lambda_0() => _cs2jCap_x + 1;", r.OutputCode);
        // The single surviving bare x is the snapshot RHS (external read kept).
        Assert.Single(BareIdentifiers(r.OutputCode, "x"));
        Assert.Empty(LambdaNodes(r.OutputCode));
        Assert.Equal(1, r.Statistics.FilesTransformed);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_ExternalWrite_AllSitesFieldBacked_NoBareName()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { System.Console.WriteLine(x); };
        x = 1;
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(1, r.Statistics.HolderClassesGenerated);
        Assert.Equal(1, r.Statistics.HolderInstancesGenerated);
        Assert.Equal(1, r.Statistics.CapturesLifted);
        Assert.Equal(1, r.Statistics.CapturesByReason[TriggerReason.ExternalWrite]);

        Assert.Contains("private sealed class Cs2jDisplayClass_M_0", r.OutputCode);
        Assert.Contains("public int _cs2jCap_x;", r.OutputCode);
        Assert.Contains("var __cs2jClosure0 = new Cs2jDisplayClass_M_0();", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_x = 0;", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_x = 1;", r.OutputCode);
        Assert.DoesNotContain("int x =", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "x"));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_InsideLambdaWrite_ExternalRead_OneHolderOneField()
    {
        var src = @"
class C {
    void M() {
        int count = 0;
        System.Action inc = () => count++;
        inc();
        System.Console.WriteLine(count);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("public int _cs2jCap_count;", r.OutputCode);
        // C45 M2: the lambda node is gone — its body is the class's Lambda_0
        // instance method (field accesses implicit-this) and the site is an
        // explicit delegate creation (D8). External sites stay C32-shaped (D2).
        Assert.Contains("System.Action inc = new global::System.Action(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("internal void Lambda_0() => _cs2jCap_count++;", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        Assert.Contains("System.Console.WriteLine(__cs2jClosure0._cs2jCap_count)", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "count"));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_InternalAndExternalWrites_SingleHolder()
    {
        var src = @"
class C {
    void M() {
        int count = 0;
        System.Action inc = () => count += 2;
        count = 5;
        inc();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Single(HolderClassNames(r.OutputCode).Where(n => n == "Cs2jDisplayClass_M_0"));
        // External write keeps the C32 shape verbatim (D2 red line)…
        Assert.Contains("__cs2jClosure0._cs2jCap_count = 5;", r.OutputCode);
        // …while the in-lambda write now lives in the migrated instance method.
        Assert.Contains("System.Action inc = new global::System.Action(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("internal void Lambda_0() => _cs2jCap_count += 2;", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_MultipleLambdas_ShareOneInstanceAndField()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action reader = () => System.Console.WriteLine(x);
        System.Action writer = () => { x = 9; };
        reader(); writer();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Equal(1, r.Statistics.HolderInstancesGenerated);
        // M2 (D3): disjoint capture-set lambdas still share one class/instance;
        // both nodes migrate to Lambda_0/Lambda_1 and their sites become
        // explicit delegate constructions over the SAME receiver (D8/D9).
        Assert.Contains("System.Action reader = new global::System.Action(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("System.Action writer = new global::System.Action(__cs2jClosure0.Lambda_1);", r.OutputCode);
        Assert.Contains("internal void Lambda_0() => System.Console.WriteLine(_cs2jCap_x);", r.OutputCode);
        Assert.Contains("internal void Lambda_1()", r.OutputCode);
        Assert.Contains("_cs2jCap_x = 9;", r.OutputCode);
        Assert.Equal(2, r.Statistics.LambdaMethodsLifted - r.Statistics.StaticLambdaMethods);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_TwoSymbolsSameScope_OneHolderOrderedFields_Deterministic()
    {
        var src = @"
class C {
    void M() {
        int a = 1, b = 2;
        System.Action t = () => { a++; b += a; };
        t();
    }
}";
        var r1 = Transform(src);
        var r2 = Transform(src);

        Assert.True(r1.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
        Assert.Single(HolderClassNames(r1.OutputCode));
        Assert.Equal(1, r1.Statistics.HolderInstancesGenerated);
        Assert.Equal(2, r1.Statistics.CapturesLifted);

        var holderIndex = r1.OutputCode.IndexOf("class Cs2jDisplayClass_M_0", StringComparison.Ordinal);
        var fieldA = r1.OutputCode.IndexOf("public int _cs2jCap_a;", StringComparison.Ordinal);
        var fieldB = r1.OutputCode.IndexOf("public int _cs2jCap_b;", StringComparison.Ordinal);
        Assert.True(holderIndex >= 0 && fieldA > holderIndex && fieldB > fieldA);

        // M2: the cross-reference lives in the migrated instance method, where
        // holder field accesses collapse to implicit-this (D2 field order kept).
        Assert.Contains("System.Action t = new global::System.Action(__cs2jClosure0.Lambda_0);", r1.OutputCode);
        Assert.Contains("_cs2jCap_b += _cs2jCap_a", r1.OutputCode);
        Assert.DoesNotContain("__cs2jClosure0._cs2jCap_b += __cs2jClosure0._cs2jCap_a", r1.OutputCode);
        Assert.Empty(BareIdentifiers(r1.OutputCode, "a"));
        Assert.Empty(BareIdentifiers(r1.OutputCode, "b"));
        Assert.Empty(LambdaNodes(r1.OutputCode));
        AssertOutputCompiles(r1.OutputCode);
    }

    [Fact]
    public void Transform_ParameterCapture_BodyFirstSnapshot_SignatureUnchanged()
    {
        var src = @"
class C {
    void Run(int seed, System.Action trigger) {
        seed++;
        System.Action a = () => seed += 2;
        a(); trigger();
        System.Console.WriteLine(seed);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("void Run(int seed, System.Action trigger)", r.OutputCode);
        var bodyIndex = r.OutputCode.IndexOf("void Run", StringComparison.Ordinal);
        var instanceIndex = r.OutputCode.IndexOf(
            "var __cs2jClosure0 = new Cs2jDisplayClass_Run_0();", StringComparison.Ordinal);
        var snapshotIndex = r.OutputCode.IndexOf(
            "__cs2jClosure0._cs2jCap_seed = seed;", StringComparison.Ordinal);
        var incrementIndex = r.OutputCode.IndexOf(
            "__cs2jClosure0._cs2jCap_seed++", StringComparison.Ordinal);
        Assert.True(instanceIndex > bodyIndex);
        Assert.True(snapshotIndex > instanceIndex && incrementIndex > snapshotIndex);

        // Exactly one bare 'seed': the one-time snapshot RHS (parameter token
        // in the signature is a parameter syntax, not an IdentifierName).
        Assert.Single(BareIdentifiers(r.OutputCode, "seed"));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_NestedLambdas_ParentLinkedDisplayClassChain()
    {
        var src = @"
class C {
    void M() {
        int outer = 0;
        System.Action a = () => {
            System.Action b = () => { outer++; };
            b();
        };
        a();
    }
}";
        var r = Transform(src);

        // M3 (D4): outer/inner each get their own display class; the child
        // links to the parent via `_cs2jParent` and its instance is built
        // inside the migrated outer method.
        Assert.True(r.Changed);
        Assert.Equal(2, HolderClassNames(r.OutputCode).Count);
        Assert.Equal(1, r.Statistics.HolderClassesGenerated);
        Assert.Equal(1, r.Statistics.NestedDisplayClasses);
        Assert.Contains("new global::System.Action(__cs2jClosure0.Lambda_0)", r.OutputCode);
        Assert.Contains(
            "var __cs2jClosure1 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1(this);",
            r.OutputCode);
        Assert.Contains("new global::System.Action(__cs2jClosure1.Lambda_0)", r.OutputCode);
        Assert.Contains("_cs2jParent._cs2jCap_outer++;", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "outer"));
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);

        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r.OutputCode, ParseWithSemantics(r.OutputCode), new ClosureLiftOptions());
        Assert.False(r2.Changed);
        Assert.Equal(r.OutputCode, r2.OutputCode);
    }

    // ── M2 transform tests: loops / interop / boundaries (design §7.1 10-19) ──

    [Fact]
    public void Transform_ForInduction_InstanceBeforeLoop_ConditionAndIncrementFieldBacked()
    {
        var src = @"
class C {
    void M(int n, System.Collections.Generic.List<System.Action> acts) {
        for (int i = 0; i < n; i++)
            acts.Add(() => System.Console.WriteLine(i));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(0, r.Statistics.ForeachPerIterationGroups);
        // C32 external shapes (for condition/increment, initializer, order) byte-kept (D2).
        Assert.Contains("for (; __cs2jClosure0._cs2jCap_i < n; __cs2jClosure0._cs2jCap_i++)", r.OutputCode);
        // M2: the Add-site lambda becomes a delegate creation; its body is the
        // class's Lambda_0 with the field read implicit-this.
        Assert.Contains("acts.Add(new global::System.Action(__cs2jClosure0.Lambda_0));", r.OutputCode);
        Assert.Contains("internal void Lambda_0() => System.Console.WriteLine(_cs2jCap_i);", r.OutputCode);
        Assert.DoesNotContain("int i", r.OutputCode);

        var initIndex = r.OutputCode.IndexOf("__cs2jClosure0._cs2jCap_i = 0;", StringComparison.Ordinal);
        var forIndex = r.OutputCode.IndexOf("for (;", StringComparison.Ordinal);
        Assert.True(initIndex >= 0 && initIndex < forIndex);
        Assert.Empty(BareIdentifiers(r.OutputCode, "i"));
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_ForeachIteration_InstanceFirstInBody_AndEmbeddedBodyWrapped()
    {
        var src = @"
class C {
    void M(System.Collections.Generic.List<System.Action> acts) {
        foreach (var item in new[] { 1, 2, 3 })
        {
            acts.Add(() => System.Console.WriteLine(item));
        }
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(1, r.Statistics.ForeachPerIterationGroups);

        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(r.OutputCode, ParseOptions).GetRoot();
        var loop = Assert.Single(root.DescendantNodes().OfType<ForEachStatementSyntax>());
        var body = Assert.IsType<BlockSyntax>(loop.Statement);
        Assert.Equal(3, body.Statements.Count);
        Assert.Equal("__cs2jClosure0",
            ((LocalDeclarationStatementSyntax)body.Statements[0]).Declaration.Variables[0].Identifier.Text);
        var snapshot = Assert.IsType<ExpressionStatementSyntax>(body.Statements[1]);
        Assert.IsType<AssignmentExpressionSyntax>(snapshot.Expression);
        Assert.Equal("item",
            ((IdentifierNameSyntax)((AssignmentExpressionSyntax)snapshot.Expression).Right).Identifier.Text);

        // The snapshot RHS is the single surviving bare 'item'; lambda + body sites are fields.
        Assert.Single(BareIdentifiers(r.OutputCode, "item"));
        AssertOutputCompiles(r.OutputCode);

        // Non-block embedded body: wrapped with instance + snapshot + original statement.
        var embedded = @"
class C {
    void M(System.Collections.Generic.List<System.Action> acts) {
        foreach (var v in new[] { 1, 2 }) acts.Add(() => System.Console.WriteLine(v));
    }
}";
        var r2 = Transform(embedded);
        var root2 = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(r2.OutputCode, ParseOptions).GetRoot();
        var loop2 = Assert.Single(root2.DescendantNodes().OfType<ForEachStatementSyntax>());
        var wrapped = Assert.IsType<BlockSyntax>(loop2.Statement);
        Assert.Equal(3, wrapped.Statements.Count);
        AssertOutputCompiles(r2.OutputCode);
    }

    [Fact]
    public void Transform_StaticAndCapturelessLambdas_NoHolder()
    {
        var src = @"
class C {
    void M() {
        System.Action a = static () => System.Console.WriteLine(1);
        System.Func<int> b = () => 42;
        a();
        System.Console.WriteLine(b());
    }
}";
        var r = Transform(src);

        // C45 M1: zero-capture / static lambdas lift to host static named
        // methods and their sites become explicit delegate creations. The C32
        // core invariant still holds — no holder class, no capture lifting.
        Assert.True(r.Changed);
        Assert.Contains("new global::System.Action(Cs2jLambda_M_0)", r.OutputCode);
        Assert.Contains("new global::System.Func<int>(Cs2jLambda_M_1)", r.OutputCode);
        Assert.Empty(HolderClassNames(r.OutputCode));
        Assert.Equal(0, r.Statistics.CapturesLifted);
        Assert.Equal(2, r.Statistics.StaticLambdaMethods);
        Assert.Equal(2, r.Statistics.DelegateSitesRewritten);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_AsyncLambdaMutableCapture_AwaitPreservedAndCompiles()
    {
        var src = @"
class C {
    async System.Threading.Tasks.Task M() {
        int x = 0;
        System.Func<System.Threading.Tasks.Task> f = async () => {
            await System.Threading.Tasks.Task.Yield();
            x++;
        };
        await f();
        System.Console.WriteLine(x);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // D17: async migrates through the instance-method path; await/body
        // structure is carried verbatim, the in-lambda write is an
        // implicit-this field, and the site is an explicit Func<Task> creation.
        Assert.Contains("await System.Threading.Tasks.Task.Yield();", r.OutputCode);
        Assert.Contains("internal async global::System.Threading.Tasks.Task Lambda_0()", r.OutputCode);
        Assert.Contains("System.Func<System.Threading.Tasks.Task> f = new global::System.Func<global::System.Threading.Tasks.Task>(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("_cs2jCap_x++;", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jClosure0._cs2jCap_x)", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "x"));
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_ExpressionTree_PureUnchanged_MixFreezesFile()
    {
        var pure = @"
using System;
using System.Linq.Expressions;
class C {
    void M() {
        Expression<Func<int, int>> e = x => x + 1;
    }
}";
        var rp = Transform(pure);
        Assert.False(rp.Changed);
        Assert.Equal(pure, rp.OutputCode);

        var mix = @"
using System;
using System.Linq.Expressions;
class C {
    void M() {
        int x = 0;
        Expression<Func<int>> q = () => x;
        Action a = () => { x++; };
    }
}";
        var rm = Transform(mix);
        Assert.False(rm.Changed);
        Assert.Equal(mix, rm.OutputCode);
        Assert.Contains(rm.Diagnostics, d =>
            d.Code == "CS2J3206" && d.Severity == ClosureLiftSeverity.Error);
        Assert.DoesNotContain(rm.Diagnostics, d => d.Code == "CS2J3207");
    }

    [Fact]
    public void Transform_RefArgument_FieldBacked_RefKindsFreeze()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x = 2; };
        Mutate(ref x);
        a();
    }
    static void Mutate(ref int z) { z++; }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("Mutate(ref __cs2jClosure0._cs2jCap_x)", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);

        var byRefParameter = @"
class C {
    void M(ref int p) {
        System.Action a = () => { p++; };
    }
}";
        var rp = Transform(byRefParameter);
        Assert.False(rp.Changed);
        Assert.Equal(byRefParameter, rp.OutputCode);
        Assert.Contains(rp.Diagnostics, d => d.Code == "CS2J3201");

        var refLocal = @"
class C {
    void M() {
        int z = 0;
        ref int p = ref z;
        System.Action a = () => { p++; };
    }
}";
        var rl = Transform(refLocal);
        Assert.False(rl.Changed);
        Assert.Equal(refLocal, rl.OutputCode);
        Assert.Contains(rl.Diagnostics, d => d.Code == "CS2J3201");
    }

    [Theory]
    [InlineData("CS2J3203", @"
class C {
    void M() {
        System.Comparison<int> c = delegate(int a, int b) { return a - b; };
    }
}")]
    [InlineData("CS2J3204", @"
class C {
    void M() {
        void Local() {
            System.Action a = () => { System.Console.WriteLine(1); };
        }
        Local();
    }
}")]
    [InlineData("CS2J3205", @"
unsafe class C {
    void M() {
        int* p = null;
        System.Action a = () => { var z = p; };
    }
}")]
    public void Transform_BlockingDiagnostic_FileVerbatim(string code, string src)
    {
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Empty(HolderClassNames(r.OutputCode));
        Assert.Contains(r.Diagnostics, d =>
            d.Code == code && d.Severity == ClosureLiftSeverity.Error);
    }

    [Fact]
    public void Transform_E02LiftedShape_CaptureParameterLiftsNormally()
    {
        var src = @"
class C {
    static void M_cs2jLifted(C @this, int _cs2jCap_count) {
        System.Action a = () => { _cs2jCap_count++; };
        a();
        System.Console.WriteLine(_cs2jCap_count);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("(C @this, int _cs2jCap_count)", r.OutputCode); // 签名不动
        // 唯二裸名扫描：快照 RHS 一处；另两处（lambda/WriteLine）已是字段访问。
        Assert.Single(BareIdentifiers(r.OutputCode, "_cs2jCap_count"));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_E05OutputShape_NoResidualRow_StatsCorrect()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
        System.Func<int> b = () => 42;
        a();
        System.Console.WriteLine(x + b());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J3203");
        // x 的引用站点恰为两处：lambda 内的 x++ 与提升后的 x + b()（声明符不是引用站点）。
        Assert.True(r.Statistics.SitesRewritten >= 2);
        Assert.Equal(1, r.Statistics.CapturesLifted);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_LockExpressionFieldBacked_UsingAndConstNotLifted()
    {
        var src = @"
class C {
    private readonly object _gate = new object();
    void M() {
        object locker = new object();
        System.Action a = () => { System.Console.WriteLine(locker); };
        lock (locker) { System.Console.WriteLine(locker); }
        locker = _gate;
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // External lock/write sites keep the C32 field shape verbatim (D2/D22)…
        Assert.Contains("lock (__cs2jClosure0._cs2jCap_locker)", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jClosure0._cs2jCap_locker)", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_locker = _gate;", r.OutputCode);
        // …while the lambda referencing locker migrates (M2).
        Assert.Contains("new global::System.Action(__cs2jClosure0.Lambda_0)", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(_cs2jCap_locker);", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);

        // D22/D5: const and a read-only parameter referenced by a lambda now
        // snapshot into read-only display-class fields; the lambda migrates and
        // the const declaration itself stays at its external site.
        var readOnly = @"
using System.IO;
class C {
    void M(MemoryStream u) {
        const int c = 3;
        System.Func<int> f = () => c + (int)u.Length;
        System.Console.WriteLine(f());
    }
}";
        var rr = Transform(readOnly);
        Assert.True(rr.Changed);
        Assert.Contains("const int c = 3;", rr.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_c = c;", rr.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_u = u;", rr.OutputCode);
        Assert.Contains("public int _cs2jCap_c;", rr.OutputCode);
        Assert.Contains("public global::System.IO.MemoryStream _cs2jCap_u;", rr.OutputCode);
        Assert.Contains("internal int Lambda_0() => _cs2jCap_c + (int)_cs2jCap_u.Length;", rr.OutputCode);
        Assert.Single(BareIdentifiers(rr.OutputCode, "c")); // snapshot RHS only
        Assert.Single(BareIdentifiers(rr.OutputCode, "u")); // snapshot RHS only
        Assert.Empty(LambdaNodes(rr.OutputCode));
        AssertOutputCompiles(rr.OutputCode);
    }

    // ── M2 transform tests: final group (design §7.1 20-25) ──

    [Fact]
    public void Transform_ThisAndFieldCapture_ThisBackedDisplayClass_Compiles()
    {
        var src = @"
class C {
    private int _n;
    void Use() { }
    void M() {
        System.Action a = () => { Use(); _n++; };
        a();
    }
}";
        var r = Transform(src);

        // D7: implicit-this member references force the display class to carry
        // `_cs2jThis`; the creation passes `this` and the migrated method
        // reaches host members through the field.
        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("var __cs2jClosure0 = new Cs2jDisplayClass_M_0(this);", r.OutputCode);
        Assert.Contains("private readonly C _cs2jThis;", r.OutputCode);
        Assert.Contains("public Cs2jDisplayClass_M_0(C @this)", r.OutputCode);
        Assert.Contains("_cs2jThis = @this;", r.OutputCode);
        Assert.Contains("System.Action a = new global::System.Action(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("_cs2jThis.Use(); _cs2jThis._n++;", r.OutputCode);
        Assert.Equal(1, r.Statistics.ThisOnlyLambdas);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_MultiDeclarator_OnlyLiftedSymbolRemoved()
    {
        var src = @"
class C {
    void M() {
        int a = 1, b = 2;
        System.Action w = () => a++;
        System.Console.WriteLine(b);
        w();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("int b = 2;", r.OutputCode);
        Assert.DoesNotContain("int a =", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "a"));
        Assert.Single(BareIdentifiers(r.OutputCode, "b")); // WriteLine(b) 原样保留
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_Idempotent_SecondRunUnchangedByteIdentical()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
        x = 1;
        a();
    }
}";
        var r1 = Transform(src);
        Assert.True(r1.Changed);

        // Fresh preprocessor + freshly parsed semantic model: reentry relies on
        // the reserved name prefixes only, exactly like the project pipeline.
        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r1.OutputCode, ParseWithSemantics(r1.OutputCode), new ClosureLiftOptions());

        Assert.False(r2.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
        Assert.Equal(0, r2.Statistics.HolderClassesGenerated);
        Assert.DoesNotContain("CS2J3207", string.Join("\n", r2.Diagnostics.Select(d => d.Code)));
    }

    [Fact]
    public void Transform_AuditOnly_StillVerbatimWithRows()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
    }
}";
        var r = Transform(src, new ClosureLiftOptions(AuditOnly: true));

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Equal(0, r.Statistics.FilesTransformed);
        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J3207"));
        Assert.Equal("x", row.SymbolName);
        Assert.Contains("Cs2jDisplayClass_M_0", row.Message);
    }

    [Fact]
    public void Transform_SingleElementArrayForm_SlotShapeCompilesAndIsIdempotent()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { System.Console.WriteLine(x); };
        x = 1;
        a();
    }
}";
        var options = new ClosureLiftOptions(HolderForm: HolderForm.SingleElementArray);
        var r = Transform(src, options);

        Assert.True(r.Changed);
        Assert.DoesNotContain("Cs2jDisplayClass", r.OutputCode);
        Assert.Contains("var __cs2jArr_x = new int[1];", r.OutputCode);
        Assert.Contains("__cs2jArr_x[0] = 0;", r.OutputCode);
        Assert.Contains("__cs2jArr_x[0] = 1;", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jArr_x[0])", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);

        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r.OutputCode, ParseWithSemantics(r.OutputCode), options);
        Assert.False(r2.Changed);
        Assert.Equal(r.OutputCode, r2.OutputCode);
    }

    [Fact]
    public void Transform_NameOfAttributeAndXmlDoc_SameNameIdentifiersPreserved()
    {
        var src = @"
class C {
    /// <param name=""seed"">documented</param>
    [System.Obsolete(nameof(seed))]
    void M(int seed) {
        seed = 1;
        System.Action a = () => seed++;
        var n = nameof(seed);
        System.Console.WriteLine(n);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("/// <param name=\"seed\">documented</param>", r.OutputCode);
        Assert.Contains("[System.Obsolete(nameof(seed))]", r.OutputCode);
        Assert.Contains("var n = nameof(seed);", r.OutputCode);
        Assert.DoesNotContain("nameof(__cs2jClosure", r.OutputCode);

        // Snapshot RHS (1) + the two nameof(seed) IdentifierNames (2) = 3;
        // every other seed site is field-backed.
        Assert.Equal(3, BareIdentifiers(r.OutputCode, "seed").Count);
        AssertOutputCompiles(r.OutputCode);
    }

    // ── Generic arity (fixture M2: holder nested at class level) ──

    [Fact]
    public void Transform_GenericMethodCapture_HolderRedeclaresMethodArity()
    {
        var src = @"
class C {
    public T[] Pick<T>(T first, T second) {
        var current = first;
        System.Func<T> get = () => current;
        System.Action swap = () => current = second;
        swap();
        return new[] { first, get() };
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("private sealed class Cs2jDisplayClass_Pick_0<T>", r.OutputCode);
        Assert.Contains("public T _cs2jCap_current;", r.OutputCode);
        Assert.Contains("new Cs2jDisplayClass_Pick_0<T>()", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_GenericHostTypeCapture_HolderReusesOuterTypeParameter()
    {
        var src = @"
class C<T> {
    public T Pick(T first, T second) {
        var current = first;
        System.Func<T> get = () => current;
        System.Action swap = () => current = second;
        swap();
        return get();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // A nested class sees the outer type parameter; the holder must not
        // redeclare its own arity (that would shadow and bind fields wrong).
        Assert.Contains("private sealed class Cs2jDisplayClass_Pick_0", r.OutputCode);
        Assert.DoesNotContain("Cs2jDisplayClass_Pick_0<", r.OutputCode);
        Assert.Contains("new Cs2jDisplayClass_Pick_0()", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void Transform_CapturelessStaticLambdaInFieldInitializer_StaysClean()
    {
        var src = @"
class C {
    private static readonly System.Func<int, int> Square = static x => x * x;
    public static int Run(int x) {
        System.Func<int, int> addOne = static v => v + 1;
        return addOne(x) + Square(x);
    }
}";
        var r = Transform(src);

        // C45 M1: the method-body static lambda lifts; the field-initializer
        // lambda stays verbatim (D18③/D21 — fields are not analysis roots),
        // so no holder class and no error rows for this file.
        Assert.True(r.Changed);
        Assert.Contains("static x => x * x", r.OutputCode);
        Assert.Contains("new global::System.Func<int, int>(Cs2jLambda_Run_0)", r.OutputCode);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity == ClosureLiftSeverity.Error);
        Assert.Empty(HolderClassNames(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    // ── M2 residual gate (design §7.3) ──

    [Fact]
    public void ResidualGate_TransformedOutput_Zero()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
        x = 1;
        a();
    }
}";
        var r = Transform(src);
        Assert.True(r.Changed);

        var gate = new ClosureLiftPreprocessor().CountResidualMutableCaptures(r.OutputCode);
        Assert.Equal(0, gate);
    }

    [Fact]
    public void ResidualGate_RawMutableCapture_Counted()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => x++;
    }
}";
        var gate = new ClosureLiftPreprocessor().CountResidualMutableCaptures(src);
        Assert.Equal(1, gate);
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

    // ── M2 transform helpers ──

    private static ClosureLiftResult Transform(string src, ClosureLiftOptions? options = null)
        => new ClosureLiftPreprocessor().Preprocess(
            src, ParseWithSemantics(src), options ?? new ClosureLiftOptions());

    /// <summary>Design §7.1: every transformed tree must compile (LanguageVersion.Latest, unsafe allowed).</summary>
    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-transformed",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(
            errors.Count == 0,
            "Transformed output failed to compile:\n" + string.Join("\n", errors.Select(e => e.GetMessage())));
    }

    /// <summary>All IdentifierName nodes spelling <paramref name="name"/> (declarators/parameters are NOT included).</summary>
    private static IReadOnlyList<IdentifierNameSyntax> BareIdentifiers(string code, string name)
    {
        var root = (SyntaxNode)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        return root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == name)
            .ToList();
    }

    private static IReadOnlyList<string> HolderClassNames(string code)
    {
        var root = (SyntaxNode)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        return root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.Text.StartsWith("Cs2jDisplayClass_", StringComparison.Ordinal))
            .Select(c => c.Identifier.Text)
            .ToList();
    }

    /// <summary>All surviving lambda nodes (design §7.3 residual gate view at text level).</summary>
    private static IReadOnlyList<LambdaExpressionSyntax> LambdaNodes(string code)
        => ((SyntaxNode)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot())
            .DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
}
