using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C20 部件 using 迁移：非主部件的 using 不能无条件提升到主文件。
/// 真实 SDK（json）：JsonSerializerOptions.cs 的 `using System.ComponentModel;`
/// 被迁移进合并文件后，使主文件原有的 `BooleanConverter`（来自
/// Serialization.Converters）与 System.ComponentModel.BooleanConverter 二义
/// （CS0104），partial-merge 中间树 build 失败、整链中止。
/// 规则：部件自身没用到 → 不携带；必须携带但与主文件现有简单名冲突 → 整组保持
/// 多部件（不合并）。
/// </summary>
public class PartialMergeUsingCarryTests
{
    private const string Root = @"d:\cs2j-partial-merge-test";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static (PartialMergePlan Plan, List<(string Path, string Source)> Outputs,
        CSharpCompilation Candidate) Apply(params (string Name, string Source)[] files)
    {
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(
            file.Source, ParseOptions, path: Path.Combine(Root, file.Name))).ToList();
        var compilation = CSharpCompilation.Create(
            "partial-merge-using-carry",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var plan = new PartialMergePreprocessor().Analyze(compilation, Root);
        var rewriter = new PartialMergeRewriter();

        var outputs = new List<(string Path, string Source)>();
        foreach (var (name, source) in files)
        {
            var path = Path.Combine(Root, name);
            outputs.Add((path, rewriter.Apply(source, plan.EditsByFile[path].ToList())));
        }

        var candidateTrees = outputs.Select(output => CSharpSyntaxTree.ParseText(
            output.Source, ParseOptions, path: output.Path));
        var candidate = CSharpCompilation.Create(
            "partial-merge-using-carry-candidate",
            candidateTrees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (plan, outputs, candidate);
    }

    private const string PrimarySource = """
        using Demo.Converters;

        namespace Demo
        {
            public partial class Options
            {
                public object M() => new BooleanConverter();
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    [Fact]
    public void Unused_Using_From_NonPrimary_Part_Is_Not_Carried()
    {
        var files = new[]
        {
            ("a.cs", PrimarySource),
            ("b.cs", """
                using System.ComponentModel;

                namespace Demo
                {
                    public partial class Options
                    {
                        public void N() { }
                    }
                }
                """),
        };
        var (plan, outputs, candidate) = Apply(files);

        Assert.NotNull(plan.Groups.Single(g => g.TypeFullName == "Demo.Options").MergedDeclaration);
        Assert.DoesNotContain(candidate.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("using System.ComponentModel;", outputs[0].Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Needed_But_Conflicting_Using_Leaves_Group_MultiPart()
    {
        var files = new[]
        {
            ("a.cs", PrimarySource),
            ("b.cs", """
                using System.ComponentModel;

                namespace Demo
                {
                    public partial class Options
                    {
                        public EditorBrowsableState N() => EditorBrowsableState.Never;
                    }
                }
                """),
        };
        var (plan, outputs, candidate) = Apply(files);

        var group = plan.Groups.Single(g => g.TypeFullName == "Demo.Options");
        Assert.True(group.SkipUnsupportedShape);      // 冲突 → 不合并，保持多部件
        Assert.Null(group.MergedDeclaration);
        Assert.Contains(plan.Diagnostics, d => d.TypeFullName == "Demo.Options");

        // 两部件原样输出，合并后的树仍可编译、无 CS0104。
        Assert.Equal(files[0].Item2, outputs[0].Source);
        Assert.Equal(files[1].Item2, outputs[1].Source);
        Assert.DoesNotContain(candidate.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
    }
}
