using CSharpToJava.Core.IteratorLowering;
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
        Assert.Equal("_cs2jCap_count", IteratorLoweringNaming.GetCaptureFieldName("count"));
        Assert.Equal("_cs2jLoc_i", IteratorLoweringNaming.GetLocalFieldName("i"));
    }

    [Fact]
    public void Naming_ResumeLabel_IsPrefixed()
    {
        Assert.Equal("_cs2jResume_0", IteratorLoweringNaming.GetResumeLabel(0));
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

    // ── Task 3: StateMachinePlan ──

    [Fact]
    public void StateMachinePlan_Stores_BasicFields()
    {
        var plan = new StateMachinePlan(
            MemberName: "GetTokens",
            StateMachineClassName: "Cs2jIterator_GetTokens_0",
            ElementType: "string",
            IsIEnumerableReturn: true,
            IsIEnumeratorReturn: false,
            IsGenericMethod: false,
            DeclaringTypeName: "Writer",
            IsStatic: false,
            Parameters: new[] { new CaptureField("count", "int", "_cs2jCap_count") },
            HoistedLocals: new[] { new CaptureField("i", "int", "_cs2jLoc_i") },
            YieldReturnCount: 1,
            HasYieldBreak: false);

        Assert.Equal("GetTokens", plan.MemberName);
        Assert.Equal("Cs2jIterator_GetTokens_0", plan.StateMachineClassName);
        Assert.Equal("string", plan.ElementType);
        Assert.True(plan.IsIEnumerableReturn);
        Assert.False(plan.IsIEnumeratorReturn);
        Assert.Single(plan.Parameters);
        Assert.Single(plan.HoistedLocals);
        Assert.Equal(1, plan.YieldReturnCount);
    }

    [Fact]
    public void CaptureField_Records_Name_Type_FieldName()
    {
        var cf = new CaptureField("count", "int", "_cs2jCap_count");
        Assert.Equal("count", cf.OriginalName);
        Assert.Equal("int", cf.Type);
        Assert.Equal("_cs2jCap_count", cf.FieldName);
    }

    // ── Task 4: CfgBuilder ──

    [Fact]
    public void CfgBuilder_AssignsSequentialStates_ToYieldReturns()
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
        var model = ParseWithSemantics(src);
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var info = CSharpToJava.Core.IteratorLowering.ControlFlow.CfgBuilder.Analyze(method, model);

        Assert.Equal(3, info.YieldReturnCount);
        Assert.Equal(3, info.ResumeStates.Count);
        Assert.Equal(1, info.ResumeStates[0]);
        Assert.Equal(2, info.ResumeStates[1]);
        Assert.Equal(3, info.ResumeStates[2]);
    }

    [Fact]
    public void CfgBuilder_DetectsYieldBreak()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(bool early) {
        if (early) yield break;
        yield return 1;
    }
}";
        var model = ParseWithSemantics(src);
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var info = CSharpToJava.Core.IteratorLowering.ControlFlow.CfgBuilder.Analyze(method, model);

        Assert.True(info.HasYieldBreak);
        Assert.Equal(1, info.YieldReturnCount);
    }

    [Fact]
    public void CfgBuilder_HoistsLoopVariable_WhenYieldIsInsideLoop()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M(int count) {
        for (int i = 0; i < count; i++)
            yield return i;
    }
}";
        var model = ParseWithSemantics(src);
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var info = CSharpToJava.Core.IteratorLowering.ControlFlow.CfgBuilder.Analyze(method, model);

        Assert.Contains(info.HoistedLocalNames, n => n == "i");
        Assert.Contains(info.HoistedParameterNames, n => n == "count");
    }

    // ── Task 5: Analyzer ──

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

    // ── Task 6: Rewriter ──

    [Fact]
    public void Rewriter_SimpleYieldReturn_ProducesStateMachine()
    {
        var src = @"
using System.Collections.Generic;
class C {
    IEnumerable<int> M() { yield return 42; }
}";
        var (output, stats, diags) = Rewrite(src);

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

        Assert.Contains("_cs2jResume_0", output);
        Assert.Contains("_cs2jResume_1", output);
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
    public void Rewriter_ForLoop_PreservesLoopStructure()
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

    // ── Helpers ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    internal static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "il-test",
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

    internal static (string Output, IteratorLoweringStatistics Stats,
        List<IteratorLoweringDiagnostic> Diags) Rewrite(string src)
    {
        var model = ParseWithSemantics(src);
        var analyzerResult = IteratorLoweringAnalyzer.Analyze(model, new IteratorLoweringOptions());
        var rewriter = new IteratorStateMachineRewriter(model, analyzerResult);
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var newRoot = rewriter.Rewrite(root);
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
            SkippedUnsupported = analyzerResult.Diagnostics.Count,
        };
        return (output, stats, analyzerResult.Diagnostics.ToList());
    }
}
