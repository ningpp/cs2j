using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.IteratorLowering.ControlFlow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Xunit;

namespace CSharpToJava.ProjectTests;

public partial class IteratorLoweringUnitTests
{
    // ── Task 1: Options / Statistics / Diagnostic ──

    [Fact]
    public void Options_Default_IsStrict()
    {
        var opts = new IteratorLoweringOptions();
        Assert.True(opts.Strict);
    }

    [Fact]
    public void Statistics_Defaults_AreZero()
    {
        var stats = new IteratorLoweringStatistics();
        Assert.Equal(0, stats.FilesScanned);
        Assert.Equal(0, stats.FilesTransformed);
        Assert.Equal(0, stats.IteratorMembersLowered);
        Assert.Equal(0, stats.HoistedEnumerators);
        Assert.Equal(0, stats.FinallyRegions);
        Assert.Equal(0, stats.ResidualCount);
    }

    [Fact]
    public void Diagnostic_Records_FieldsCorrectly()
    {
        var d = new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            IteratorLoweringDiagnosticCode.CS2J0402,
            "test message",
            "File.cs",
            42);
        Assert.Equal(IteratorLoweringSeverity.Error, d.Severity);
        Assert.Equal(IteratorLoweringDiagnosticCode.CS2J0402, d.Code);
        Assert.Equal("test message", d.Message);
        Assert.Equal("File.cs", d.FilePath);
        Assert.Equal(42, d.Line);
    }

    // ── Task 2: Naming ──

    [Fact]
    public void Naming_FieldNames_ArePrefixed()
    {
        Assert.Equal("_cs2jState", IteratorLoweringNaming.StateField);
        Assert.Equal("_cs2jCurrent", IteratorLoweringNaming.CurrentField);
        Assert.Equal("_cs2jInitialThreadId", IteratorLoweringNaming.InitialThreadIdField);
        Assert.Equal("_cs2jThis", IteratorLoweringNaming.ThisField);
        Assert.Equal("_cs2jActiveRegion", IteratorLoweringNaming.ActiveRegionField);
        Assert.Equal("_cs2jCap_count", IteratorLoweringNaming.GetCaptureFieldName("count"));
        Assert.Equal("_cs2jLoc_i", IteratorLoweringNaming.GetLocalFieldName("i"));
        Assert.Equal("_cs2jEnum_0", IteratorLoweringNaming.GetEnumeratorFieldName(0));
    }

    [Fact]
    public void Naming_ResumeLabel_IsPrefixed()
    {
        Assert.Equal("_cs2jResume_1", IteratorLoweringNaming.GetResumeLabel(1));
        Assert.Equal("_cs2jResume_3", IteratorLoweringNaming.GetResumeLabel(3));
    }

    [Fact]
    public void Naming_ResolveUniqueName_AppendsSuffix()
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "Cs2jIterator_Foo_0" };
        var resolved = IteratorLoweringNaming.ResolveUniqueName("Cs2jIterator_Foo_0", used);
        Assert.Equal("Cs2jIterator_Foo_02", resolved);
        Assert.Contains("Cs2jIterator_Foo_02", used);
    }

    // ── Task 3: StateMachinePlan / RegionPlan ──

    [Fact]
    public void StateMachinePlan_Stores_BasicFields()
    {
        var region = new RegionPlan(0, null, RegionKind.UserFinally, 1, 2, null);
        var plan = new StateMachinePlan(
            MemberName: "GetTokens",
            StateMachineClassName: "Cs2jIterator_GetTokens_0",
            ElementType: "string",
            IsIEnumerableReturn: true,
            IsIEnumeratorReturn: false,
            IsNonGenericReturn: false,
            IsGenericMethod: false,
            DeclaringTypeName: "Writer",
            ThisCaptureType: "Writer",
            IsStatic: false,
            Parameters: new[] { new CaptureField("count", "int", "_cs2jCap_count") },
            HoistedLocals: new[] { new CaptureField("i", "int", "_cs2jLoc_i") },
            HoistedEnumerators: Array.Empty<CaptureField>(),
            Regions: new[] { region },
            YieldReturnCount: 1,
            HasYieldBreak: false);

        Assert.Equal("GetTokens", plan.MemberName);
        Assert.Equal("Cs2jIterator_GetTokens_0", plan.StateMachineClassName);
        Assert.Equal("string", plan.ElementType);
        Assert.True(plan.IsIEnumerableReturn);
        Assert.False(plan.IsIEnumeratorReturn);
        Assert.Single(plan.Parameters);
        Assert.Single(plan.HoistedLocals);
        Assert.Single(plan.Regions);
        Assert.Equal(1, plan.YieldReturnCount);
    }

    [Fact]
    public void RegionPlan_ContainsState_FollowsInclusiveRange()
    {
        var region = new RegionPlan(2, 0, RegionKind.UserFinally, 3, 5, null);
        Assert.False(region.ContainsState(2));
        Assert.True(region.ContainsState(3));
        Assert.True(region.ContainsState(5));
        Assert.False(region.ContainsState(6));
        Assert.Equal(0, region.ParentId!.Value);
        Assert.Equal(RegionKind.UserFinally, region.Kind);
    }

    [Fact]
    public void CaptureField_Records_Name_Type_FieldName()
    {
        var cf = new CaptureField("count", "int", "_cs2jCap_count");
        Assert.Equal("count", cf.OriginalName);
        Assert.Equal("int", cf.Type);
        Assert.Equal("_cs2jCap_count", cf.FieldName);
    }

    // ── Task 4: IteratorBodyAnalysis ──

    [Fact]
    public void BodyAnalysis_AssignsSequentialStates_ToYieldReturns()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        yield return 1;
        yield return 2;
        yield return 3;
    }
}";
        var info = AnalyzeBody(src);

        Assert.Equal(3, info.YieldReturns.Count);
        Assert.Equal(3, info.Plan.YieldReturnCount);
        Assert.Equal(new[] { 1, 2, 3 },
            info.YieldReturns.Select((_, i) => i + 1).ToArray());
    }

    [Fact]
    public void BodyAnalysis_DetectsYieldBreak()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(bool early) {
        if (early) yield break;
        yield return 1;
    }
}";
        var info = AnalyzeBody(src);

        Assert.True(info.Plan.HasYieldBreak);
        Assert.Equal(1, info.Plan.YieldReturnCount);
    }

    [Fact]
    public void BodyAnalysis_HoistsLoopVariable_AndParameter_WhenYieldIsInsideLoop()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int count) {
        for (int i = 0; i < count; i++)
            yield return i;
    }
}";
        var info = AnalyzeBody(src);

        Assert.Contains(info.Plan.HoistedLocals, f => f.OriginalName == "i");
        Assert.Contains(info.Plan.Parameters, f => f.OriginalName == "count");
    }

    [Fact]
    public void BodyAnalysis_ListForeach_HoistsStructEnumerator_AndBuildsDisposeRegion()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(List<int> xs) {
        foreach (var x in xs)
            yield return x;
    }
}";
        var info = AnalyzeBody(src);
        var feInfo = Assert.Single(info.Foreaches).Value;

        Assert.False(feInfo.IsArray);
        Assert.True(feInfo.IsDisposable);
        Assert.True(feInfo.IsEnumeratorValueType);
        Assert.NotNull(feInfo.Region);
        Assert.Equal(RegionKind.ForeachDispose, feInfo.Region!.Kind);
        Assert.Single(info.Plan.HoistedEnumerators);
        Assert.Contains(info.Plan.Regions, r => r.Kind == RegionKind.ForeachDispose);
    }

    [Fact]
    public void BodyAnalysis_ArrayForeach_HasNoDisposeRegion()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int[] xs) {
        foreach (var x in xs)
            yield return x;
    }
}";
        var info = AnalyzeBody(src);
        var feInfo = Assert.Single(info.Foreaches).Value;

        Assert.True(feInfo.IsArray);
        Assert.Null(feInfo.Region);
        Assert.Empty(info.Plan.Regions);
    }

    [Fact]
    public void BodyAnalysis_NestedTryFinally_BuildsParentChain()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        try {
            yield return 1;
            try { yield return 2; } finally { }
        } finally { }
    }
}";
        var info = AnalyzeBody(src);
        var regions = info.Plan.Regions;

        Assert.Equal(2, regions.Count);
        var outer = Assert.Single(regions, r => r.FirstState == 1);
        var inner = Assert.Single(regions, r => r.FirstState == 2);
        Assert.Equal(outer.Id, inner.ParentId!.Value);
    }

    // ── Task 5: Analyzer gates ──

    [Fact]
    public void Analyzer_FindsIteratorMethod_ReturningIEnumerable()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; }
}";
        var model = ParseWithSemantics(src);
        var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

        Assert.Single(result.Plans);
        Assert.Equal("M", result.Plans[0].MemberName);
        Assert.Equal("int", result.Plans[0].ElementType);
        Assert.True(result.Plans[0].IsIEnumerableReturn);
    }

    [Fact]
    public void Analyzer_SkipsNonIteratorMethod()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { return new List<int> { 1 }; }
}";
        var model = ParseWithSemantics(src);
        var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

        Assert.Empty(result.Plans);
    }

    [Fact]
    public void Analyzer_SkipsIteratorWithRefParam_YieldsDiagnostic()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(ref int x) { yield return x; }
}";
        var model = ParseWithSemantics(src);
        var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0402);
    }

    [Fact]
    public void Analyzer_DetectsStaticMethod()
    {
        var src = @"
using System.Collections.Generic;
class C {
    static IEnumerable<int> M(int n) { for (int i = 0; i < n; i++) yield return i; }
}";
        var model = ParseWithSemantics(src);
        var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

        Assert.Single(result.Plans);
        Assert.True(result.Plans[0].IsStatic);
    }

    [Fact]
    public void Analyzer_DetectsGenericMethod()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<T> M<T>(T val) { yield return val; }
}";
        var model = ParseWithSemantics(src);
        var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

        Assert.Single(result.Plans);
        Assert.True(result.Plans[0].IsGenericMethod);
    }

    [Theory]
    // M4b: bare `return;` inside an iterator is legal C# and normalized to
    // yield break by the preprocessor, so it is no longer rejected here.
    [InlineData("checked { yield return x; }", "int x")]
    [InlineData("using (var d = new System.IO.StringWriter()) { yield return 1; }", "")]
    [InlineData("lock (new object()) { yield return 1; }", "")]
    public void Analyzer_Rejects_UnsupportedM2Constructs(string bodySnippet, string parameters)
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(" + parameters + @") {
        " + bodySnippet + @"
    }
}";
        var model = ParseWithSemantics(src);
        var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, d => d.Severity == IteratorLoweringSeverity.Error
            && d.Code == IteratorLoweringDiagnosticCode.CS2J0402);
    }

    [Fact]
    public void Analyzer_TryFinallyWithYield_IsAccepted()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        try { yield return 1; } finally { }
    }
}";
        var model = ParseWithSemantics(src);
        var result = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());

        Assert.Single(result.Plans);
        Assert.Single(result.Plans[0].Regions);
    }

    // ── Task 6: Rewriter structural checks ──

    [Fact]
    public void Rewriter_SimpleYieldReturn_ProducesStateMachine()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 42; }
}";
        var (output, stats, _) = Rewrite(src);

        Assert.Equal(1, stats.IteratorMembersLowered);
        Assert.Contains("Cs2jIterator_M_0", output);
        Assert.Contains("_cs2jState", output);
        Assert.Contains("_cs2jCurrent", output);
        Assert.Contains("MoveNext", output);
        Assert.Contains("GetEnumerator", output);
        Assert.DoesNotContain("yield return", output);
    }

    [Fact]
    public void Rewriter_MultipleYields_ProducesMultipleResumeStates()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        yield return 1;
        yield return 2;
    }
}";
        var (output, _, _) = Rewrite(src);

        Assert.Contains("_cs2jResume_1", output);
        Assert.Contains("_cs2jResume_2", output);
        Assert.DoesNotContain("yield return", output);
    }

    [Fact]
    public void Rewriter_YieldBreak_ProducesEarlyExit()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(bool early) {
        if (early) yield break;
        yield return 1;
    }
}";
        var (output, _, _) = Rewrite(src);

        Assert.DoesNotContain("yield break", output);
        Assert.DoesNotContain("yield return", output);
    }

    [Fact]
    public void Rewriter_ForLoop_HoistsLocalsAndParameters()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int count) {
        for (int i = 0; i < count; i++)
            yield return i;
    }
}";
        var (output, _, _) = Rewrite(src);

        Assert.Contains("_cs2jLoc_i", output);
        Assert.Contains("_cs2jCap_count", output);
        Assert.DoesNotContain("yield return", output);
    }

    [Fact]
    public void Rewriter_ProtectedRegion_DeclaresActiveRegion_Field()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        try { yield return 1; } finally { }
    }
}";
        var (output, stats, _) = Rewrite(src);

        Assert.Contains("_cs2jActiveRegion", output);
        Assert.Equal(1, stats.FinallyRegions);
    }

    [Fact]
    public void Rewriter_NoProtectedRegion_OmitsActiveRegionField()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; }
}";
        var (output, _, _) = Rewrite(src);

        Assert.DoesNotContain("_cs2jActiveRegion", output);
    }

    [Fact]
    public void Rewriter_StaticMethod_NoThisField()
    {
        var src = @"
using System.Collections.Generic;
class C {
    static IEnumerable<int> M() { yield return 1; }
}";
        var (output, _, _) = Rewrite(src);

        Assert.DoesNotContain("_cs2jThis", output);
    }

    [Fact]
    public void Rewriter_SkippedMember_PreservedUnchanged()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> Good() { yield return 1; }
    IEnumerable<int> Bad(ref int x) { yield return x; }
}";
        var (output, _, _) = Rewrite(src);

        Assert.Contains("Cs2jIterator_Good_0", output);
        Assert.Contains("yield return x", output);
    }

    [Fact]
    public void Rewriter_PropertyGetter_ProducesStateMachine()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> Items {
        get {
            yield return 1;
            yield return 2;
        }
    }
}";
        var (output, stats, diags) = Rewrite(src);

        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.Equal(1, stats.IteratorMembersLowered);
        Assert.Contains("Cs2jIterator_Items_0", output);
        Assert.Contains("return new Cs2jIterator_Items_0", output);
        Assert.DoesNotContain("yield return", output);
        AssertOutputCompilesZeroDiagnostics(output);
    }

    [Fact]
    public void Rewriter_MethodAndProperty_AreBothLowered()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; }
    public IEnumerable<int> Items {
        get { yield return 2; }
    }
}";
        var (output, stats, diags) = Rewrite(src);

        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.Equal(2, stats.IteratorMembersLowered);
        Assert.Contains("Cs2jIterator_M_0", output);
        Assert.Contains("Cs2jIterator_Items_1", output);
        Assert.DoesNotContain("yield", output);
        AssertOutputCompilesZeroDiagnostics(output);
    }

    [Fact]
    public void Rewriter_IndexerGetter_ProducesStateMachine()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> this[int i] {
        get { yield return i; }
    }
}";
        var (output, stats, diags) = Rewrite(src);

        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.Equal(1, stats.IteratorMembersLowered);
        Assert.Contains("Cs2jIterator_Item_0", output);
        Assert.Contains("return new Cs2jIterator_Item_0(this, i, -2)", Flat(output));
        Assert.DoesNotContain("yield", output);
        AssertOutputCompilesZeroDiagnostics(output);
    }

    [Fact]
    public void Runtime_PropertyGetter_Enumerates()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> Items {
        get {
            yield return 1;
            yield return 2;
        }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var result = (IEnumerable)asm.GetType("C")!.GetProperty("Items")!.GetValue(c)!;
        Assert.Equal(new object[] { 1, 2 }, ToList(result).ToArray());
    }

    // ── Task 7: Preprocessor ──

    [Fact]
    public void Preprocessor_NoYield_ReturnsUnchanged()
    {
        var src = @"
using System.Collections.Generic;
class C {
    List<int> M() { return new List<int> { 1, 2, 3 }; }
}";
        var model = ParseWithSemantics(src);
        var preprocessor = new IteratorLoweringPreprocessor();
        var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions());

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesScanned);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void Preprocessor_WithYield_ReturnsChanged()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; yield return 2; }
}";
        var model = ParseWithSemantics(src);
        var preprocessor = new IteratorLoweringPreprocessor();
        var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions());

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Equal(1, result.Statistics.IteratorMembersLowered);
        Assert.Equal(2, result.Statistics.YieldReturnSites);
        Assert.DoesNotContain("yield return", result.OutputCode);
    }

    [Fact]
    public void Preprocessor_StrictMode_WithSkip_HasErrorDiagnostic()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(ref int x) { yield return x; }
}";
        var model = ParseWithSemantics(src);
        var preprocessor = new IteratorLoweringPreprocessor();
        var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions());

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == IteratorLoweringSeverity.Error
            && d.Code == IteratorLoweringDiagnosticCode.CS2J0402);
    }

    [Fact]
    public void Preprocessor_NonStrictMode_WithSkip_HasWarningDiagnostic()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(ref int x) { yield return x; }
}";
        var model = ParseWithSemantics(src);
        var preprocessor = new IteratorLoweringPreprocessor();
        var result = preprocessor.Preprocess(src, model, new IteratorLoweringOptions(Strict: false));

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == IteratorLoweringSeverity.Warning);
    }

    // ── Task 8: 发射产物零编译诊断（无 warning 的干净形态） ──

    [Fact]
    public void Emit_SimpleIterator_HasZeroCompileDiagnostics()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        yield return 1;
        yield return 2;
    }
}";
        var (output, _, _) = Rewrite(src);
        AssertOutputCompilesZeroDiagnostics(output);
    }

    [Fact]
    public void Emit_ProtectedRegionIterator_HasZeroCompileDiagnostics()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        try {
            yield return 1;
            yield return 2;
        } finally { }
    }
}";
        var (output, _, _) = Rewrite(src);
        AssertOutputCompilesZeroDiagnostics(output);
    }

    // ── Task 9: 运行时语义（M2 核心验收） ──

    [Fact]
    public void Runtime_SimpleSequence()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M() {
        yield return 1;
        yield return 2;
        yield return 3;
    }
}";
        var (values, _) = EnumerateFully(LoadRewritten(code), "M");
        Assert.Equal(new object[] { 1, 2, 3 }, values);
    }

    [Fact]
    public void Runtime_ForLoop_WithParameter()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(int n) {
        for (int i = 0; i < n; i++)
            yield return i * i;
    }
}";
        var (values, _) = EnumerateFully(LoadRewritten(code), "M", 4);
        Assert.Equal(new object[] { 0, 1, 4, 9 }, values);
    }

    [Fact]
    public void Runtime_WhileAndDoLoops()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> WhileM(int n) {
        int i = 0;
        while (i < n) { yield return i; i++; }
    }
    public IEnumerable<int> DoM(int n) {
        int i = 0;
        do { yield return i; i++; } while (i < n);
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        Assert.Equal(new object[] { 0, 1, 2 }, EnumerateFully(asm, c, "WhileM", 3).values);
        Assert.Equal(new object[] { 0, 1, 2 }, EnumerateFully(asm, c, "DoM", 3).values);
    }

    [Fact]
    public void Runtime_ListForeach_StructEnumerator()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(List<int> xs) {
        foreach (var x in xs)
            yield return x + 1;
    }
}";
        var (values, _) = EnumerateFully(LoadRewritten(code), "M",
            new List<int> { 10, 20, 30 });
        Assert.Equal(new object[] { 11, 21, 31 }, values);
    }

    [Fact]
    public void Runtime_ArrayForeach()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(int[] xs) {
        foreach (var x in xs)
            yield return x;
    }
}";
        var (values, _) = EnumerateFully(LoadRewritten(code), "M",
            new[] { 7, 8, 9 });
        Assert.Equal(new object[] { 7, 8, 9 }, values);
    }

    [Fact]
    public void Runtime_ParameterAndThisCapture()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int Base = 100;
    public int Scale = 3;
    public IEnumerable<int> M(int add) {
        yield return Base + add;
        yield return Scale + add;
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, _) = EnumerateFully(asm, c, "M", 5);
        Assert.Equal(new object[] { 105, 8 }, values);
    }

    [Fact]
    public void Runtime_GenericMethod()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<T> M<T>(T a, T b) {
        yield return a;
        yield return b;
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var result = Invoke(asm, c, "M", new[] { typeof(int) }, 4, 5);
        var values = ToList((IEnumerable)result);
        Assert.Equal(new object[] { 4, 5 }, values);
    }

    [Fact]
    public void Runtime_NonGenericIEnumerable()
    {
        var code = @"
using System.Collections;
class C {
    public IEnumerable M() {
        yield return 1;
        yield return ""two"";
    }
}";
        var (values, _) = EnumerateFully(LoadRewritten(code), "M");
        Assert.Equal(new object[] { 1, "two" }, values);
    }

    [Fact]
    public void Runtime_IEnumeratorReturn_IsDirectlyEnumerable()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerator<int> M(int start) {
        yield return start;
        yield return start + 1;
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var enumerator = (IEnumerator)Invoke(asm, c, "M", null, 40)!;
        var values = new List<object?>();
        while (enumerator.MoveNext()) values.Add(enumerator.Current);
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(new object[] { 40, 41 }, values);
    }

    [Fact]
    public void Runtime_FinallyRunsExactlyOnce_OnCompletion()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int FinallyCount;
    public IEnumerable<int> M() {
        try {
            yield return 1;
            yield return 2;
        } finally { FinallyCount++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, disposable) = EnumerateFully(asm, c, "M");
        Assert.Equal(new object[] { 1, 2 }, values);
        Assert.Equal(1, GetField(c, "FinallyCount"));
        disposable.Dispose();
        Assert.Equal(1, GetField(c, "FinallyCount"));
    }

    [Fact]
    public void Runtime_FinallyRunsExactlyOnce_OnEarlyDispose()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int FinallyCount;
    public IEnumerable<int> M() {
        try {
            yield return 1;
            yield return 2;
            yield return 3;
        } finally { FinallyCount++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var enumerator = ((IEnumerable)Invoke(asm, c, "M")!).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current);
        Assert.True(enumerator.MoveNext());
        Assert.Equal(0, GetField(c, "FinallyCount"));

        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, GetField(c, "FinallyCount"));
        // 重复 Dispose 幂等；之后 MoveNext 立即终止。
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, GetField(c, "FinallyCount"));
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void Runtime_FinallyNotRun_WhenDisposedBeforeFirstMoveNext()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int FinallyCount;
    public IEnumerable<int> M() {
        try { yield return 1; } finally { FinallyCount++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var enumerator = ((IEnumerable)Invoke(asm, c, "M")!).GetEnumerator();
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(0, GetField(c, "FinallyCount"));
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void Runtime_YieldBreakInTry_FinallyExactlyOnce()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int FinallyCount;
    public IEnumerable<int> M() {
        try {
            yield return 1;
            yield break;
        } finally { FinallyCount++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, _) = EnumerateFully(asm, c, "M");
        Assert.Equal(new object[] { 1 }, values);
        Assert.Equal(1, GetField(c, "FinallyCount"));
    }

    [Fact]
    public void Runtime_BreakOutOfForeachInsideTry()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int FinallyCount;
    public IEnumerable<int> M(List<int> xs) {
        try {
            foreach (var x in xs) {
                if (x == 3) break;
                yield return x;
            }
        } finally { FinallyCount++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, _) = EnumerateFully(asm, c, "M",
            new List<int> { 1, 2, 3, 4 });
        Assert.Equal(new object[] { 1, 2 }, values);
        Assert.Equal(1, GetField(c, "FinallyCount"));
    }

    [Fact]
    public void Runtime_ContinueInsideForeach_DoesNotDisposeEnumeratorEarly()
    {
        var code = @"
using System.Collections;
using System.Collections.Generic;
class C {
    public TrackedEnumerable Items = new TrackedEnumerable(1, 2, 3, 4, 5);
    public int InnerFinally;
    public IEnumerable<int> M() {
        foreach (var x in Items) {
            try {
                if (x % 2 == 0) continue;
                yield return x;
            } finally { InnerFinally++; }
        }
    }
}
" + TrackedEnumerableSource;
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, _) = EnumerateFully(asm, c, "M");
        Assert.Equal(new object[] { 1, 3, 5 }, values);
        // 每次 continue 都执行内层 finally（2 次）+ 正常产出退出（3 次）。
        Assert.Equal(5, GetField(c, "InnerFinally"));
        // 枚举器仅在整个 foreach 结束时释放一次。
        var items = GetField(c, "Items");
        Assert.Equal(1, GetField(items!, "DisposeCount"));
    }

    [Fact]
    public void Runtime_BreakCrossesUserFinallyAndForeachRegion()
    {
        var code = @"
using System.Collections;
using System.Collections.Generic;
class C {
    public TrackedEnumerable Items = new TrackedEnumerable(1, 2, 3, 4, 5);
    public int InnerFinally;
    public IEnumerable<int> M() {
        foreach (var x in Items) {
            try {
                if (x == 4) break;
                yield return x;
            } finally { InnerFinally++; }
        }
    }
}
" + TrackedEnumerableSource;
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, _) = EnumerateFully(asm, c, "M");
        Assert.Equal(new object[] { 1, 2, 3 }, values);
        Assert.Equal(4, GetField(c, "InnerFinally"));
        var items = GetField(c, "Items");
        Assert.Equal(1, GetField(items!, "DisposeCount"));
    }

    [Fact]
    public void Runtime_NestedFinally_AllThreeExitPaths()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int Outer;
    public int Inner;
    public IEnumerable<int> M(int stop) {
        try {
            yield return 1;
            try {
                yield return 2;
                if (stop == 1) yield break;
                yield return 3;
            } finally { Inner++; }
            yield return 4;
        } finally { Outer++; }
    }
}";
        var asm = LoadRewritten(code);

        // 路径 1：正常枚举完。
        var c1 = NewC(asm);
        var (v1, _) = EnumerateFully(asm, c1, "M", 0);
        Assert.Equal(new object[] { 1, 2, 3, 4 }, v1);
        Assert.Equal(1, GetField(c1, "Inner"));
        Assert.Equal(1, GetField(c1, "Outer"));

        // 路径 2：内层 yield break，内外各一次。
        var c2 = NewC(asm);
        var (v2, _) = EnumerateFully(asm, c2, "M", 1);
        Assert.Equal(new object[] { 1, 2 }, v2);
        Assert.Equal(1, GetField(c2, "Inner"));
        Assert.Equal(1, GetField(c2, "Outer"));

        // 路径 3：挂起在内层区域时提前 Dispose，内外各一次。
        var c3 = NewC(asm);
        var en3 = ((IEnumerable)Invoke(asm, c3, "M", null, 1)!).GetEnumerator();
        en3.MoveNext();
        en3.MoveNext();
        ((IDisposable)en3).Dispose();
        Assert.Equal(1, GetField(c3, "Inner"));
        Assert.Equal(1, GetField(c3, "Outer"));

        // 路径 4：挂起在外层区域（内层尚未进入）时提前 Dispose，仅外层。
        var c4 = NewC(asm);
        var en4 = ((IEnumerable)Invoke(asm, c4, "M", null, 0)!).GetEnumerator();
        en4.MoveNext();
        ((IDisposable)en4).Dispose();
        Assert.Equal(0, GetField(c4, "Inner"));
        Assert.Equal(1, GetField(c4, "Outer"));
    }

    [Fact]
    public void Runtime_ForeachInsideTry_NormalAndEarlyDispose()
    {
        var code = @"
using System.Collections;
using System.Collections.Generic;
class C {
    public TrackedEnumerable Items = new TrackedEnumerable(1, 2, 3, 4);
    public int FinallyCount;
    public IEnumerable<int> M() {
        try {
            foreach (var x in Items)
                yield return x;
        } finally { FinallyCount++; }
    }
}
" + TrackedEnumerableSource;
        var asm = LoadRewritten(code);

        var c1 = NewC(asm);
        var (v1, _) = EnumerateFully(asm, c1, "M");
        Assert.Equal(new object[] { 1, 2, 3, 4 }, v1);
        Assert.Equal(1, GetField(c1, "FinallyCount"));
        Assert.Equal(1, GetField(GetField(c1, "Items")!, "DisposeCount"));

        var c2 = NewC(asm);
        var en2 = ((IEnumerable)Invoke(asm, c2, "M")!).GetEnumerator();
        en2.MoveNext();
        en2.MoveNext();
        ((IDisposable)en2).Dispose();
        Assert.Equal(1, GetField(c2, "FinallyCount"));
        Assert.Equal(1, GetField(GetField(c2, "Items")!, "DisposeCount"));
    }

    [Fact]
    public void Runtime_ExceptionInBody_RunsOnce_AndKillsEnumerator()
    {
        var code = @"
using System;
using System.Collections.Generic;
class C {
    public int FinallyCount;
    public IEnumerable<int> M() {
        try {
            yield return 1;
            throw new InvalidOperationException(""boom"");
        } finally { FinallyCount++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var en = ((IEnumerable)Invoke(asm, c, "M")!).GetEnumerator();
        Assert.True(en.MoveNext());
        Assert.Equal(1, en.Current);
        // 第二次 MoveNext 经接口调用直接抛出用户异常（catch 补跑 finally 后裸 throw）。
        Assert.Throws<InvalidOperationException>(() => en.MoveNext());
        Assert.Equal(1, GetField(c, "FinallyCount"));
        // 异常后枚举器死亡；Dispose 不重复执行 finally。
        Assert.False(en.MoveNext());
        ((IDisposable)en).Dispose();
        Assert.Equal(1, GetField(c, "FinallyCount"));
    }

    [Fact]
    public void Runtime_RepeatEnumeration_NoSharedState()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int FinallyRuns;
    public IEnumerable<int> M() {
        try {
            yield return 1;
            yield return 2;
        } finally { FinallyRuns++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var e1 = ((IEnumerable)Invoke(asm, c, "M")!).GetEnumerator();
        var e2 = ((IEnumerable)Invoke(asm, c, "M")!).GetEnumerator();

        // 交错推进：两个枚举器互不共享状态。
        var collected = new List<object?>();
        Assert.True(e1.MoveNext());
        collected.Add(e1.Current);
        Assert.True(e2.MoveNext());
        collected.Add(e2.Current);
        Assert.True(e1.MoveNext());
        collected.Add(e1.Current);
        Assert.True(e2.MoveNext());
        collected.Add(e2.Current);
        Assert.Equal(new object[] { 1, 1, 2, 2 }, collected);
        Assert.False(e1.MoveNext());
        Assert.False(e2.MoveNext());
        Assert.Equal(2, GetField(c, "FinallyRuns"));
    }

    // ── Task 10: Determinism ──

    [Fact]
    public void Determinism_SameInput_TwiceProducesIdenticalOutput()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int n) {
        for (int i = 0; i < n; i++)
            yield return i * 2;
    }
}";
        var (output1, _, _) = Rewrite(src);
        var (output2, _, _) = Rewrite(src);
        Assert.Equal(output1, output2);
    }

    [Fact]
    public void Determinism_CollisionWithUserType_AppendsSuffix()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 1; }
    private sealed class Cs2jIterator_M_0 { }
}";
        var (output, _, _) = Rewrite(src);

        Assert.Contains("Cs2jIterator_M_02", output);
    }

    // ── Task 6 (M3): using 归一（语句/声明/多资源/pattern/值类型/null） ──

    private const string M3ResourceTypes = @"
public sealed class CountingDisposable : System.IDisposable
{
    public int DisposeCount;
    public void Dispose() { DisposeCount++; }
}

public sealed class OrderEntry
{
    public System.Collections.Generic.List<string> Log;
    private readonly string _name;
    public OrderEntry(string name, System.Collections.Generic.List<string> log) { _name = name; Log = log; }
    public void Dispose() { Log.Add(_name); }
}

public sealed class PatternResource
{
    public int DisposeCount;
    public void Dispose() { DisposeCount++; }
}

public struct StructResource : System.IDisposable
{
    public static int DisposeCount;
    public void Dispose() { DisposeCount++; }
}
";

    [Fact]
    public void M3_UsingStatement_HasZeroCompileDiagnostics_AndTracksRegion()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(CountingDisposable r) {
        using (r) {
            yield return 1;
            yield return 2;
        }
    }
}" + M3ResourceTypes;
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        Assert.Equal(1, stats.UsingRegionsNormalized);
        Assert.Equal(1, stats.UsingResourcesHoisted);
        // using 已归一：输出树不再含 using/yield 令牌，释放走 IDisposable.Dispose，
        // 迭代器被状态机化（非原生 yield 假绿）。
        Assert.DoesNotContain("using (", output);
        Assert.DoesNotContain("yield", output);
        Assert.Contains("Cs2jIterator", output);
        Assert.Contains("System.IDisposable", output);
        Assert.Contains(".Dispose()", output);
    }

    [Fact]
    public void M3_UsingStatement_DisposesExactlyOnce_OnFullEnumeration()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(CountingDisposable r) {
        using (r) {
            yield return 1;
            yield return 2;
        }
    }
}" + M3ResourceTypes;
        var asm = LoadRewritten(src);
        var c = NewC(asm);
        var resourceType = asm.GetType("CountingDisposable")!;
        var resource = Activator.CreateInstance(resourceType)!;
        var (values, disposable) = EnumerateFully(asm, c, "M", resource);
        Assert.Equal(new object[] { 1, 2 }, values);
        Assert.Equal(1, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
        disposable.Dispose();
        Assert.Equal(1, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
    }

    [Fact]
    public void M3_UsingStatement_EarlyDispose_RunsFinallyExactlyOnce()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(CountingDisposable r) {
        using (r) {
            yield return 1;
            yield return 2;
            yield return 3;
        }
    }
}" + M3ResourceTypes;
        var asm = LoadRewritten(src);
        var c = NewC(asm);
        var resourceType = asm.GetType("CountingDisposable")!;
        var resource = Activator.CreateInstance(resourceType)!;
        var enumerator = ((IEnumerable)Invoke(asm, c, "M", null, resource)!).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(0, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
    }

    [Fact]
    public void M3_UsingDeclaration_ExtendsToBlockEnd_AndDisposes()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(CountingDisposable r) {
        using var resource = r;
        yield return 1;
        yield return 2;
    }
}" + M3ResourceTypes;
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        Assert.Equal(1, stats.UsingRegionsNormalized);

        var asm = LoadRewritten(src);
        var c = NewC(asm);
        var resourceType = asm.GetType("CountingDisposable")!;
        var resource = Activator.CreateInstance(resourceType)!;
        var (_, disposable) = EnumerateFully(asm, c, "M", resource);
        Assert.Equal(1, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
        disposable.Dispose();
        Assert.Equal(1, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
    }

    [Fact]
    public void M3_NestedUsing_DisposesInLifoOrder()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(List<string> log) {
        using (new OrderEntry(""a"", log)) {
            using (new OrderEntry(""b"", log)) {
                yield return 1;
            }
        }
    }
}" + M3ResourceTypes;
        // 表达式资源（new）被提为合成临时变量。
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        Assert.Equal(2, stats.UsingRegionsNormalized);
        Assert.Equal(2, stats.UsingResourcesHoisted);

        var asm = LoadRewritten(src);
        var c = NewC(asm);
        var log = new List<string>();
        EnumerateFully(asm, c, "M", log);
        Assert.Equal(new[] { "b", "a" }, log.ToArray());
    }

    [Fact]
    public void M3_NullResource_DoesNotThrow()
    {
        var src = @"
using System;
using System.Collections.Generic;
class C {
    public IEnumerable<int> M() {
        using (default(IDisposable)) {
            yield return 1;
        }
        yield return 2;
    }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        var (values, _) = EnumerateFully(LoadRewritten(src), "M");
        Assert.Equal(new object[] { 1, 2 }, values);
    }

    [Fact]
    public void M3_PatternBasedDispose_ClassWithoutInterface_CallsDirectly()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(PatternResource r) {
        using (r) {
            yield return 1;
        }
    }
}" + M3ResourceTypes;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        // pattern 路径不应强转 IDisposable，但保留 null 守卫。
        Assert.DoesNotContain("((System.IDisposable)", output);
        Assert.Contains("r.Dispose()", output);

        var asm = LoadRewritten(src);
        var c = NewC(asm);
        var resourceType = asm.GetType("PatternResource")!;
        var resource = Activator.CreateInstance(resourceType)!;
        EnumerateFully(asm, c, "M", resource);
        Assert.Equal(1, (int)resourceType.GetField("DisposeCount")!.GetValue(resource)!);
    }

    [Fact]
    public void M3_ValueTypeResource_DisposesWithoutNullGuard()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M() {
        using (var r = new StructResource()) {
            yield return 1;
        }
    }
}" + M3ResourceTypes;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        // 值类型：无 null 守卫、直接 Dispose（constrained，不装箱）。
        Assert.DoesNotContain("!= null", output);
        Assert.DoesNotContain("(System.IDisposable)", output);

        var asm = LoadRewritten(src);
        var staticCount = asm.GetType("StructResource")!
            .GetField("DisposeCount", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(0, (int)staticCount.GetValue(null)!);
        EnumerateFully(asm, "M");
        Assert.Equal(1, (int)staticCount.GetValue(null)!);
    }

    // ── Task 7 (M3): lock 内 yield（.NET 半区 Monitor 时序） ──

    [Fact]
    public void M3_LockStatement_EmitsCanonicalMonitorForm_AndCompiles()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(object gate) {
        lock (gate) {
            yield return 1;
            yield return 2;
        }
    }
}";
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        Assert.Equal(1, stats.LocksNormalized);
        Assert.Contains("System.Threading.Monitor.Enter", output);
        Assert.Contains("System.Threading.Monitor.Exit", output);
        Assert.DoesNotContain("yield", output);
        Assert.Contains("Cs2jIterator", output);
    }

    [Fact]
    public void M3_Lock_HeldAcrossYields_AndReleasedOnCompletion()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(object gate) {
        lock (gate) {
            yield return 1;
            yield return 2;
        }
    }
}";
        var asm = LoadRewritten(src);
        var c = NewC(asm);
        var gate = new object();
        var enumerator = ((IEnumerable)Invoke(asm, c, "M", null, gate)!).GetEnumerator();

        Assert.True(enumerator.MoveNext());
        // Monitor 是递归锁：同线程 TryEnter 永远成功，必须跨线程观测，
        // 并用同线程 IsEntered 佐证锁确实被状态机持有。
        Assert.True(System.Threading.Monitor.IsEntered(gate));
        Assert.False(TryEnterFromOtherThread(gate));
        Assert.True(enumerator.MoveNext());
        Assert.True(System.Threading.Monitor.IsEntered(gate));
        Assert.False(TryEnterFromOtherThread(gate));
        Assert.False(enumerator.MoveNext());
        Assert.False(System.Threading.Monitor.IsEntered(gate));
        Assert.True(TryEnterFromOtherThread(gate, 100));
    }

    [Fact]
    public void M3_Lock_ReleasedOnEarlyDispose()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(object gate) {
        lock (gate) {
            yield return 1;
            yield return 2;
        }
    }
}";
        var asm = LoadRewritten(src);
        var c = NewC(asm);
        var gate = new object();
        var enumerator = ((IEnumerable)Invoke(asm, c, "M", null, gate)!).GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.True(System.Threading.Monitor.IsEntered(gate));
        Assert.False(TryEnterFromOtherThread(gate));
        ((IDisposable)enumerator).Dispose();
        Assert.False(System.Threading.Monitor.IsEntered(gate));
        Assert.True(TryEnterFromOtherThread(gate, 100));
    }

    private static bool TryEnterFromOtherThread(object gate, int timeoutMs = 0)
    {
        var completed = new ManualResetEventSlim(false);
        var result = false;
        var thread = new Thread(() =>
        {
            var entered = false;
            try
            {
                entered = System.Threading.Monitor.TryEnter(gate, timeoutMs);
                result = entered;
            }
            finally
            {
                if (entered) System.Threading.Monitor.Exit(gate);
                completed.Set();
            }
        });
        thread.Start();
        completed.Wait(5000);
        return result;
    }

    [Fact]
    public void M3_RefStructUsingResource_IsRejected_WithCS2J0403_AndNotRewritten()
    {
        var src = @"
using System.Collections.Generic;
ref struct Box { public void Dispose() { } }
class C {
    public IEnumerable<int> M() {
        using (var b = new Box()) {
            yield return 1;
        }
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0403);
    }

    // ── M4a：受限局部（ref struct/ref local/指针）保留策略 ──

    [Fact]
    public void M4_SpanLocal_DoesNotCrossYield_IsKeptAsRealLocal_NotAField()
    {
        var src = @"
using System;
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int[] xs) {
        Span<int> span = xs.AsSpan();
        var first = span[0];
        yield return first;
    }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        // Span 保留为 MoveNext 真局部（不是状态机 class 字段）。
        Assert.Contains("Span<int> span = _cs2jCap_xs.AsSpan();", Flat(output));
        Assert.DoesNotContain("_cs2jLoc_span", output);
        // 同体内的常规局部仍提升。
        Assert.Contains("_cs2jLoc_first", output);
    }

    [Fact]
    public void M4_RefLocal_DoesNotCrossYield_IsKeptAsRealLocal()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int[] xs) {
        ref int r = ref xs[0];
        var v = r;
        yield return v;
    }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        Assert.Contains("ref int r = ref _cs2jCap_xs[0];", Flat(output));
        Assert.DoesNotContain("_cs2jLoc_r", output);
    }

    [Fact]
    public void M4_SpanLocal_AliveAcrossYield_IsRejected_CS2J0403()
    {
        var src = @"
using System;
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int[] xs) {
        Span<int> span = xs.AsSpan();
        yield return 0;
        var v = span[0];
        yield return v;
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0403);
    }

    [Fact]
    public void M4_SpanLocal_ReadInsideLaterYield_AfterEarlierSuspension_IsRejected()
    {
        var src = @"
using System;
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int[] xs) {
        Span<int> span = xs.AsSpan();
        yield return span[0];
        yield return span[1];
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0403);
    }

    [Fact]
    public void M4_SpanLocal_CreatedInsideLoopBody_EachIteration_CompilesAndRuns()
    {
        var src = @"
using System;
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(int[] xs) {
        for (var i = 0; i < xs.Length; i++) {
            Span<int> span = xs.AsSpan(i, 1);
            yield return span[0];
        }
    }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        Assert.DoesNotContain("_cs2jLoc_span", output);
        var (values, _) = EnumerateFully(LoadRewritten(src), "M", new[] { 5, 6 });
        Assert.Equal(new object[] { 5, 6 }, values);
    }

    [Fact]
    public void M4_AddressOfLocal_IsAlwaysRejected_CS2J0403()
    {
        var src = @"
using System.Collections.Generic;
class C {
    static unsafe System.Collections.Generic.IEnumerable<int> M(int[] xs) {
        int v = 1;
        int* p = &v;
        yield return v + *p;
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0403);
    }

    [Fact]
    public void M4_YieldInsideUnsafeBlock_IsRejected_CS2J0403()
    {
        var src = @"
using System.Collections.Generic;
class C {
    static unsafe System.Collections.Generic.IEnumerable<int> M() {
        unsafe { yield return 1; }
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0403);
    }

    [Fact]
    public void M4_YieldFreeUnsafeFixedBlock_PassesThrough_AndRuns()
    {
        var src = @"
using System.Collections.Generic;
class C {
    public static unsafe IEnumerable<int> M(int[] xs) {
        unsafe {
            fixed (int* q = xs) {
                *q += 1;
            }
        }
        yield return xs[0];
    }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        AssertOutputCompilesZeroDiagnostics(output);
        var (values, _) = EnumerateFully(LoadRewritten(src), "M", new[] { 41 });
        Assert.Equal(new object[] { 42 }, values);
    }

    // ── M4a：含 yield 的 switch 展平 ──

    [Fact]
    public void M4_SwitchWithYield_FlattensToDispatchAndLabels_AndCompiles()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int k) {
        switch (k) {
            case 1: yield return 10; break;
            case 2: yield return 20; yield return 21; break;
            default: yield return 99; break;
        }
    }
}";
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.Equal(1, stats.IteratorMembersLowered);
        AssertOutputCompilesZeroDiagnostics(output);
        Assert.DoesNotContain("yield", output);
        // 保留只做转发的 switch；参数 k 已提升为字段。
        Assert.Contains("switch (_cs2jCap_k)", Flat(output));
    }

    [Fact]
    public void M4_SwitchWithYield_RuntimeDispatch_AllSections()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(int k) {
        switch (k) {
            case 1:
                yield return 10;
                break;
            case 2:
                yield return 20;
                yield return 21;
                break;
            default:
                yield return 99;
                break;
        }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        Assert.Equal(new object[] { 10 }, EnumerateFully(asm, c, "M", 1).values);
        Assert.Equal(new object[] { 20, 21 }, EnumerateFully(asm, c, "M", 2).values);
        Assert.Equal(new object[] { 99 }, EnumerateFully(asm, c, "M", 7).values);
    }

    [Fact]
    public void M4_Switch_EmptySectionFallthrough_AndSwitchInsideLoop()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(int[] xs) {
        foreach (var v in xs) {
            switch (v) {
                case 0:
                case 1:
                    yield return v * 100;
                    break;
                default:
                    yield return -v;
                    break;
            }
        }
    }
}";
        var asm = LoadRewritten(code);
        var (values, _) = EnumerateFully(asm, "M", (object)new[] { 0, 1, 2, 3 });
        Assert.Equal(new object[] { 0, 100, -2, -3 }, values);
    }

    [Fact]
    public void M4_SwitchBreak_InsideTry_FinallyRunsOnce()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int Fin;
    public IEnumerable<int> M(int k) {
        try {
            switch (k) {
                case 1: yield return 1; break;
                default: yield return 2; break;
            }
        } finally { Fin++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, _) = EnumerateFully(asm, c, "M", 5);
        Assert.Equal(new object[] { 2 }, values);
        // break 只跳出 switch，不跨 finally 区域；finally 在整段结束时一次。
        Assert.Equal(1, GetField(c, "Fin"));
    }

    [Fact]
    public void M4_SwitchBindingPatternVariable_WithYield_IsRejected_CS2J0402()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(object x) {
        switch (x) {
            case int y:
                yield return y;
                break;
        }
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0402);
    }

    [Fact]
    public void M4_SwitchSection_WithOutVarInBody_FlattensAndRuns()
    {
        var code = @"
using System;
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(int k, string s) {
        switch (k) {
            case 1:
                if (int.TryParse(s, out var n))
                    yield return n;
                else
                    yield return -1;
                break;
            default:
                yield return 0;
                break;
        }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        Assert.Equal(new object[] { 7 }, EnumerateFully(asm, c, "M", 1, "7").values);
        Assert.Equal(new object[] { -1 }, EnumerateFully(asm, c, "M", 1, "x").values);
        Assert.Equal(new object[] { 0 }, EnumerateFully(asm, c, "M", 9, "7").values);
    }

    [Fact]
    public void M4_GotoCase_WithYield_IsRejected_CS2J0406()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int x) {
        switch (x) {
            case 1:
                yield return 1;
                goto case 2;
            case 2:
                yield return 2;
                break;
        }
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0406);
    }

    // ── M4a：残留 lambda 防御网 + struct this 按值捕获 ──

    [Fact]
    public void M4_ResidualLambda_IsRejected_CS2J0405()
    {
        var src = @"
using System;
using System.Collections.Generic;
class C {
    IEnumerable<int> M() {
        Func<int> f = () => 1;
        yield return f();
    }
}";
        var model = ParseWithSemantics(src);
        var result = new IteratorLoweringPreprocessor().Preprocess(src, model, new IteratorLoweringOptions());
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0405);
    }

    [Fact]
    public void M4_StructIterator_CapturesThisByValue_OuterStructUntouched()
    {
        var code = @"
using System.Collections.Generic;
struct C {
    public int Calls;
    public IEnumerable<int> M() {
        Calls++;
        yield return Calls;
        Calls++;
        yield return Calls;
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var (values, _) = EnumerateFully(asm, c, "M");
        // 状态机持 this 的值拷贝：序列 1,2 落在拷贝上，外层结构体保持 0。
        Assert.Equal(new object[] { 1, 2 }, values);
        Assert.Equal(0, GetField(c, "Calls"));
    }

    [Fact]
    public void M4b_BareReturn_InIterator_LowersAndEndsEnumeration()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(int stop) {
        for (int i = 0; i < 10; i++) {
            if (i == stop) return;
            yield return i;
        }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        // 裸 return; 等价 yield break：立即结束枚举（不产出 stop）。
        Assert.Equal(new object[] { 0, 1, 2 }, EnumerateFully(asm, c, "M", 3).values);
        Assert.Equal(new object[] { 0, 1, 2, 3, 4 }, EnumerateFully(asm, c, "M", 5).values);
        // 不触发时遍历完整。
        Assert.Equal(new object[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 },
            EnumerateFully(asm, c, "M", 100).values);
    }

    [Fact]
    public void M4b_BareReturn_InsideTryRegion_StillRunsFinally()
    {
        var code = @"
using System.Collections.Generic;
class C {
    public int Fin;
    public IEnumerable<int> M(int stop) {
        try {
            for (int i = 0; i < 10; i++) {
                if (i == stop) return;
                yield return i;
            }
        } finally { Fin++; }
    }
}";
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        Assert.Equal(new object[] { 0, 1 }, EnumerateFully(asm, c, "M", 2).values);
        // 归一为 yield break 后走区域清理链：finally 恰好一次。
        Assert.Equal(1, GetField(c, "Fin"));
    }

    [Fact]
    public void M4b_QualifiedInstanceMemberAccess_RightmostNameNotRewritten()
    {
        // csharp-sdk M4b 画像：x.Foo 右侧的 Foo 与裸 Foo 解析到同一拥有者
        // 实例成员；旧代码把它改写成 _cs2jThis.Foo，既改目标又在默认
        // VisitMemberAccessExpression 里触发 SimpleNameSyntax 转换崩溃。
        var code = @"
using System.Collections.Generic;
class C {
    public int Value;
    public IEnumerable<int> M(C other) {
        if (other.Value > 0) yield return other.Value;
        yield return Value;
    }
}";
        var asm = LoadRewritten(code);
        var other = NewC(asm);
        SetField(other, "Value", 5);
        var self = NewC(asm);
        SetField(self, "Value", 7);
        Assert.Equal(new object[] { 5, 7 }, EnumerateFully(asm, self, "M", other).values);
    }

    [Fact]
    public void M4b_NonIteratorOuter_WithIteratorLocalFunction_KeepsBareReturn()
    {
        // 外层方法自身不含 yield，仅嵌套了一个迭代器局部函数：外层裸
        // return; 不得被误归一为 yield break（否则 CS1631）。
        var code = @"
using System.Collections.Generic;
class C {
    public int M(int stop) {
        IEnumerable<int> It() {
            yield return 1;
            yield return 2;
        }
        if (stop == 0) return 0;
        int sum = 0;
        foreach (var x in It()) sum += x;
        return stop + sum;
    }
}";
        var (output, stats, _) = Rewrite(code);
        Assert.Equal(0, stats.IteratorMembersLowered);
        Assert.Contains("return 0;", output);
        var asm = LoadRewritten(code);
        var c = NewC(asm);
        var m = asm.GetType("C")!.GetMethod("M")!;
        Assert.Equal(0, m.Invoke(c, new object[] { 0 }));
        Assert.Equal(8, m.Invoke(c, new object[] { 5 }));
    }

    [Fact]
    public void M4b_BareReturn_InCtorAndEventAccessor_AlongsideIterator_IsUntouched()
    {
        // 同一文件里有迭代器方法（通过脏检查），但构造函数与事件 add
        // 访问器中的裸 return; 属于它们自己的例程，不得归一为 yield break
        // （否则 CS1631，输出无法编译）。
        var code = @"
using System;
using System.Collections.Generic;
class C {
    public int Adds;
    public bool Blocked;
    public C(int stop) {
        if (stop == 0) return;
        Blocked = true;
    }
    public event Action E {
        add { if (Blocked) return; Adds++; }
        remove { }
    }
    public IEnumerable<int> M() {
        yield return 1;
        yield return 2;
    }
}";
        var (output, _, _) = Rewrite(code);
        // 两处裸 return 原样保留。
        Assert.Contains("if (stop == 0) return;", output);
        Assert.Contains("if (Blocked) return;", output);
        var asm = LoadRewritten(code);
        var open = Activator.CreateInstance(asm.GetType("C")!, 0);
        var blocked = Activator.CreateInstance(asm.GetType("C")!, 1);
        var ev = asm.GetType("C")!.GetEvent("E")!;
        var handler = new Action(() => { });
        ev.AddEventHandler(open, handler);
        ev.AddEventHandler(blocked, handler);
        Assert.Equal(1, GetField(open, "Adds"));
        Assert.Equal(0, GetField(blocked, "Adds"));
    }

    [Fact]
    public void NonGenericIEnumerator_ExplicitInterfaceImplementation_SkipsIEnumerableBase()
    {
        // GraphLayout 画像：类实现 IEnumerable<T>，显式 IEnumerator
        // IEnumerable.GetEnumerator() 体内有 yield。状态机返回形态是非泛型
        // IEnumerator，只能实现 IEnumerator（+IDisposable）；同时加
        // IEnumerable 会缺 GetEnumerator() 而 CS0535。
        var src = @"
using System.Collections;
using System.Collections.Generic;
class C : IEnumerable<int> {
    public IEnumerator<int> GetEnumerator() { yield return 1; }
    IEnumerator IEnumerable.GetEnumerator() {
        yield return 2;
    }
    public IEnumerable<int> M() { yield return 3; }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);

        var root = CSharpSyntaxTree.ParseText(output, ParseOptions).GetCompilationUnitRoot();
        var explicitGetEnumerator = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Parent is ClassDeclarationSyntax { Identifier.ValueText: "C" }
                && m.ExplicitInterfaceSpecifier?.Name.ToString()
                    .EndsWith("IEnumerable", StringComparison.Ordinal) == true);
        var machineName = Regex.Match(
            Flat(explicitGetEnumerator.Body!.ToString()),
            @"new (Cs2jIterator_\w+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(machineName));

        var machines = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText.StartsWith("Cs2jIterator_", StringComparison.Ordinal))
            .ToList();
        var enumeratorMachine = machines.Single(c => c.Identifier.ValueText == machineName);
        var enumeratorBases = enumeratorMachine.BaseList!.Types
            .Select(t => Flat(t.Type.ToString())).ToList();
        Assert.Contains("System.Collections.IEnumerator", enumeratorBases);
        Assert.Contains("System.IDisposable", enumeratorBases);
        Assert.DoesNotContain("System.Collections.IEnumerable", enumeratorBases);

        // 泛型 IEnumerable<int> 迭代器仍实现泛型双目 + 非泛型 IEnumerator。
        var genericMachine = machines.Single(c => c.BaseList!.Types
            .Any(t => Flat(t.Type.ToString()) == "System.Collections.Generic.IEnumerable<int>"));
        var genericBases = genericMachine.BaseList!.Types
            .Select(t => Flat(t.Type.ToString())).ToList();
        Assert.Contains("System.Collections.Generic.IEnumerable<int>", genericBases);
        Assert.Contains("System.Collections.Generic.IEnumerator<int>", genericBases);
        Assert.Contains("System.Collections.IEnumerator", genericBases);

        AssertOutputCompilesZeroDiagnostics(output);
    }

    [Fact]
    public void ByRefAccessorLambdas_InIterator_NoResidualLambdaDiagnostic()
    {
        // C01 ref-out-in 生成的 ByRef<T>.Create(() => x, v => x = v)
        // 脚手架 lambda 属于基础设施，不是跨 yield 捕获，不得触发 CS2J0405。
        var src = @"
namespace Cs2jByRef {
    public class ByRef<T> {
        public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter) => new ByRef<T>();
        public T Value;
    }
}
class C {
    public System.Collections.Generic.IEnumerable<char> M() {
        char prev = 'w';
        yield return 'M';
        Cs2jByRef.ByRef<char> r = Cs2jByRef.ByRef<char>.Create(() => prev, v => prev = v);
        r.Value = 'L';
        yield return prev;
    }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0405);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        // 脚手架 lambda 原样保留（不提升、不删除）。
        Assert.Contains("Create(() =>", Flat(output));
        AssertOutputCompilesZeroDiagnostics(output);

        // 普通用户 lambda 的残余拒绝行为保持不变。
        var plain = @"
using System.Collections.Generic;
class D {
    public IEnumerable<int> M() {
        System.Func<int> f = () => 1;
        yield return f();
    }
}";
        var (_, _, plainDiags) = Rewrite(plain);
        Assert.Contains(plainDiags, d => d.Code == IteratorLoweringDiagnosticCode.CS2J0405);
    }

    [Fact]
    public void ForeachOverExplicitInterfaceEnumerator_EmitsInterfaceCast()
    {
        // C# foreach 模式允许接收者只提供显式接口 GetEnumerator；展平后直接
        // `expr.GetEnumerator()` 会 CS1061（GraphLayout 的 Polyline 画像）。
        var src = @"
using System.Collections;
using System.Collections.Generic;
class C {
    public IEnumerable<int> M(P p) {
        foreach (var x in p) yield return x;
    }
}
class P : IEnumerable<int> {
    public int[] Items = { 1, 2 };
    IEnumerator<int> IEnumerable<int>.GetEnumerator() {
        foreach (var x in Items) yield return x;
    }
    IEnumerator IEnumerable.GetEnumerator() { return ((IEnumerable<int>)this).GetEnumerator(); }
}";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        Assert.Contains("System.Collections.Generic.IEnumerable<int>)", Flat(output));
        AssertOutputCompilesZeroDiagnostics(output);
    }

    // ── Helpers: parsing / analysis / rewriting ──

    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ");

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    internal static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "il-test-" + Guid.NewGuid().ToString("N"),
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
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static MethodLoweringInfo AnalyzeBody(string src)
    {
        var model = ParseWithSemantics(src);
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var symbol = (IMethodSymbol)model.GetDeclaredSymbol(method)!;
        return IteratorBodyAnalysis.Analyze(method, symbol, model, "Cs2jIterator_M_0");
    }

    internal static (string Output, IteratorLoweringStatistics Stats,
        List<IteratorLoweringDiagnostic> Diags) Rewrite(string src)
    {
        var originalModel = ParseWithSemantics(src);
        var originalRoot = (CompilationUnitSyntax)originalModel.SyntaxTree.GetRoot();

        var normalizer = new IteratorResourceNormalizer(originalModel);
        var normalizedRoot = normalizer.Normalize(originalRoot);
        // M4b: mirror the production pipeline — bare `return;` in iterators is
        // normalized to yield break after the model-bound resource normalization.
        var bareReturnRewriter = new BareReturnToYieldBreakRewriter();
        normalizedRoot = (CompilationUnitSyntax)bareReturnRewriter.Visit(normalizedRoot)!;
        var model = originalModel;
        var rewriteRoot = normalizedRoot;
        if (bareReturnRewriter.Changed
            || normalizer.UsingRegionsNormalized > 0
            || normalizer.LocksNormalized > 0
            || normalizer.Diagnostics.Count > 0)
        {
            var normalizedTree = CSharpSyntaxTree.Create(
                normalizedRoot,
                (CSharpParseOptions?)originalRoot.SyntaxTree.Options,
                originalRoot.SyntaxTree.FilePath,
                System.Text.Encoding.UTF8);
            var replacement = ((CSharpCompilation)originalModel.Compilation)
                .ReplaceSyntaxTree(originalModel.SyntaxTree, normalizedTree);
            model = replacement.GetSemanticModel(normalizedTree);
            // CSharpSyntaxTree.Create clones the root: Analyzer/Rewriter node
            // identity must share the tree's own root instance.
            rewriteRoot = normalizedTree.GetCompilationUnitRoot();
        }

        var analyzerResult = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());
        var rewriter = new IteratorStateMachineRewriter(model, analyzerResult);
        var newRoot = rewriter.Rewrite(rewriteRoot);
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var output = formatted.ToFullString();
        var stats = new IteratorLoweringStatistics
        {
            IteratorMembersLowered = analyzerResult.Plans.Count,
            StateMachinesGenerated = analyzerResult.Plans.Count,
            YieldReturnSites = analyzerResult.Plans.Sum(p => p.YieldReturnCount),
            YieldBreakSites = analyzerResult.Plans.Count(p => p.HasYieldBreak),
            HoistedParameters = analyzerResult.Plans.Sum(p => p.Parameters.Count),
            HoistedLocals = analyzerResult.Plans.Sum(p => p.HoistedLocals.Count),
            HoistedEnumerators = analyzerResult.Plans.Sum(p => p.HoistedEnumerators.Count),
            FinallyRegions = analyzerResult.Plans.Sum(p => p.Regions.Count),
            UsingRegionsNormalized = normalizer.UsingRegionsNormalized,
            UsingResourcesHoisted = normalizer.UsingResourcesHoisted,
            LocksNormalized = normalizer.LocksNormalized,
            SkippedUnsupported = analyzerResult.Diagnostics.Count,
        };
        return (output, stats, analyzerResult.Diagnostics.ToList());
    }

    private static void AssertOutputCompilesZeroDiagnostics(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "il-probe-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();
        Assert.True(diagnostics.Count == 0, "rewritten output has compile diagnostics: " +
            string.Join("; ", diagnostics.Select(e => e.ToString())));
    }

    // ── Helpers: 反射运行 ──

    private const string TrackedEnumerableSource = @"
public sealed class TrackedEnumerable : IEnumerable<int>
{
    public int DisposeCount;
    private readonly int[] _data;
    public TrackedEnumerable(params int[] data) { _data = data; }
    public IEnumerator<int> GetEnumerator() { return new E(this, _data); }
    IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    private sealed class E : IEnumerator<int>
    {
        private readonly TrackedEnumerable _owner;
        private readonly int[] _data;
        private int _index = -1;
        public E(TrackedEnumerable owner, int[] data) { _owner = owner; _data = data; }
        public int Current => _data[_index];
        object IEnumerator.Current => Current;
        public bool MoveNext() { ++_index; return _index < _data.Length; }
        public void Reset() { throw new NotSupportedException(); }
        public void Dispose() { _owner.DisposeCount++; }
    }
}";

    private static Assembly LoadRewritten(string src)
    {
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain(diags, d => d.Severity == IteratorLoweringSeverity.Error);
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var parseErrors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(parseErrors.Count == 0, "rewritten output has parse errors: " +
            string.Join("; ", parseErrors));
        var compilation = CSharpCompilation.Create(
            "il-asm-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success, "emit failed: " +
            string.Join("; ", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())));
        return Assembly.Load(pe.ToArray());
    }

    private static object NewC(Assembly asm)
        => Activator.CreateInstance(asm.GetType("C")!)!;

    private static object? Invoke(
        Assembly asm, object target, string method, Type[]? genericArgs = null, params object?[] args)
    {
        var mi = asm.GetType("C")!.GetMethod(method)!;
        if (genericArgs != null)
        {
            mi = mi.MakeGenericMethod(genericArgs);
        }
        return mi.Invoke(target, args);
    }

    private static (List<object?> values, IDisposable disposable) EnumerateFully(
        Assembly asm, string method, params object?[] args)
        => EnumerateFully(asm, NewC(asm), method, args);

    private static (List<object?> values, IDisposable disposable) EnumerateFully(
        Assembly asm, object c, string method, params object?[] args)
    {
        var result = Invoke(asm, c, method, null, args)!;
        var enumerator = ((IEnumerable)result).GetEnumerator();
        var values = ToList(enumerator);
        return (values, (IDisposable)enumerator);
    }

    private static List<object?> ToList(IEnumerator enumerator)
    {
        var values = new List<object?>();
        while (enumerator.MoveNext())
        {
            values.Add(enumerator.Current);
        }
        return values;
    }

    private static List<object?> ToList(IEnumerable enumerable)
        => ToList(enumerable.GetEnumerator());

    private static object? GetField(object obj, string name)
        => obj.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(obj);

    private static void SetField(object obj, string name, object? value)
        => obj.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(obj, value);
}
