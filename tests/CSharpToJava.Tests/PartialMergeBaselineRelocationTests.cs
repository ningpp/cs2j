using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// 合并会把非主部件的成员（连同方法体）搬到主部件文件里，基线里「原来在部件
/// 文件中已报的错」因此在候选树上出现在主文件位置。基线快照按文件索引时会把
/// 它当成新错 → 无法归属 → CS2J2002 整树回滚。
/// 真实 SDK（System.Text.RegularExpressions）：
/// SymbolicRegexMatcher.cs 的方法体调用 `RegexFindOptimizations`
/// 中 `#if SYSTEM_TEXT_REGULAREXPRESSIONS` 区域内的成员；本 pass 的统一
/// parse 选项只定义 NETCOREAPP，该区域被裁掉，基线在两部件上都已报 CS1061，
/// 合并后 CS1061 搬到主部件 SymbolicRegexMatcher.Automata.cs，被当成新错。
/// </summary>
public class PartialMergeBaselineRelocationTests
{
    private const string Root = @"d:\cs2j-partial-baseline-relocation";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static (PartialMergePlan Plan, string PrimaryPath, string OtherPath,
        CSharpCompilation Baseline, CSharpCompilation Candidate) Merge(
            params (string Name, string Source)[] files)
    {
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(
            file.Source, ParseOptions, path: Path.Combine(Root, file.Name))).ToList();
        var baseline = CSharpCompilation.Create(
            "partial-merge-relocation",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var plan = new PartialMergePreprocessor().Analyze(baseline, Root);
        var rewriter = new PartialMergeRewriter();
        var candidateTrees = files.Select(file =>
        {
            var path = Path.Combine(Root, file.Name);
            var source = rewriter.Apply(file.Source, plan.EditsByFile[path].ToList());
            return CSharpSyntaxTree.ParseText(source, ParseOptions, path: path);
        }).ToList();

        var candidate = CSharpCompilation.Create(
            "partial-merge-relocation-candidate",
            candidateTrees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var group = plan.Groups.Single(g => g.TypeFullName == "Demo.C");
        return (plan, group.Primary.SourceFilePath,
            group.Parts.Single(part => part.Ordinal != 0).SourceFilePath,
            baseline, candidate);
    }

    private static HashSet<(string Id, string Message)> ErrorKeysOf(
        CSharpCompilation compilation, string path)
    {
        var tree = compilation.SyntaxTrees.Single(t =>
            string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        return compilation.GetSemanticModel(tree)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => d.Location.SourceTree == tree)
            .Select(d => (d.Descriptor.Id, d.GetMessage()))
            .ToHashSet();
    }

    [Fact]
    public void PreExisting_Error_Relocated_From_NonPrimary_Part_Is_Not_New()
    {
        var files = new[]
        {
            // a.cs 字典序在前 → 主部件；b.cs 的方法体引用不存在的成员（CS0103）。
            ("a.cs", """
                namespace Demo;
                public partial class C
                {
                    public void A() { }
                }
                """),
            ("b.cs", """
                namespace Demo;
                public partial class C
                {
                    public void B() => Missing.Type.Call();
                }
                """),
        };

        var (plan, primaryPath, otherPath, baseline, candidate) = Merge(files);
        var group = plan.Groups.Single(g => g.TypeFullName == "Demo.C");

        // 方法体错误不在声明的 parse/declaration 阶段里，组照常合并。
        Assert.False(group.SkipBaseline);
        Assert.NotNull(group.MergedDeclaration);
        Assert.Equal(otherPath, group.Parts[1].SourceFilePath);

        var baselineOtherKeys = ErrorKeysOf(baseline, otherPath);
        Assert.Contains(baselineOtherKeys, key => key.Id == "CS0103");
        Assert.Empty(ErrorKeysOf(baseline, primaryPath));

        // 合并后 CS0103 出现在主部件文件里——按文件的基线视图会把它当新错。
        var relocated = ErrorKeysOf(candidate, primaryPath);
        Assert.Contains(relocated, key => key.Id == "CS0103");

        // 修正后的基线视图：主部件的有效基线包含整组各部件文件的既有错误。
        var effectiveBaseline = PartialMergeBaseline.ForRewrittenFile(
            plan, primaryPath, path => ErrorKeysOf(baseline, path));
        Assert.Contains(relocated, effectiveBaseline.Contains);

        // 非主部件文件只继承自己的既有错误，不反向吞掉主部件的错误。
        var ownBaseline = PartialMergeBaseline.ForRewrittenFile(
            plan, otherPath, path => ErrorKeysOf(baseline, path));
        Assert.Equal(baselineOtherKeys, ownBaseline);
    }
}
