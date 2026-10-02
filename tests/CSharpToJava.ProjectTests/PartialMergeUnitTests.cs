using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.PartialMerge;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// M1 unit matrix (design §7.1 groups 1-3, 12-13): inline syntax trees →
/// CSharpCompilation → Analyze/RewriteFile text assertions; candidate tree must
/// compile with zero errors.
/// </summary>
public sealed class PartialMergeUnitTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? "D:\\cs2j-partial-merge-test" : "/tmp/cs2j-partial-merge-test";

    private static (CSharpCompilation Compilation, PartialMergePlan Plan) BuildPlan(
        params (string Path, string Source)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Source, new CSharpParseOptions(LanguageVersion.Latest), path: Path.Combine(Root, f.Path)));
        var compilation = CSharpCompilation.Create(
            "partial-merge-tests", trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var plan = new PartialMergePreprocessor().Analyze(compilation, Root);
        return (compilation, plan);
    }

    [Fact]
    public void Group1_Two_File_Class_Members_Union_And_Primary_Determinism()
    {
        var (_, plan) = BuildPlan(
            ("Alpha/Part1.cs", "namespace Demo; public partial class C { public void A() {} }"),
            ("Beta/Part2.cs", "namespace Demo; public partial class C { public void B() {} }"));

        var group = Assert.Single(plan.Groups);
        Assert.Equal("Demo.C", group.TypeFullName);
        Assert.Equal("Alpha/Part1.cs", group.Primary.RelativePath);
        Assert.Equal(2, group.Parts.Count);
        Assert.Contains(group.MergedDeclaration!.Members, m => m.ToString().Contains("A()"));
        Assert.Contains(group.MergedDeclaration!.Members, m => m.ToString().Contains("B()"));
    }

    [Fact]
    public void Group1_Same_File_Smallest_Span_Wins_And_Header_Usings_Preserved()
    {
        var source =
            "using System; namespace Demo { public partial class C { void A(){} } " +
            "public partial class C { void B(){} } }";
        var (_, plan) = BuildPlan(("Same.cs", source));

        var group = Assert.Single(plan.Groups);
        Assert.True(group.Primary.Declaration.SpanStart < group.Parts[1].Declaration.SpanStart);
        var rewritten = new PartialMergeRewriter().Apply(source,
            plan.EditsByFile[Path.Combine(Root, "Same.cs")].ToList());
        Assert.StartsWith("using System;", rewritten);
    }

    [Fact]
    public void Group2_Three_Parts_MultiGroup_Shared_File_And_Nested_Partials()
    {
        var (compilation, plan) = BuildPlan(
            ("a1.cs", "namespace Demo; public partial class A { void X(){} }"),
            ("a2.cs", "namespace Demo; public partial class A { void Y(){} public partial class N { void P(){} } }"),
            ("a3.cs", "namespace Demo; public partial class A { void Z(){} public partial class N { void Q(){} } }"));

        // Two groups: the containing A and the nested N; A spans three parts.
        var group = Assert.Single(plan.Groups.Where(g => g.TypeFullName == "Demo.A"));
        Assert.Equal(3, group.Parts.Count);
        Assert.Contains(plan.Groups, g => g.TypeFullName == "Demo.A.N");

        // Apply all file edits; candidate text must compile and contain X/Y/Z and P/Q.
        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var allText = string.Join("\n", outputs.Select(o => o.Source));
        Assert.Contains("X()", allText);
        Assert.Contains("Y()", allText);
        Assert.Contains("Z()", allText);
        Assert.Contains("P()", allText);
        Assert.Contains("Q()", allText);
    }

    [Fact]
    public void Group3_Struct_Interface_Record_Generic_And_FileScopedNamespace()
    {
        var (compilation, plan) = BuildPlan(
            ("s1.cs", "namespace Demo; public ref partial struct S { int _a; }"),
            ("s2.cs", "namespace Demo; public ref partial struct S { void M(){} }"),
            ("i1.cs", "namespace Demo; public partial interface I { void One(); }"),
            ("i2.cs", "namespace Demo; public partial interface I { void Two(); }"),
            ("r1.cs", "namespace Demo; public partial record class R(int Id);"),
            ("r2.cs", "namespace Demo; public partial record class R { string? Name { get; } }"),
            ("g1.cs", "namespace Demo; public partial class G<T> { void A(T t){} }"),
            ("g2.cs", "namespace Demo; public partial class G<T> { void B(){} }"),
            ("f1.cs", "namespace Demo; public partial class F { }"),
            ("f2.cs", "namespace Demo; public partial class F { }"));

        Assert.Equal(5, plan.Groups.Count);
        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group12_Idempotent_Second_Run_Has_No_Edits()
    {
        var (compilation, plan) = BuildPlan(
            ("p1.cs", "namespace Demo; public partial class C { void A(){} }"),
            ("p2.cs", "namespace Demo; public partial class C { void B(){} }"));

        var outputs = ApplyAll(plan, compilation);
        var secondTrees = outputs.Select(o => CSharpSyntaxTree.ParseText(
            o.Source, new CSharpParseOptions(LanguageVersion.Latest), path: o.Path));
        var secondCompilation = CSharpCompilation.Create(
            "partial-merge-idem", secondTrees, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var secondPlan = new PartialMergePreprocessor().Analyze(secondCompilation, Root);

        Assert.Empty(secondPlan.EditsByFile.SelectMany(g => g));
    }

    [Fact]
    public void Group13_Residual_Count_And_Partial_Token_Kept()
    {
        var (compilation, plan) = BuildPlan(
            ("p1.cs", "namespace Demo; public partial class C { void A(){} }"),
            ("p2.cs", "namespace Demo; public partial class C { void B(){} }"));

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(new PartialMergePreprocessor().CheckResidual(candidate, plan));
        var mergedFile = outputs.Single(o => o.Source.Contains("cs2j-partial-merge: merged"));
        Assert.Contains("partial class C", mergedFile.Source);
    }

    // ---- helpers ----

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static List<(string Path, string Source)> ApplyAll(
        PartialMergePlan plan, CSharpCompilation original)
    {
        var rewriter = new PartialMergeRewriter();
        return original.SyntaxTrees
            .Where(t => t.FilePath.StartsWith(Root))
            .Select(t =>
            {
                var edits = plan.EditsByFile[t.FilePath].ToList();
                var source = t.ToString();
                return (t.FilePath, edits.Count == 0 ? source : rewriter.Apply(source, edits));
            })
            .ToList();
    }

    private static CSharpCompilation Recompile(List<(string Path, string Source)> outputs)
        => CSharpCompilation.Create(
            "partial-merge-candidate",
            outputs.Select(o => CSharpSyntaxTree.ParseText(
                o.Source, new CSharpParseOptions(LanguageVersion.Latest), path: o.Path)),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
}
