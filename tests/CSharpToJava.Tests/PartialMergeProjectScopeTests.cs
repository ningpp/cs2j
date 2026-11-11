using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// 跨工程部件组：整树统一编译会把不同工程里同名（同全名）的 partial 类型当成
/// 同一类型的多个部件。真实 SDK：System.Private.Uri / System.Private.CoreLib /
/// Common 各有独立的一份 ValueStringBuilder，System.Text.Encodings.Web 的
/// tests 工程也声明 OptimizedInboxTextEncoder；把它们合进某一个工程的主文件后，
/// 成员被搬进从未声明过它们的编译单元（CS0246 ValueStringBuilder / Xunit、
/// CS0122 不可访问等 20 个 build 错误）。
/// 规则：一组部件的全部文件必须由「完全相同的一组工程」编译，否则整组保持多部件。
/// </summary>
public class PartialMergeProjectScopeTests
{
    private const string Root = @"d:\cs2j-partial-project-scope";

    private const string OneProject = @"d:\cs2j-partial-project-scope\One.csproj";
    private const string TwoProject = @"d:\cs2j-partial-project-scope\Two.csproj";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static (CSharpCompilation Compilation, PartialMergePlan Plan) Analyze(
        Func<string, IReadOnlyCollection<string>?>? scope,
        params (string Name, string Source)[] files)
    {
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(
            file.Source, ParseOptions, path: Path.Combine(Root, file.Name))).ToList();
        var compilation = CSharpCompilation.Create(
            "partial-merge-project-scope",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (compilation, new PartialMergePreprocessor().Analyze(compilation, Root, null, scope));
    }

    /// <summary>按文件名给出「哪些工程编译它」的解析器；未列出的文件视为未知（null）。</summary>
    private static Func<string, IReadOnlyCollection<string>?> Scope(
        params (string File, string[] Projects)[] scopes)
    {
        var map = scopes.ToDictionary(
            scope => Path.Combine(Root, scope.File),
            scope => (IReadOnlyCollection<string>)scope.Projects,
            StringComparer.OrdinalIgnoreCase);
        return path => map.TryGetValue(path, out var projects) ? projects : null;
    }

    private static readonly (string Name, string Source)[] TwoParts =
    {
        ("a.cs", "namespace Demo; public partial class C { public void A() {} }"),
        ("b.cs", "namespace Demo; public partial class C { public void B() {} }"),
    };

    [Fact]
    public void Parts_Compiled_By_Different_Project_Sets_Stay_Multi_Part()
    {
        var (compilation, plan) = Analyze(
            Scope(("a.cs", new[] { OneProject }), ("b.cs", new[] { OneProject, TwoProject })),
            TwoParts);

        var group = plan.Groups.Single(g => g.TypeFullName == "Demo.C");
        Assert.True(group.SkipUnsupportedShape);
        Assert.Null(group.MergedDeclaration);
        Assert.DoesNotContain(plan.ActiveGroups, g => g.TypeFullName == "Demo.C");
        Assert.Empty(plan.EditsByFile.SelectMany(edits => edits));

        var diagnostic = Assert.Single(plan.Diagnostics.Where(d => d.TypeFullName == "Demo.C"));
        Assert.Equal(PartialMergeCodes.CrossProject, diagnostic.Code);
        Assert.Equal(PartialMergeSeverity.Warning, diagnostic.Severity);
        Assert.Contains("project", diagnostic.Message, StringComparison.OrdinalIgnoreCase);

        // 保持多部件的组必须落在 residual 白名单内，否则 Strict 模式整轮报错。
        Assert.DoesNotContain(
            new PartialMergePreprocessor().CheckResidual(compilation, plan),
            d => d.TypeFullName == "Demo.C");
    }

    [Fact]
    public void Parts_Compiled_By_Same_Project_Set_In_Different_Order_Are_Merged()
    {
        var (_, plan) = Analyze(
            Scope(
                ("a.cs", new[] { OneProject, TwoProject }),
                ("b.cs", new[] { TwoProject, OneProject })),
            TwoParts);

        var group = plan.Groups.Single(g => g.TypeFullName == "Demo.C");
        Assert.False(group.SkipUnsupportedShape);
        Assert.NotNull(group.MergedDeclaration);
        Assert.NotEmpty(plan.EditsByFile.SelectMany(edits => edits));
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == PartialMergeCodes.CrossProject);
    }

    [Fact]
    public void Unknown_Project_Scope_Keeps_Group_Multi_Part()
    {
        // b.cs 不在映射里（工程文件解析失败等）→ 未知，保守跳过合并。
        var (compilation, plan) = Analyze(
            Scope(("a.cs", new[] { OneProject })),
            TwoParts);

        var group = plan.Groups.Single(g => g.TypeFullName == "Demo.C");
        Assert.True(group.SkipUnsupportedShape);
        Assert.Null(group.MergedDeclaration);
        Assert.Contains(plan.Diagnostics, d => d.TypeFullName == "Demo.C");
        Assert.DoesNotContain(
            new PartialMergePreprocessor().CheckResidual(compilation, plan),
            d => d.TypeFullName == "Demo.C");
    }

    [Fact]
    public void No_Project_Graph_Keeps_Whole_Tree_Merging()
    {
        // 目录模式（无工程图）没有跨工程概念，行为不变。
        var (_, plan) = Analyze(null, TwoParts);

        var group = plan.Groups.Single(g => g.TypeFullName == "Demo.C");
        Assert.False(group.SkipUnsupportedShape);
        Assert.NotNull(group.MergedDeclaration);
    }
}
