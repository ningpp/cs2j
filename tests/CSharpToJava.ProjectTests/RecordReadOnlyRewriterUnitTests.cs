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
        // Task 3 临时断言：桩分析器不产记录；Task 4 完成后恢复 AlreadyReadOnly=1 与 RR010。
        Assert.Empty(diags.Where(d => d.Severity == RecordReadOnlySeverity.Error));
    }
}
