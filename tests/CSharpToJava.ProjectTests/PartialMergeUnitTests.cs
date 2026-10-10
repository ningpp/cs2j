using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.PartialMerge;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Unit matrix (design §7.1): inline syntax trees → CSharpCompilation →
/// Analyze/RewriteFile text assertions; candidate trees must compile with zero
/// errors. M1: groups 1-3, 12-13; M2: groups 6-11, 14-15; M3: groups 4-5 plus
/// the AssumeNoGeneratedPartial call-site / idempotency / residual cases.
/// </summary>
public sealed class PartialMergeUnitTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? "D:\\cs2j-partial-merge-test" : "/tmp/cs2j-partial-merge-test";

    private static (CSharpCompilation Compilation, PartialMergePlan Plan) BuildPlan(
        IReadOnlyList<Action<TypeGroupPlan>>? mutators = null,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary,
        params (string Path, string Source)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Source, new CSharpParseOptions(LanguageVersion.Latest), path: Path.Combine(Root, f.Path)));
        var compilation = CSharpCompilation.Create(
            "partial-merge-tests", trees,
            GetReferences(),
            new CSharpCompilationOptions(outputKind, allowUnsafe: true));
        var plan = new PartialMergePreprocessor().Analyze(compilation, Root, mutators);
        return (compilation, plan);
    }

    /// <summary>M3: options-bearing variant (AssumeNoGeneratedPartial / Strict).</summary>
    private static (CSharpCompilation Compilation, PartialMergePlan Plan) BuildPlanWith(
        PartialMergeOptions options,
        params (string Path, string Source)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Source, new CSharpParseOptions(LanguageVersion.Latest), path: Path.Combine(Root, f.Path)));
        var compilation = CSharpCompilation.Create(
            "partial-merge-tests", trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var plan = new PartialMergePreprocessor(options).Analyze(compilation, Root);
        return (compilation, plan);
    }

    /// <summary>M3: re-run the preprocessor over already-rewritten outputs (fixpoint check).</summary>
    private static PartialMergePlan Replan(
        PartialMergeOptions options,
        List<(string Path, string Source)> outputs)
    {
        var trees = outputs.Select(o => CSharpSyntaxTree.ParseText(
            o.Source, new CSharpParseOptions(LanguageVersion.Latest), path: o.Path));
        var compilation = CSharpCompilation.Create(
            "partial-merge-rerun", trees, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return new PartialMergePreprocessor(options).Analyze(compilation, Root);
    }

    [Fact]
    public void Group1_Two_File_Class_Members_Union_And_Primary_Determinism()
    {
        var (_, plan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
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
        var (_, plan) = BuildPlan(null, OutputKind.DynamicallyLinkedLibrary, ("Same.cs", source));

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
            null, OutputKind.DynamicallyLinkedLibrary,
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
    public void Group3_Struct_Interface_Record_Generic_And_FileScoped_Namespace()
    {
        var (compilation, plan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
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
            null, OutputKind.DynamicallyLinkedLibrary,
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
            null, OutputKind.DynamicallyLinkedLibrary,
            ("p1.cs", "namespace Demo; public partial class C { void A(){} }"),
            ("p2.cs", "namespace Demo; public partial class C { void B(){} }"));

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(new PartialMergePreprocessor().CheckResidual(candidate, plan));
        var mergedFile = outputs.Single(o => o.Source.Contains("cs2j-partial-merge: merged"));
        Assert.Contains("partial class C", mergedFile.Source);
    }

    // ---- Group 6: modifier union semantics and ordering ----

    [Fact]
    public void Group6_Modifiers_Union_Accessibility_Abstract_Sealed_Static_Unsafe_ReadonlyPartialOrder()
    {
        // Same accessibility repeats across parts and dedups; part 2 contributes
        // new modifiers without re-declaring accessibility (that would be CS0262).
        // static and abstract are mutually exclusive on one class, so unsafe is
        // unioned here while readonly ordering is covered by the struct.
        var (compilation, plan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
            ("c1.cs", "namespace Demo; public abstract partial class C { }"),
            ("c2.cs", "namespace Demo; public unsafe partial class C { }"),
            ("s1.cs", "namespace Demo; public readonly partial struct S { readonly int _a; }"),
            ("s2.cs", "namespace Demo; public partial struct S { void M(){} }"));

        var classGroup = Assert.Single(plan.Groups.Where(g => g.TypeFullName == "Demo.C"));
        Assert.Equal(
            "public unsafe abstract partial",
            classGroup.MergedDeclaration!.Modifiers.ToString().Trim());

        var structGroup = Assert.Single(plan.Groups.Where(g => g.TypeFullName == "Demo.S"));
        var kinds = structGroup.MergedDeclaration!.Modifiers.Select(t => t.Kind()).ToList();
        Assert.True(
            kinds.IndexOf(SyntaxKind.ReadOnlyKeyword) < kinds.IndexOf(SyntaxKind.PartialKeyword),
            "readonly must precede partial (CS1585)");
    }

    // ---- Group 7: base/interface semantic dedup and conflict baseline-skip ----

    [Fact]
    public void Group7_Base_One_NonObject_Interface_Semantic_Dedup_And_Conflict_BaselineSkip()
    {
        var (compilation, plan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
            ("bases.cs",
                "namespace Demo { public class Base1 {} public class Base2 {} " +
                "public interface IThing { void M(); } }"),
            ("Alpha/C.Part1.cs", "namespace Demo; public partial class C : Base1, IThing { }"),
            ("Beta/C.Part2.cs",
                "namespace Demo; public partial class C : Base1, IThing { void IThing.M(){} }"),
            ("Delta/D.Part1.cs", "namespace Demo; public partial class D : Base1 { }"),
            ("Delta/D.Part2.cs", "namespace Demo; public partial class D : Base2 { }"));

        var cGroup = Assert.Single(plan.Groups.Where(g => g.TypeFullName == "Demo.C"));
        Assert.False(cGroup.SkipBaseline);
        Assert.Equal(2, cGroup.MergedDeclaration!.BaseList!.Types.Count);

        var dGroup = Assert.Single(plan.Groups.Where(g => g.TypeFullName == "Demo.D"));
        Assert.True(dGroup.SkipBaseline);
        Assert.Null(dGroup.MergedDeclaration);

        Assert.Empty(plan.EditsByFile[Path.Combine(Root, "Delta/D.Part1.cs")]);
        Assert.Empty(plan.EditsByFile[Path.Combine(Root, "Delta/D.Part2.cs")]);

        // The pass emits no own codes for a baseline-covered conflict.
        Assert.Empty(plan.Diagnostics.Where(d => d.Code.StartsWith("CS2J20", StringComparison.Ordinal)));

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error
            && d.GetMessage().Contains("Demo.C")));
    }

    // ---- Group 8: attribute union / AllowMultiple / non-AllowMultiple baseline ----

    [Fact]
    public void Group8_Attributes_Union_AllowMultiple_And_NonAllowMultiple_BaselineSkip()
    {
        var (compilation, plan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
            ("attributes.cs",
                "namespace Demo; " +
                "[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)] " +
                "public sealed class MultiAttribute : System.Attribute {} " +
                "[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)] " +
                "public sealed class SingleAttribute : System.Attribute {}"),
            ("Alpha/C.Part1.cs", "namespace Demo; [Multi] public partial class C { }"),
            ("Beta/C.Part2.cs", "namespace Demo; [Multi] public partial class C { }"),
            ("Delta/D.Part1.cs", "namespace Demo; [Single] public partial class D { }"),
            ("Delta/D.Part2.cs", "namespace Demo; [Single] public partial class D { }"));

        var cGroup = Assert.Single(plan.Groups.Where(g => g.TypeFullName == "Demo.C"));
        Assert.False(cGroup.SkipBaseline);
        Assert.Equal(2, cGroup.MergedDeclaration!.AttributeLists.Count);

        var dGroup = Assert.Single(plan.Groups.Where(g => g.TypeFullName == "Demo.D"));
        Assert.True(dGroup.SkipBaseline);
        Assert.Null(dGroup.MergedDeclaration);
        Assert.Empty(plan.EditsByFile[Path.Combine(Root, "Delta/D.Part1.cs")]);
        Assert.Empty(plan.EditsByFile[Path.Combine(Root, "Delta/D.Part2.cs")]);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        // D is baseline-skipped, so its pre-existing CS0579 remains; the merged
        // C group introduces no new errors.
        var candidateErrors = candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.All(candidateErrors, d => Assert.Equal("CS0579", d.Id));
    }

    // ---- Group 9: XML doc merge ordering, source paths and byte-stable rerun ----

    [Fact]
    public void Group9_XmlDoc_Merge_Order_SourcePaths_And_Rerun_ByteStable()
    {
        var (compilation, plan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
            ("Alpha/C.Part1.cs",
                "namespace Demo;\n/// <summary>One</summary>\npublic partial class C { public void A(){} }"),
            ("Beta/C.Part2.cs",
                "namespace Demo;\n/// <summary>\n/// Two\n/// </summary>\npublic partial class C { public void B(){} }"));

        var group = Assert.Single(plan.Groups);
        var text = group.MergedDeclaration!.ToFullString();

        var idxSummary = text.IndexOf("<summary>One</summary>", StringComparison.Ordinal);
        var idxRemarks = text.IndexOf("/// <remarks>", StringComparison.Ordinal);
        var idxSource = text.IndexOf("para source=\"Beta/C.Part2.cs\"", StringComparison.Ordinal);
        var idxTwo = text.IndexOf("Two", StringComparison.Ordinal);
        var idxRemarksClose = text.IndexOf("/// </remarks>", StringComparison.Ordinal);
        var idxBanner = text.IndexOf("cs2j-partial-merge: merged", StringComparison.Ordinal);

        Assert.True(idxSummary >= 0);
        Assert.True(idxSummary < idxRemarks);
        Assert.True(idxRemarks < idxSource);
        Assert.True(idxSource < idxTwo);
        Assert.True(idxTwo < idxRemarksClose);
        Assert.True(idxRemarksClose < idxBanner);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        // Byte-stable rerun: fresh compilation from the same inputs.
        var (_, secondPlan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
            ("Alpha/C.Part1.cs",
                "namespace Demo;\n/// <summary>One</summary>\npublic partial class C { public void A(){} }"),
            ("Beta/C.Part2.cs",
                "namespace Demo;\n/// <summary>\n/// Two\n/// </summary>\npublic partial class C { public void B(){} }"));
        Assert.Equal(text, Assert.Single(secondPlan.Groups).MergedDeclaration!.ToFullString());
    }

    // ---- Group 10: global using synthetic-tree gating and negative control ----

    [Fact]
    public void Group10_GlobalUsing_Alias_Signature_Pairing_And_NoSyntheticTrees_Negative()
    {
        static (CSharpCompilation Compilation, PartialMergePlan Plan) Run(bool withSyntheticTree)
        {
            var entries = new List<(string Path, string Source)>
            {
                ("Alpha/C.Part1.cs", "namespace Demo; public partial class C { public void A(Int32 x){} }"),
                ("Beta/C.Part2.cs", "namespace Demo; public partial class C { public void B(){} }"),
            };
            var trees = entries.Select(f => CSharpSyntaxTree.ParseText(
                f.Source, new CSharpParseOptions(LanguageVersion.Latest),
                path: Path.Combine(Root, f.Path))).ToList<SyntaxTree>();
            if (withSyntheticTree)
            {
                trees.Add(CSharpSyntaxTree.ParseText(
                    "global using System;",
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: Path.Combine(Root, "__cs2j_global_usings.g.cs")));
            }

            var compilation = CSharpCompilation.Create(
                "partial-merge-gu", trees, GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var plan = new PartialMergePreprocessor().Analyze(compilation, Root);
            return (compilation, plan);
        }

        // With the synthetic global using: Int32 resolves; group merges and the
        // candidate compiles; the synthetic tree itself is never grouped.
        var (withComp, withPlan) = Run(withSyntheticTree: true);
        Assert.Single(withPlan.Groups);
        Assert.False(withPlan.Groups[0].SkipBaseline);
        var withOutputs = ApplyAll(withPlan, withComp);
        var withCandidate = Recompile(withOutputs);
        Assert.Empty(withCandidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        // Negative: without the synthetic tree the baseline already carries
        // CS0246, so the group is baseline-skipped and no edits are produced.
        var (_, withoutPlan) = Run(withSyntheticTree: false);
        var group = Assert.Single(withoutPlan.Groups);
        Assert.True(group.SkipBaseline);
        Assert.Null(group.MergedDeclaration);
        Assert.Empty(withoutPlan.EditsByFile.SelectMany(g => g));
    }

    // ---- Group 11: rollback CS2J2001 / CS2J2002 / Strict=false ----

    [Fact]
    public void Group11_Unique_Attribution_Rolls_Group_Back_CS2J2001()
    {
        var (compilation, plan) = BuildPlan(
            [InjectDuplicateA],
            OutputKind.DynamicallyLinkedLibrary,
            ("a1.cs", "namespace Demo; public partial class C { public void A(){} }"),
            ("a2.cs", "namespace Demo; public partial class C { public void B(){} }"));

        // Original sources are clean; candidate gets an injected CS0111.
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        var newErrors = candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Contains(newErrors, d => d.Id == "CS0111");

        // Unique attribution by touched file (design D9).
        var first = newErrors[0];
        var file = first.Location.SourceTree!.FilePath;
        var hits = plan.ActiveGroups
            .Where(g => g.TouchedFiles.Any(t =>
                string.Equals(t, file, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Single(hits);

        // Rolled-back group is whitelisted for the residual gate.
        hits[0].RolledBack = true;
        Assert.Empty(new PartialMergePreprocessor().CheckResidual(candidate, plan));

        var rollbackDiagnostic = new PartialMergeDiagnostic(
            PartialMergeSeverity.Warning,
            PartialMergeCodes.GroupRolledBack,
            "group rolled back",
            TypeFullName: "Demo.C");
        Assert.Equal("CS2J2001", rollbackDiagnostic.Code);
        Assert.Contains("warning CS2J2001", rollbackDiagnostic.ToMsBuildString());
    }

    [Fact]
    public void Group11_Unattributable_Error_Rolls_Whole_Tree_Back_CS2J2002()
    {
        var (compilation, plan) = BuildPlan(
            [InjectDuplicateShared],
            OutputKind.DynamicallyLinkedLibrary,
            ("Shared/All.cs",
                "namespace Demo; public partial class C { public void CA(){} } " +
                "public partial class D { public void DA(){} }"),
            ("c2.cs", "namespace Demo; public partial class C { public void CB(){} }"),
            ("d2.cs", "namespace Demo; public partial class D { public void DB(){} }"));

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        var newErrors = candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(newErrors);

        // Every new error sits in the file shared by two active groups → hits != 1.
        foreach (var error in newErrors)
        {
            var file = error.Location.SourceTree!.FilePath;
            var hits = plan.ActiveGroups
                .Where(g => g.TouchedFiles.Any(t =>
                    string.Equals(t, file, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.NotEqual(1, hits.Count);
        }

        // Whole-tree rollback restores the originals, which are clean.
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var rollbackDiagnostic = new PartialMergeDiagnostic(
            PartialMergeSeverity.Error,
            PartialMergeCodes.WholeTreeRolledBack,
            "whole tree rolled back");
        Assert.Equal("CS2J2002", rollbackDiagnostic.Code);
        Assert.Contains("error CS2J2002", rollbackDiagnostic.ToMsBuildString());
    }

    [Fact]
    public void Group11_StrictFalse_Downgrades_Residual_To_Warning()
    {
        var entries = new (string Path, string Source)[]
        {
            ("a1.cs", "namespace Demo; public partial class C { public void A(){} }"),
            ("a2.cs", "namespace Demo; public partial class C { public void B(){} }"),
        };
        var trees = entries.Select(f => CSharpSyntaxTree.ParseText(
            f.Source, new CSharpParseOptions(LanguageVersion.Latest),
            path: Path.Combine(Root, f.Path)));
        var compilation = CSharpCompilation.Create(
            "partial-merge-strict", trees, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var relaxed = new PartialMergePreprocessor(
            new PartialMergeOptions { Strict = false });
        var plan = relaxed.Analyze(compilation, Root, [NullMerged]);

        // Forced no-merge leaves the type multi-part with no whitelist entry.
        Assert.Null(plan.Groups[0].MergedDeclaration);
        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        var residuals = relaxed.CheckResidual(candidate, plan);

        Assert.NotEmpty(residuals);
        Assert.All(residuals, d => Assert.Equal(PartialMergeSeverity.Warning, d.Severity));
        Assert.All(residuals, d => Assert.Equal(PartialMergeCodes.Residual, d.Code));
    }

    // ---- Group 14: generator safety — extra 'generated' part; classic declaration kept ----

    [Fact]
    public void Group14_Simulated_Generated_Part_Compiles_And_Classic_Declaration_Kept()
    {
        var (compilation, plan) = BuildPlan(
            null, OutputKind.DynamicallyLinkedLibrary,
            ("Methods/C.Part1.cs",
                "namespace Demo; public partial class C { partial void Hook(); public void Run(){ Hook(); } }"),
            ("Methods/C.Part2.cs", "namespace Demo; public partial class C { partial void Hook() {} }"),
            ("Generated/SimulatedGenerated.G.cs", "namespace Demo; public partial class C { }"));

        var group = Assert.Single(plan.Groups);
        Assert.Equal(3, group.Parts.Count);
        Assert.Contains(
            group.MergedDeclaration!.Modifiers, t => t.IsKind(SyntaxKind.PartialKeyword));
        var mergedText = group.MergedDeclaration.ToFullString();
        Assert.Contains("partial class C", mergedText);
        Assert.Contains("Hook()", mergedText);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ---- Group 15: top-level statements Program is never grouped/changed ----

    [Fact]
    public void Group15_TopLevel_Program_Not_Grouped_Or_Changed()
    {
        var (compilation, plan) = BuildPlan(
            null, OutputKind.ConsoleApplication,
            ("Program.cs", "System.Console.WriteLine(0);"),
            ("C.cs", "namespace Demo; public partial class C {}"));

        Assert.Empty(plan.Groups);
        Assert.Empty(plan.EditsByFile.SelectMany(g => g));

        var outputs = ApplyAll(plan, compilation);
        Assert.Equal("System.Console.WriteLine(0);", outputs[0].Source);
        var candidate = Recompile(outputs, OutputKind.ConsoleApplication);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ---- M3 Group 4: classic vs extended partial methods ----

    [Fact]
    public void Group4_Classic_Pair_Collapses_To_Implementation_And_Drops_Partial_Token()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("Methods/C.Part1.cs",
                "namespace Demo; public partial class C { partial void Hook(); public void Run(){ Hook(); } }"),
            ("Methods/C.Part2.cs",
                "namespace Demo; public partial class C { partial void Hook() { System.Console.WriteLine(1); } }"));

        var group = Assert.Single(plan.Groups);
        Assert.Equal(1, plan.Statistics.MembersCollapsed);

        var mergedText = group.MergedDeclaration!.ToFullString();
        Assert.DoesNotContain("partial void Hook", mergedText);
        Assert.Contains("void Hook()", mergedText);
        Assert.Contains("WriteLine(1)", mergedText);
        // The call site keeps binding to the same name: zero text change there.
        Assert.Contains("Hook();", mergedText);
        // Type-level partial token is intentionally retained (generator safety).
        Assert.Contains("partial class C", mergedText);

        // Exactly one Hook member survives (declaration+implementation collapsed);
        // count by member name so the Hook() call inside Run() is not mistaken
        // for a second member.
        var hookMembers = group.MergedDeclaration.Members
            .OfType<MethodDeclarationSyntax>()
            .Count(m => m.Identifier.Text == "Hook");
        Assert.Equal(1, hookMembers);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group4_Static_Classic_And_Modern_Generic_Pairs_Collapse()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("a.cs", """
                namespace Demo;
                public partial class C
                {
                    static partial void S();
                    public partial T Pick<T>(T a, T b);
                    public void Drive() { S(); var x = Pick(1, 2); }
                }
                """),
            ("b.cs", """
                namespace Demo;
                public partial class C
                {
                    static partial void S() { }
                    public partial T Pick<T>(T a, T b) => a;
                }
                """));

        var mergedText = Assert.Single(plan.Groups).MergedDeclaration!.ToFullString();
        Assert.Equal(2, plan.Statistics.MembersCollapsed);
        Assert.Contains("static void S()", mergedText);
        Assert.DoesNotContain("partial T Pick", mergedText);
        Assert.Contains("public T Pick<T>(T a, T b) => a;", mergedText);
        Assert.Contains("S();", mergedText);
        Assert.Contains("Pick(1, 2)", mergedText);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group4_Classic_Declaration_Only_Is_Kept_By_Default()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("Methods/C.Part1.cs",
                "namespace Demo; public partial class C { partial void Hook(); public void Run(){ Hook(); } }"),
            ("Methods/C.Part2.cs",
                "namespace Demo; public partial class C { public void Other(){} }"));

        var mergedText = Assert.Single(plan.Groups).MergedDeclaration!.ToFullString();
        Assert.Equal(0, plan.Statistics.MembersCollapsed);
        Assert.Equal(0, plan.Statistics.PartialDeclarationsRemoved);
        Assert.Contains("partial void Hook();", mergedText);
        Assert.Contains("Hook();", mergedText);

        // A no-implementation classic partial is legal C# (calls elide), so the
        // candidate compiles — v1 behavior, no PartialMethodMerger-style drop.
        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group4_Extended_Method_Missing_Implementation_Baseline_Skips_Group()
    {
        var (_, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("a.cs", "namespace Demo; public partial class C { public partial int Modern(int x); }"),
            ("b.cs", "namespace Demo; public partial class C { }"));

        var group = Assert.Single(plan.Groups);
        Assert.True(group.SkipBaseline);
        Assert.Null(group.MergedDeclaration);
        Assert.Empty(plan.EditsByFile.SelectMany(g => g));
        // The pass must not compete with the compiler (CS8795) with its own code.
        Assert.DoesNotContain(plan.Diagnostics, d => d.Code == PartialMergeCodes.ExtendedPartialMissingImpl);
    }

    // ---- M3 Group 5: C#13 properties / indexers / events / constructors ----

    [Fact]
    public void Group5_Property_Pair_Collapses_And_Accessor_Attributes_Combine()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("attrs.cs",
                "namespace Demo; public class AttrAAttribute : System.Attribute {} " +
                "public class AttrBAttribute : System.Attribute {} " +
                "public class AttrCAttribute : System.Attribute {} " +
                "public class AttrDAttribute : System.Attribute {}"),
            ("Alpha/C.Part1.cs", """
                namespace Demo;
                public partial class C
                {
                    private int _p;
                    public partial int P { [AttrA] get; [AttrC] private set; }
                }
                """),
            ("Beta/C.Part2.cs", """
                namespace Demo;
                public partial class C
                {
                    public partial int P
                    {
                        [AttrB] get => _p;
                        [AttrD] private set { _p = value; }
                    }
                }
                """));

        var group = Assert.Single(plan.Groups);
        Assert.Equal(1, plan.Statistics.MembersCollapsed);
        var mergedText = group.MergedDeclaration!.ToFullString();
        Assert.DoesNotContain("partial int P", mergedText);

        // Implementation-side accessor attribute first, definition-side appended,
        // matched per accessor kind (get vs set) independently.
        var getIndex = mergedText.IndexOf("get => _p;", StringComparison.Ordinal);
        var setIndex = mergedText.IndexOf("private set", StringComparison.Ordinal);
        Assert.True(getIndex >= 0 && setIndex >= 0);
        Assert.True(mergedText.LastIndexOf("[AttrB]", getIndex, StringComparison.Ordinal) >= 0);
        Assert.True(mergedText.LastIndexOf("[AttrA]", getIndex, StringComparison.Ordinal) >= 0);
        Assert.True(mergedText.LastIndexOf("[AttrB]", getIndex, StringComparison.Ordinal)
            < mergedText.LastIndexOf("[AttrA]", getIndex, StringComparison.Ordinal));
        Assert.True(mergedText.LastIndexOf("[AttrD]", setIndex, StringComparison.Ordinal) >= 0);
        Assert.True(mergedText.LastIndexOf("[AttrC]", setIndex, StringComparison.Ordinal) >= 0);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group5_Indexer_Pair_Collapses()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("a.cs", "namespace Demo; public partial class C { public partial int this[int i] { get; } }"),
            ("b.cs", """
                namespace Demo;
                public partial class C
                {
                    private int[] _arr = new int[3];
                    public partial int this[int i] { get => _arr[i]; }
                }
                """));

        var mergedText = Assert.Single(plan.Groups).MergedDeclaration!.ToFullString();
        Assert.Equal(1, plan.Statistics.MembersCollapsed);
        Assert.DoesNotContain("partial int this", mergedText);
        Assert.Contains("public int this[int i] { get => _arr[i]; }", mergedText);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group5_Event_FieldDefinition_And_AddRemove_Implementation_Collapses()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("a.cs", "namespace Demo; public partial class C { public partial event System.Action? Changed; }"),
            ("b.cs", """
                namespace Demo;
                public partial class C
                {
                    private System.Action? _changed;
                    public partial event System.Action? Changed
                    {
                        add => _changed += value;
                        remove => _changed -= value;
                    }
                }
                """));

        var mergedText = Assert.Single(plan.Groups).MergedDeclaration!.ToFullString();
        Assert.Equal(1, plan.Statistics.MembersCollapsed);
        Assert.DoesNotContain("partial event", mergedText);
        Assert.Contains("public event System.Action? Changed", mergedText);
        Assert.Contains("add => _changed += value;", mergedText);
        Assert.Contains("remove => _changed -= value;", mergedText);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group5_Constructor_Pair_Collapses_And_Initializer_Stays_On_Implementation()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("base.cs", "namespace Demo; public class Base { protected Base() {} }"),
            ("Alpha/C.Part1.cs",
                "namespace Demo; public partial class C : Base { private int _id; public partial C(int id); }"),
            ("Beta/C.Part2.cs", """
                namespace Demo;
                public partial class C : Base
                {
                    public partial C(int id) : base() { _id = id; }
                    public C() : this(0) { }
                }
                """));

        var group = Assert.Single(plan.Groups);
        Assert.Equal(1, plan.Statistics.MembersCollapsed);
        var mergedText = group.MergedDeclaration!.ToFullString();
        Assert.DoesNotContain("partial C(int", mergedText);
        Assert.Contains("public C(int id) : base()", mergedText);
        // The ordinary (non-partial) ctor survives untouched.
        Assert.Contains("public C() : this(0)", mergedText);

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Group5_Constructor_Definition_With_Initializer_Is_Baseline_Skipped()
    {
        var (_, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("base.cs", "namespace Demo; public class Base { protected Base() {} }"),
            ("a.cs", "namespace Demo; public partial class C : Base { public partial C(int id) : base(); }"),
            ("b.cs", "namespace Demo; public partial class C : Base { public partial C(int id) { } }"));

        var group = Assert.Single(plan.Groups);
        Assert.True(group.SkipBaseline);
        Assert.Null(group.MergedDeclaration);
        Assert.Empty(plan.EditsByFile.SelectMany(g => g));
    }

    [Fact]
    public void Group5_Primary_Constructor_On_Two_Parts_Is_Baseline_Skipped()
    {
        var (_, classPlan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("c1.cs", "namespace Demo; public partial class C(int x) { public int X => x; }"),
            ("c2.cs", "namespace Demo; public partial class C(int x) { }"));

        var classGroup = Assert.Single(classPlan.Groups);
        Assert.True(classGroup.SkipBaseline);
        Assert.Null(classGroup.MergedDeclaration);
        Assert.Empty(classPlan.EditsByFile.SelectMany(g => g));

        // Record primary-constructor parameter lists on two parts: explicit
        // unsupported-shape diagnostic (CS2J2016), not a silent skip.
        var (_, recordPlan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("r1.cs", "namespace Demo; public partial record R(int a);"),
            ("r2.cs", "namespace Demo; public partial record R(int a) { }"));

        var recordGroup = Assert.Single(recordPlan.Groups);
        Assert.True(recordGroup.SkipUnsupportedShape);
        Assert.Null(recordGroup.MergedDeclaration);
        Assert.Contains(recordPlan.Diagnostics,
            d => d.Code == PartialMergeCodes.UnsupportedShape
                && d.Severity == PartialMergeSeverity.Error);
    }

    // ---- M3: nested partial type member pairing ----

    [Fact]
    public void M3_Nested_Partial_Type_Member_Pairs_Collapse()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("Alpha/O.Part1.cs",
                "namespace Demo; public partial class O { public partial class I { partial void H(); } }"),
            ("Beta/O.Part2.cs",
                "namespace Demo; public partial class O { public partial class I { partial void H(){} public void R(){ H(); } } }"));

        Assert.Contains(plan.Groups, g => g.TypeFullName == "Demo.O.I");
        var outputs = ApplyAll(plan, compilation);
        var allText = string.Join("\n", outputs.Select(o => o.Source));

        // The nested implementation survives once; the definition part is gone.
        Assert.Single(System.Linq.Enumerable.Where(
            allText.Split('\n'), line => line.Contains("void H")));
        Assert.DoesNotContain("partial void H();", allText);
        Assert.Contains("H();", allText);

        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ---- M3: member-level doc/attribute merge (D6) ----

    [Fact]
    public void M3_Collapsed_Member_Merges_Docs_And_Attributes()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("attrs.cs", "namespace Demo; public class AttrAAttribute : System.Attribute {} public class AttrBAttribute : System.Attribute {}"),
            ("Alpha/C.Part1.cs", """
                namespace Demo;
                public partial class C
                {
                    /// <summary>definition doc</summary>
                    [AttrA]
                    public partial int M();
                }
                """),
            ("Beta/C.Part2.cs", """
                namespace Demo;
                public partial class C
                {
                    /// <summary>implementation doc</summary>
                    [AttrB]
                    public partial int M() => 1;
                }
                """));

        var mergedText = Assert.Single(plan.Groups).MergedDeclaration!.ToFullString();

        var idxImplDoc = mergedText.IndexOf("implementation doc", StringComparison.Ordinal);
        var idxRemarks = mergedText.IndexOf("/// <remarks>", StringComparison.Ordinal);
        var idxSource = mergedText.IndexOf("para source=\"Alpha/C.Part1.cs\"", StringComparison.Ordinal);
        var idxDefDoc = mergedText.IndexOf("definition doc", StringComparison.Ordinal);
        var idxMember = mergedText.IndexOf("public int M()", StringComparison.Ordinal);

        Assert.True(idxImplDoc >= 0);
        Assert.True(idxImplDoc < idxRemarks);
        Assert.True(idxRemarks < idxSource);
        Assert.True(idxSource < idxDefDoc);
        Assert.True(idxDefDoc < idxMember);

        // Implementation-side attribute first.
        Assert.True(mergedText.IndexOf("[AttrB]", StringComparison.Ordinal)
            < mergedText.IndexOf("[AttrA]", StringComparison.Ordinal));

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    // ---- M3: AssumeNoGeneratedPartial call-site removal special cases ----

    [Fact]
    public void M3_OptIn_MultiPart_Removes_Declaration_And_Block_Call_Sites()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions { AssumeNoGeneratedPartial = true },
            ("a.cs", "namespace Demo; public partial class C { partial void Hook(); public void R(){ Hook(); Hook(); } }"),
            ("b.cs", "namespace Demo; public partial class C { public void X(){} }"));

        var group = Assert.Single(plan.Groups);
        Assert.False(group.IsMemberOnlyRewrite);
        Assert.Equal(1, plan.Statistics.PartialDeclarationsRemoved);
        Assert.Equal(2, plan.Statistics.PartialCallSitesRemoved);
        Assert.Equal(1, plan.Statistics.GroupsMerged);

        var outputs = ApplyAll(plan, compilation);
        var allText = string.Join("\n", outputs.Select(o => o.Source));
        Assert.DoesNotContain("partial void Hook", allText);
        Assert.DoesNotContain("Hook();", allText);
        Assert.Contains("public void R(){ }", allText);

        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void M3_OptIn_SinglePart_MemberOnly_Rewrite_And_Second_Run_Zero_Edits()
    {
        var options = new PartialMergeOptions { AssumeNoGeneratedPartial = true };
        var (compilation, plan) = BuildPlanWith(
            options,
            ("a.cs", "namespace Demo; public partial class C { partial void Hook(); public void R(){ Hook(); } }"));

        var group = Assert.Single(plan.Groups);
        Assert.True(group.IsMemberOnlyRewrite);
        Assert.Equal(0, plan.Statistics.GroupsMerged);
        Assert.Equal(1, plan.Statistics.PartialDeclarationsRemoved);
        Assert.Equal(1, plan.Statistics.PartialCallSitesRemoved);

        var edits = plan.EditsByFile.SelectMany(g => g).ToList();
        var edit = Assert.Single(edits);
        Assert.IsType<ReplaceTypeEdit>(edit);
        // Member-only rewrites never emit part-removal or using edits.
        Assert.All(edits, e => Assert.IsType<ReplaceTypeEdit>(e));

        var outputs = ApplyAll(plan, compilation);
        var allText = string.Join("\n", outputs.Select(o => o.Source));
        Assert.DoesNotContain("partial void Hook", allText);
        Assert.Contains("public void R(){ }", allText);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var secondPlan = Replan(options, outputs);
        Assert.Empty(secondPlan.EditsByFile.SelectMany(g => g));
        Assert.Equal(0, secondPlan.Statistics.PartialDeclarationsRemoved);
    }

    [Fact]
    public void M3_Expression_Bodied_Call_Site_Emits_CS2J2019_And_Keeps_Everything()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions { AssumeNoGeneratedPartial = true },
            ("a.cs", "namespace Demo; public partial class C { partial void Hook(); public void R() => Hook(); }"));

        var diagnostic = Assert.Single(plan.Diagnostics
            .Where(d => d.Code == PartialMergeCodes.PartialCallSiteNotRemovable));
        Assert.Equal(PartialMergeSeverity.Warning, diagnostic.Severity);
        Assert.EndsWith("a.cs", diagnostic.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(diagnostic.Line > 0);
        Assert.True(diagnostic.Column > 0);
        Assert.Equal(0, plan.Statistics.PartialDeclarationsRemoved);
        Assert.Equal(0, plan.Statistics.PartialCallSitesRemoved);
        Assert.Empty(plan.EditsByFile.SelectMany(g => g));

        var outputs = ApplyAll(plan, compilation);
        var allText = string.Join("\n", outputs.Select(o => o.Source));
        Assert.Contains("partial void Hook();", allText);
        Assert.Contains("R() => Hook();", allText);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void M3_Call_Inside_Lambda_Aborts_Whole_Method_Removal()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions { AssumeNoGeneratedPartial = true },
            ("a.cs", "using System; namespace Demo; public partial class C { partial void Hook(); public void R(){ Hook(); Action a = () => Hook(); a(); } }"));

        Assert.Contains(plan.Diagnostics,
            d => d.Code == PartialMergeCodes.PartialCallSiteNotRemovable);
        Assert.Equal(0, plan.Statistics.PartialDeclarationsRemoved);
        Assert.Equal(0, plan.Statistics.PartialCallSitesRemoved);
        Assert.Empty(plan.EditsByFile.SelectMany(g => g));

        // The block-level statement survives together with the declaration:
        // no half-finished products.
        var outputs = ApplyAll(plan, compilation);
        var allText = string.Join("\n", outputs.Select(o => o.Source));
        Assert.Contains("partial void Hook();", allText);
        Assert.Contains("() => Hook()", allText);
    }

    // ---- M3: collapse-path idempotency and residual ----

    [Fact]
    public void M3_Collapse_Path_Second_Run_Has_Zero_Edits()
    {
        var options = new PartialMergeOptions();
        var (compilation, plan) = BuildPlanWith(
            options,
            ("Methods/C.Part1.cs",
                "namespace Demo; public partial class C { partial void Hook(); public void Run(){ Hook(); } }"),
            ("Methods/C.Part2.cs",
                "namespace Demo; public partial class C { partial void Hook() { } }"));

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(candidate.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var secondPlan = Replan(options, outputs);
        Assert.Empty(secondPlan.EditsByFile.SelectMany(g => g));
        Assert.Equal(0, secondPlan.Statistics.MembersCollapsed);
    }

    [Fact]
    public void M3_Collapsed_Candidate_Has_No_Residual_MultiPart_Types()
    {
        var (compilation, plan) = BuildPlanWith(
            new PartialMergeOptions(),
            ("a.cs", "namespace Demo; public partial class C { partial void Hook(); }"),
            ("b.cs", "namespace Demo; public partial class C { partial void Hook() { } public void X(){} }"));

        var outputs = ApplyAll(plan, compilation);
        var candidate = Recompile(outputs);
        Assert.Empty(new PartialMergePreprocessor().CheckResidual(candidate, plan));
    }

    // ---- injection mutators (test-only) ----

    private static void InjectDuplicateA(TypeGroupPlan group)
    {
        if (group.TypeFullName != "Demo.C")
        {
            return;
        }

        group.MergedDeclaration = group.MergedDeclaration!.AddMembers(
            SyntaxFactory.ParseMemberDeclaration("public void A() {}")!);
    }

    private static void InjectDuplicateShared(TypeGroupPlan group)
    {
        var member = group.TypeFullName switch
        {
            "Demo.C" => "public void CA() {}",
            "Demo.D" => "public void DA() {}",
            _ => null,
        };
        if (member != null)
        {
            group.MergedDeclaration = group.MergedDeclaration!.AddMembers(
                SyntaxFactory.ParseMemberDeclaration(member)!);
        }
    }

    private static void NullMerged(TypeGroupPlan group)
    {
        if (group.TypeFullName == "Demo.C")
        {
            group.MergedDeclaration = null;
        }
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

    private static CSharpCompilation Recompile(
        List<(string Path, string Source)> outputs,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
        => CSharpCompilation.Create(
            "partial-merge-candidate",
            outputs.Select(o => CSharpSyntaxTree.ParseText(
                o.Source, new CSharpParseOptions(LanguageVersion.Latest), path: o.Path)),
            GetReferences(),
            new CSharpCompilationOptions(outputKind, allowUnsafe: true));
}
