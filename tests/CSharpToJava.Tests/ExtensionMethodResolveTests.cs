using CSharpToJava.Core.ExtensionMethodResolve;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// M0 contract tests for the extension-method-resolve (C19) core skeleton:
/// rules are empty, so analysis/rewrite is a verified no-op and the residual
/// scan is vacuously empty. Defaults/diagnostics/codes pin the contract the
/// M1 implementation will start filling.
/// </summary>
public sealed class ExtensionMethodResolveTests
{
    private const string ExtensionSource = """
        using System;
        using System.Linq;

        public static class StringExt
        {
            public static int Shout(this string s) => s.Length;
        }

        public class Program
        {
            public static void Main()
            {
                var a = "x".Shout();
                var b = new int[] { 1, 2, 3 }.Select(i => i + 1).Count();
                Console.WriteLine(a + b);
            }
        }
        """;

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static (CSharpCompilation Compilation, SemanticModel Model) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "em-m0-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    [Fact]
    public void Options_CarryTheDesignDefaults()
    {
        var options = new ExtensionMethodResolveOptions();
        Assert.False(options.Strict);
        Assert.True(options.ReportInfo);
        Assert.True(options.RunResidualScan);
        Assert.True(options.RewriteExternalMetadataHosts);
        Assert.Equal(
            new[] { "System.Linq.Enumerable", "System.Linq.Queryable" },
            options.LinqHostSet.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(ExpressionTreeRewritePolicy.RewriteWithIdentityAssert, options.ExpressionTreePolicy);
        Assert.Null(options.OverridesPath);
    }

    [Fact]
    public void M0_RewriteFile_IsByteEquivalentNoOp_AndReportsClean()
    {
        var (compilation, model) = Compile(ExtensionSource);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        // Sanity: the test input genuinely contains a reduced extension call
        // (Shout) plus reduced LINQ calls — the M0 binder deliberately ignores
        // all of them.
        var reduced = model.SyntaxTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(n => model.GetSymbolInfo(n).Symbol)
            .OfType<IMethodSymbol>()
            .Where(s => s.MethodKind == MethodKind.ReducedExtension)
            .ToList();
        Assert.NotEmpty(reduced);

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());

        var result = preprocessor.RewriteFile(model);

        Assert.False(result.Changed);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.FilesScanned);
        Assert.Equal(1, result.Statistics.FilesClean);
        Assert.Equal(0, result.Statistics.FilesTransformed);
        Assert.Equal(0, result.Statistics.CallsitesBoundReduced);
        Assert.Equal(0, result.Statistics.CallsitesRewritten);

        // Unchanged helper keeps the same no-op shape (runner performs the
        // byte-level File.Copy; BOM is preserved there, not via this text).
        var helper = ExtensionFileResult.Unchanged("source");
        Assert.False(helper.Changed);
        Assert.Equal("source", helper.OutputCode);
    }

    [Fact]
    public void M0_ResidualScan_IsEmpty_EvenWithReducedCallsPresent()
    {
        var (compilation, _) = Compile(ExtensionSource);
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());

        var residuals = preprocessor.ResidualScan(compilation);

        Assert.Empty(residuals);
    }

    [Fact]
    public void Statistics_MergeAndSummaryLine_BehaveInM0()
    {
        var stats = new ExtensionMethodResolveStatistics { FilesScanned = 2, FilesClean = 2 };
        stats.Merge(new ExtensionMethodResolveStatistics
        {
            FilesScanned = 1,
            FilesClean = 1,
        });
        stats.ResidualsByRule["EM-COND"] = 1;

        var mergedAgain = new ExtensionMethodResolveStatistics();
        mergedAgain.Merge(stats);
        mergedAgain.Merge(new ExtensionMethodResolveStatistics());

        Assert.Equal(3, mergedAgain.FilesScanned);
        Assert.Equal(3, mergedAgain.FilesClean);
        Assert.Equal(1, mergedAgain.ResidualsByRule["EM-COND"]);
        Assert.Contains("3 clean", mergedAgain.SummaryLine());
    }

    [Fact]
    public void Diagnostic_ToString_AndCodesTable_PinContract()
    {
        var diagnostic = new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Cond,
            ExtensionMethodCodes.NullConditional,
            "r?.M() not lifted",
            "Program.cs",
            12);
        Assert.Equal("Program.cs(12): error CS2J1205: [Cond] r?.M() not lifted", diagnostic.ToString());

        // Codes table (design §6) stays stable: downstream diagnostic routing
        // relies on the exact literals.
        Assert.Equal("CS2J1200", ExtensionMethodCodes.WholeTreeRollback);
        Assert.Equal("CS2J1201", ExtensionMethodCodes.Ambiguous);
        Assert.Equal("CS2J1202", ExtensionMethodCodes.Unbound);
        Assert.Equal("CS2J1203", ExtensionMethodCodes.ExternalHostNoMapping);
        Assert.Equal("CS2J1204", ExtensionMethodCodes.RefMismatch);
        Assert.Equal("CS2J1205", ExtensionMethodCodes.NullConditional);
        Assert.Equal("CS2J1206", ExtensionMethodCodes.CSharp14ExtensionBlock);
        Assert.Equal("CS2J1207", ExtensionMethodCodes.ReducedMethodGroup);
        Assert.Equal("CS2J1210", ExtensionMethodCodes.ExpressionTreeIdentityFailed);
        Assert.Equal("CS2J1220", ExtensionMethodCodes.ResidualFound);
        Assert.Equal("CS2J1230", ExtensionMethodCodes.RestoreOrBuildFailed);
        Assert.Equal("CS2J1231", ExtensionMethodCodes.TestsFailed);
        Assert.Equal("CS2J1290", ExtensionMethodCodes.SingleFileIncompleteBinding);
        Assert.Equal("EM001", ExtensionMethodCodes.Info);
    }
}
