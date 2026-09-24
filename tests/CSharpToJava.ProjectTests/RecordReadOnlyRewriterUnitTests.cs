using CSharpToJava.Core.RecordReadOnly;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

public class RecordReadOnlyRewriterUnitTests
{
    private readonly ITestOutputHelper _output;
    public RecordReadOnlyRewriterUnitTests(ITestOutputHelper output) => _output = output;

    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    internal static SemanticModel ParseWithSemantics(string src, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "rr-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    internal static (string Output, RecordReadOnlyStatistics Stats,
        List<RecordReadOnlyDiagnostic> Diagnostics) Rewrite(string src,
        RecordReadOnlyOptions? options = null, params string[] extra)
    {
        var model = ParseWithSemantics(src, extra);
        var pp = new RecordReadOnlyPreprocessor(options ?? new RecordReadOnlyOptions());
        var stats = new RecordReadOnlyStatistics();
        var diags = new List<RecordReadOnlyDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
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

    internal static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("rr-verify", new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    [Fact]
    public void Models_DefaultCounters_AreZero()
    {
        var stats = new RecordReadOnlyStatistics();
        Assert.Equal(0, stats.RecordsScanned);
        Assert.Equal(0, stats.RecordStructsConverted);
        Assert.Equal(0, stats.PlansRolledBack);
        var d = new RecordReadOnlyDiagnostic(
            RecordReadOnlySeverity.Warning, RecordReadOnlyDiagnosticCode.RR020,
            "record skipped", "Lib/R.cs", 7, 3, "Lib.R");
        Assert.Equal("RR020", d.Code.ToString());
        Assert.Equal("Lib/R.cs(7,3): warning RR020: [Lib.R] record skipped", d.ToMsBuildString());
    }

    [Fact]
    public void CleanFile_NoRecord_Unchanged()
    {
        var src = "public class C { public int X; public int Add(int a, int b) => a + b; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.FilesTransformed);
    }

    [Fact]
    public void AlreadyReadonlyRecordStruct_CountedAndUnchanged()
    {
        var src = "public readonly record struct P(int X, int Y);";
        var (output, stats, diags) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(1, stats.AlreadyReadOnly);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR010);
    }

    [Fact]
    public void AlreadyReadonlyRecordStruct_EmitsRR010()
    {
        var src = "public readonly record struct P(int X, int Y);";
        var (output, stats, diags) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(1, stats.AlreadyReadOnly);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR010);
    }

    [Fact]
    public void Analyzer_MergesPartialParts_AndClassifiesMembers()
    {
        var src = """
            public partial record struct P
            {
                public int A { get; set; }
                private int _b;
                public int C;
                public event System.Action E;
            }
            public partial record struct P { public int D { get; } }
            """;
        var model = ParseWithSemantics(src);
        var pp = new RecordReadOnlyPreprocessor();
        pp.BindCompilation(model.Compilation);
        var plan = Assert.Single(pp.Plans);
        Assert.Equal("P", plan.RecordQualifiedName);
        // A(set)、C(字段) 需改写；_b 私有字段分类；E 事件阻断。事实在计划可行性上体现：
        Assert.Contains(plan.MemberRewrites, m => m.MemberName == "A");
        Assert.Contains(plan.MemberRewrites, m => m.MemberName == "C");
        Assert.False(plan.Feasible);
        Assert.Equal(RecordReadOnlyDiagnosticCode.RR040, plan.FailCode);
    }

    [Fact]
    public void Analyzer_RecordClass_IgnoredWhenClassConversionDisabled()
    {
        var src = "public record class P(int X);";
        var (output, _, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = false });
        Assert.Equal(src, output);
    }

    [Fact]
    public void RS1_PositionalRecordStruct_AddsReadonly_AndCompiles()
    {
        var src = "public record struct P(int X, int Y);";
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        Assert.DoesNotContain("readonly readonly", output);
        Assert.Equal(1, stats.RecordStructsConverted);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR001);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS1_RecordStructWithGetOnlyBody_AddsReadonly()
    {
        var src = """
            public record struct P(int X)
            {
                public int Y { get; }
                public int Sum() => X + Y;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS2_AllPartialParts_GetReadonly()
    {
        var src = """
            public partial record struct P { public int X { get; } }
            public partial record struct P { public int Y { get; } }
            """;
        var (output, stats, _) = Rewrite(src);
        // readonly 必须在 partial 之前（CS1585），所以输出为 "readonly partial record struct P"。
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(output, "readonly partial record struct P").Count);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MutableMembers_BeforeMemberRewriteMilestone_PlanInfeasibleRR020()
    {
        var src = "public record struct P { public int X { get; set; } }";
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output); // 原子跳过，不部分转换
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }
}
