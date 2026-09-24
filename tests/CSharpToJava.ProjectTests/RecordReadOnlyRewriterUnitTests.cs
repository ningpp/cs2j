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
        new CSharpParseOptions(LanguageVersion.Preview);

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
        => AssertOutputCompiles(output, null);

    internal static void AssertOutputCompiles(string output, params string[]? extraUsings)
    {
        var preamble = extraUsings != null && extraUsings.Length > 0
            ? string.Join("\n", extraUsings) + "\n"
            : "";
        var tree = CSharpSyntaxTree.ParseText(preamble + output, ParseOptions);
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
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(output, "readonly partial record struct P").Count);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS3_SimpleSetProperty_ConvertedToInit()
    {
        var src = "public record struct P { public int X { get; set; } }";
        var (output, stats, diags) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        Assert.Contains("init;", output);
        Assert.DoesNotContain(" set;", output);
        Assert.Equal(1, stats.RecordStructsConverted);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS3_AutoProperty_SetBecomesInit_AndCompiles()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Y { get; private set; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        Assert.Contains("public int X { get; init; }", output);
        Assert.Contains("public int Y { get; private init; }", output);
        Assert.DoesNotContain(" set;", output);
        Assert.Equal(2, stats.MembersSetToInit);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS9_HandWrittenSetterBody_KeywordBecomesInit_BodyPreserved()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public int X
                {
                    get => _x;
                    set { _x = value; _x++; }
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("init", output);
        Assert.Contains("_x = value; _x++;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS20_RequiredMember_RequiredInitPreserved()
    {
        var src = "public record struct P { public required int X { get; set; } }";
        var (output, _, _) = Rewrite(src);
        Assert.Contains("required int X { get; init; }", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS12_StaticMutableProperty_Untouched()
    {
        var src = """
            public record struct P(int Id)
            {
                public static int Current { get; set; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public static int Current { get; set; }", output);
        Assert.Equal(1, stats.RecordStructsConverted);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS13_AlreadyInitOrGetOnly_Untouched()
    {
        var src = """
            public record struct P
            {
                public int X { get; init; }
                public int Y { get; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(1, stats.RecordStructsConverted);
        Assert.Equal(0, stats.MembersSetToInit);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS5_PublicField_BecomesInitProperty_WithInitializerAndAttribute()
    {
        var src = """
            using System.ComponentModel.DataAnnotations;
            public record struct P
            {
                [Required] public int X = 5;
                public string Name = "n";
                public P() { }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("[Required]", output);
        Assert.Contains("public int X { get; init; } = 5;", output);
        Assert.Contains("public string Name { get; init; } = \"n\";", output);
        Assert.DoesNotContain("public int X =", output);
        Assert.Equal(2, stats.FieldsToProperties);
        AssertOutputCompiles(output,
            "using System.ComponentModel.DataAnnotations;");
    }

    [Fact]
    public void RS5_MultiDeclaratorFields_AllPromoted()
    {
        var src = "public record struct P { public int X, Y; }";
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public int X { get; init; }", output);
        Assert.Contains("public int Y { get; init; }", output);
        Assert.Equal(2, stats.FieldsToProperties);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS6a_PrivateFieldCtorOnly_BecomesReadonly()
    {
        var src = """
            public record struct P
            {
                private int _tag;
                public P(int tag) { _tag = tag; }
                public int Tag => _tag;
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("private readonly int _tag;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS6b_PrivateFieldMutatedAfterCtor_InfeasibleBeforeMethodMigration()
    {
        var src = """
            public record struct P
            {
                private int _tag;
                public void Bump() { _tag++; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }

    [Fact]
    public void RS12_StaticField_Untouched()
    {
        var src = """
            public record struct P(int Id)
            {
                public static int Counter;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public static int Counter;", output);
        Assert.Equal(0, stats.FieldsToProperties);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS4_SemiAutoProperty_FieldKeyword_SetBecomesInit()
    {
        var src = """
            public record struct P
            {
                public int X { get; set => field = value > 0 ? value : 0; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public int X { get; init =>", output);
        AssertOutputCompiles(output);
    }
}
