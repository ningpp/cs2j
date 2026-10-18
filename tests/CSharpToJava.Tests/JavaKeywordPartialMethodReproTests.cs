using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the SDK JK030 failure using actual process-directory files
/// from D:\aacs-java-2610-sdk\.cs2j-java-keywords-src.
/// </summary>
public class JavaKeywordPartialMethodReproTests
{
    private readonly ITestOutputHelper _output;
    public JavaKeywordPartialMethodReproTests(ITestOutputHelper output) => _output = output;

    private const string SdkRoot = @"D:\aacs-java-2610-sdk";
    private const string RegExTestsDir = SdkRoot + @"\.cs2j-java-keywords-src\regular-expressions\tests";

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

    [Fact]
    public void SdkFiles_PartialMethod_Throws_AllRenamedSame_NoNewCS0759()
    {
        // Skip if the SDK process directory doesn't exist (e.g. on CI).
        if (!Directory.Exists(RegExTestsDir))
        {
            _output.WriteLine("SDK process directory not found, skipping.");
            return;
        }

        var defFile = Path.Combine(RegExTestsDir, "RegexParserTests.cs");
        var impl1File = Path.Combine(RegExTestsDir, "RegexParserTests.netcoreapp.cs");
        var impl2File = Path.Combine(RegExTestsDir, "RegexParserTests.netfx.cs");

        var defCode = File.ReadAllText(defFile);
        var impl1Code = File.ReadAllText(impl1File);
        var impl2Code = File.ReadAllText(impl2File);

        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var defTree = CSharpSyntaxTree.ParseText(defCode, options, path: defFile);
        var impl1Tree = CSharpSyntaxTree.ParseText(impl1Code, options, path: impl1File);
        var impl2Tree = CSharpSyntaxTree.ParseText(impl2Code, options, path: impl2File);

        // Build the compilation just like JavaKeywordsPassAdapter does.
        var compilation = CSharpCompilation.Create("jk-sdk-test",
            new[] { defTree, impl1Tree, impl2Tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Capture baseline errors (before rename).
        var baselineErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null)
            .Select(d => (
                File: d.Location.SourceTree!.FilePath,
                Id: d.Descriptor.Id,
                Message: d.GetMessage()))
            .ToHashSet();

        _output.WriteLine($"Baseline errors: {baselineErrors.Count}");
        foreach (var e in baselineErrors.Take(10))
            _output.WriteLine($"  {e.File}: {e.Id} {e.Message}");

        // Run the preprocessor.
        var pp = new JavaReservedWordPreprocessor();
        pp.BindCompilation(compilation);
        var stats = new JavaKeywordStatistics();
        var diags = new List<JavaKeywordDiagnostic>();

        // Rewrite all three files.
        var defModel = compilation.GetSemanticModel(defTree);
        var defResult = pp.RewriteFile(defCode, defModel, stats, diags);

        var impl1Model = compilation.GetSemanticModel(impl1Tree);
        var impl1Result = pp.RewriteFile(impl1Code, impl1Model, stats, diags);

        var impl2Model = compilation.GetSemanticModel(impl2Tree);
        var impl2Result = pp.RewriteFile(impl2Code, impl2Model, stats, diags);

        _output.WriteLine($"Stats: SymbolsRenamed={stats.SymbolsRenamed}, PlansRolledBack={stats.PlansRolledBack}");
        foreach (var d in diags)
            _output.WriteLine($"Diag: {d.Code} {d.Message}");

        // Show the partial method declarations in each rewritten file.
        _output.WriteLine("--- DEF output (Throws lines) ---");
        foreach (var line in defResult.OutputCode.Split('\n').Where(l => l.Contains("Throws")))
            _output.WriteLine($"  DEF: {line.Trim()}");
        _output.WriteLine("--- IMPL1 output (Throws lines) ---");
        foreach (var line in impl1Result.OutputCode.Split('\n').Where(l => l.Contains("Throws")))
            _output.WriteLine($"  IMPL1: {line.Trim()}");
        _output.WriteLine("--- IMPL2 output (Throws lines) ---");
        foreach (var line in impl2Result.OutputCode.Split('\n').Where(l => l.Contains("Throws")))
            _output.WriteLine($"  IMPL2: {line.Trim()}");

        // Verify all three files were renamed.
        Assert.Contains("ThrowsValue", defResult.OutputCode);
        Assert.Contains("ThrowsValue", impl1Result.OutputCode);
        Assert.Contains("ThrowsValue", impl2Result.OutputCode);

        // Verify no Throws( remains.
        Assert.DoesNotContain("partial void Throws(", defResult.OutputCode);
        Assert.DoesNotContain("partial void Throws(", impl1Result.OutputCode);
        Assert.DoesNotContain("partial void Throws(", impl2Result.OutputCode);

        // Verify the rewritten files don't introduce NEW CS0759 errors.
        var rewrittenDef = CSharpSyntaxTree.ParseText(defResult.OutputCode, options, path: defFile);
        var rewrittenImpl1 = CSharpSyntaxTree.ParseText(impl1Result.OutputCode, options, path: impl1File);
        var rewrittenImpl2 = CSharpSyntaxTree.ParseText(impl2Result.OutputCode, options, path: impl2File);

        var candidate = CSharpCompilation.Create("jk-sdk-verify",
            new[] { rewrittenDef, rewrittenImpl1, rewrittenImpl2 },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Diff errors: new errors = errors not in baseline (by File+Id for CS0759).
        var candidateErrors = candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null)
            .ToList();

        var baselineFileId = new HashSet<(string, string)>(
            baselineErrors.Select(e => (e.File, e.Id)));

        var newErrors = candidateErrors
            .Where(d => !baselineErrors.Contains((
                d.Location.SourceTree!.FilePath,
                d.Descriptor.Id,
                d.GetMessage()))
                && !(d.Descriptor.Id == "CS0759"
                     && baselineFileId.Contains((
                         d.Location.SourceTree!.FilePath,
                         d.Descriptor.Id))))
            .ToList();

        _output.WriteLine($"Candidate errors: {candidateErrors.Count}, New errors: {newErrors.Count}");
        foreach (var e in newErrors.Take(10))
            _output.WriteLine($"  NEW: {e.Location.SourceTree!.FilePath}: {e.Descriptor.Id} {e.GetMessage()}");

        // Show baseline CS0759 entries
        var baselineCS0759 = baselineErrors.Where(e => e.Id == "CS0759").ToList();
        _output.WriteLine($"Baseline CS0759 count: {baselineCS0759.Count}");
        foreach (var e in baselineCS0759)
            _output.WriteLine($"  BASE CS0759: {e.File} | {e.Message}");

        // Show candidate CS0759 entries
        var candidateCS0759 = candidateErrors.Where(d => d.Descriptor.Id == "CS0759").ToList();
        _output.WriteLine($"Candidate CS0759 count: {candidateCS0759.Count}");
        foreach (var d in candidateCS0759)
            _output.WriteLine($"  CAND CS0759: {d.Location.SourceTree!.FilePath} | {d.GetMessage()}");

        // Show baseline File+Id pairs for CS0759
        var baselineCS0759Files = baselineCS0759.Select(e => e.File).ToHashSet();
        _output.WriteLine($"Baseline CS0759 files: {string.Join(", ", baselineCS0759Files)}");
        var candidateCS0759Files = candidateCS0759.Select(d => d.Location.SourceTree!.FilePath).ToHashSet();
        _output.WriteLine($"Candidate CS0759 files: {string.Join(", ", candidateCS0759Files)}");

        Assert.True(newErrors.Count == 0,
            "Rewritten files introduced new errors:\n" + string.Join("\n", newErrors));
    }
}
