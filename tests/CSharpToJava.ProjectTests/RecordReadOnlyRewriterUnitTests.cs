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

    internal static SemanticModel ParseWithSemantics(string src,
        LanguageVersion? version = null, params string[] extra)
    {
        var options = version is { } v ? new CSharpParseOptions(v) : ParseOptions;
        var tree = CSharpSyntaxTree.ParseText(src, options);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, options)).ToArray();
        var compilation = CSharpCompilation.Create(
            "rr-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    internal static (string Output, RecordReadOnlyStatistics Stats,
        List<RecordReadOnlyDiagnostic> Diagnostics) Rewrite(string src,
        RecordReadOnlyOptions? options = null, LanguageVersion? version = null,
        params string[] extra)
    {
        var model = ParseWithSemantics(src, version, extra);
        var pp = new RecordReadOnlyPreprocessor(options ?? new RecordReadOnlyOptions());
        var stats = new RecordReadOnlyStatistics();
        var diags = new List<RecordReadOnlyDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
    }

    /// <summary>
    /// 把 metadataSource 单独编成程序集引用，src 编译只引用其元数据——
    /// 用于 IR-7“接口不在源码树内（BCL/三方形态）”场景。
    /// </summary>
    /// <remarks>ToMetadataReference 保留源符号位置，须 Emit 真实镜像。</remarks>
    internal static (string Output, RecordReadOnlyStatistics Stats,
        List<RecordReadOnlyDiagnostic> Diagnostics) RewriteWithMetadata(
            string src, string metadataSource, RecordReadOnlyOptions? options = null)
    {
        var libComp = CSharpCompilation.Create(
            "rr-external-lib.dll",
            new[] { CSharpSyntaxTree.ParseText(metadataSource, ParseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        // 必须 Emit 成真实 PE 镜像：ToMetadataReference 保留源符号位置，IsInTree 会误判。
        var peImage = new MemoryStream();
        var emitResult = libComp.Emit(peImage);
        Assert.True(emitResult.Success,
            "metadata lib failed to emit: " +
            string.Join("; ", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        peImage.Position = 0;
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "rr-test",
            new[] { tree },
            GetReferences().Append(MetadataReference.CreateFromStream(peImage)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var pp = new RecordReadOnlyPreprocessor(options ?? new RecordReadOnlyOptions());
        var stats = new RecordReadOnlyStatistics();
        var diags = new List<RecordReadOnlyDiagnostic>();
        var result = pp.RewriteFile(src, compilation.GetSemanticModel(tree), stats, diags);
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

    /// <summary>断言 output 包含指定文本，忽略所有空白字符差异（Formatter 可将
    /// `with` 表达式展开为多行）。比较时移除全部空白后做子串匹配。</summary>
    private static void AssertContainsIgnoringWhitespace(string expected, string output)
    {
        var normExpected = expected.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var normOutput = output.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
        Assert.True(normOutput.Contains(normExpected, StringComparison.Ordinal),
            $"Expected output to contain (ignoring whitespace): \"{expected}\"\n" +
            $"--- output ---\n{output}");
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
    public void RS6b_PrivateFieldMutatedAfterCtor_FeasibleWithMethodMigration()
    {
        var src = """
            public record struct P
            {
                private int _tag;
                public void Bump() { _tag++; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains("readonly record struct", output);
        Assert.DoesNotContain(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
        AssertOutputCompiles(output);
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

    [Fact]
    public void RS10_InstanceEvent_BlocksWithRR040()
    {
        var src = """
            public record struct P
            {
                public event System.Action? Changed;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR040);
    }

    [Fact]
    public void RS11_VolatileField_BlocksWithRR041()
    {
        var src = "public record struct P { public volatile int X; }";
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR041);
    }

    [Fact]
    public void RS11_VisibleRefReturningMember_BlocksWithRR041()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public ref int XRef() => ref _x;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR041);
    }

    [Fact]
    public void RS19_SettableIndexer_BlocksWithRR042()
    {
        var src = """
            public record struct P
            {
                public int this[int i] { get => i; set { } }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR042);
    }

    [Fact]
    public void RS17_ExplicitLayoutWithPromotedField_BlocksWithRR043()
    {
        var src = """
            using System.Runtime.InteropServices;
            [StructLayout(LayoutKind.Explicit)]
            public record struct P
            {
                [FieldOffset(0)] public int X;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR043);
    }

    [Fact]
    public void RS7_ConstructorAssignments_RemainLegal_AfterConversion()
    {
        var src = """
            public record struct P
            {
                public int X { get; init; }
                public P(int x) { X = x; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void GenericAndNestedRecords_ConvertIndependently()
    {
        var src = """
            public record struct Container<T>
            {
                public record struct Inner(T Value);
                public T Data { get; init; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("readonly record struct Container<T>", output);
        Assert.Contains("readonly record struct Inner", output);
        Assert.Equal(2, stats.RecordStructsConverted);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RS15_ExplicitReadOnlyInterfaceImplementation_Untouched()
    {
        var src = """
            public record struct P : System.IComparable<P>
            {
                public int X { get; init; }
                public int CompareTo(P other) => X.CompareTo(other.X);
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("readonly record struct P", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void RecordClass_NotConverted_WhenOptionOff()
    {
        var src = "public record P(int X);";
        var (output, stats, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record", output);
        Assert.Equal(0, stats.RecordClassesConverted);
        Assert.Equal(0, stats.RecordStructsConverted);
    }

    [Fact]
    public void RS16_XmlDocAndAttributes_PreservedOnProperty()
    {
        var src = """
            public record struct P
            {
                /// <summary>X doc.</summary>
                [System.Obsolete]
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("/// <summary>X doc.</summary>", output);
        Assert.Contains("[System.Obsolete]", output);
        Assert.Contains("init;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_LocalReceiver_BecomesWithAssign()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                void M()
                {
                    var p = new P();
                    p.X = 1;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 1 };", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_ValueParameterReceiver_BecomesWithAssign()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public void Update(P p) { p.X = 42; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 42 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_FieldAndSettablePropertyReceiver_Writable()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                private P _other;
                public P Holder { get; set; }
                public void M() { _other.X = 1; Holder.X = 2; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("_other = _other with { X = 1 };", output);
        Assert.Contains("Holder = Holder with { X = 2 };", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_ArrayElementReceiver_BecomesWithAssign()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C { public void M(P[] arr) { arr[0].X = 9; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("arr[0] = arr[0] with { X = 9 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS1_TupleElementReceiver_BecomesWithAssign()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M((P a, int b) t) { t.a.X = 3; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("t.a = t.a with { X = 3 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS8_InsideLambda_LocalCaptured_Writable()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M()
                {
                    var p = new P();
                    Action a = () => { p.X = 7; };
                    a();
                }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 7 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS9_RefParameterReceiver_Writable()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void Update(ref P p) { p.X = 5; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 5 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS13_ForeachIterationVariable_BlocksWithRR050()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P { public int X { get; set; } }
            public class C { public void M(List<P> list) { foreach (var p in list) p.X = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("with", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS1_InParameterReceiver_BlocksWithRR050()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public void Update(in P p) { p.X = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS1_GetOnlyReceiver_BlocksWithRR050()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public P Get() => default; public void M() { Get().X = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS1_ReceiverWithSideEffect_BlocksWithRR051()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public P[] Items = Array.Empty<P>();
                public int F() => 0;
                public void M() { Items[F()].X = 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR051);
    }

    [Fact]
    public void MS3_CompoundAssignment_BecomesWithReadModifyWrite()
    {
        var src = """
            public record struct P { public int X { get; set; } public string? S { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X += 5;
                    p.X -= 2;
                    p.S ??= "x";
                }
            }
            """;
        var model = ParseWithSemantics(src);
        var pp = new RecordReadOnlyPreprocessor();
        pp.BindCompilation(model.Compilation);
        foreach (var plan in pp.Plans)
        {
            _output.WriteLine($"Plan: {plan.RecordQualifiedName}, Outcome: {plan.Outcome}, Sites: {plan.Sites.Count}, FailCode: {plan.FailCode}");
            foreach (var s in plan.Sites)
                _output.WriteLine($"  Site: Kind={s.Site.Kind}, WriteNode={s.Site.WriteNode}, Receiver={s.Site.Receiver}, Left={s.Site.Left}");
        }
        var (output, stats, _) = Rewrite(src);
        _output.WriteLine($"OUTPUT:\n{output}");
        _output.WriteLine($"MutationSitesRewritten: {stats.MutationSitesRewritten}");
        Assert.Contains("p = p with { X = p.X + 5 };", output);
        Assert.Contains("p = p with { X = p.X - 2 };", output);
        Assert.Contains("p = p with { S = p.S ?? \"x\" };", output);
        Assert.Equal(3, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS3_ImpureGetter_BlocksWithRR052()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public int X { get => _x + Next(); set => _x = value; }
                private static int Next() => 1;
            }
            public class C { public void M(P p) { p.X += 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR052);
    }

    [Fact]
    public void MS4_IncrementStatement_BecomesWithPlusOne()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X++;
                    ++p.X;
                    p.X--;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = p.X + 1 };", output);
        Assert.Contains("p = p with { X = p.X - 1 };", output);
        Assert.Equal(3, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS4_IncrementInExpressionPosition_BlocksWithRR053()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public int M(P p) => p.X++; }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MS2_AdjacentSameReceiverAssignments_MergeIntoOneWith()
    {
        var src = """
            public record struct P { public int X { get; set; } public int Y { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X = 1;
                    p.Y = 2;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 1, Y = 2 };", output);
        Assert.DoesNotContain("p = p with { X = 1 };", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS2_ReadAfterWriteAcrossStatements_DoesNotMerge()
    {
        var src = """
            public record struct P { public int X { get; set; } public int Y { get; set; } }
            public class C
            {
                public void M(P p)
                {
                    p.X = 1;
                    p.Y = p.X;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        // 不合并（合并会让 p.Y 读到旧值）；逐条 with 改写。
        Assert.Contains("p = p with { X = 1 };", output);
        Assert.Contains("p = p with { Y = p.X };", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS6_ChainedAssignment_SplitsInnerToOuterWithTemp()
    {
        var src = """
            public record struct A { public int M { get; set; } }
            public record struct B { public int M { get; set; } }
            public class C
            {
                public void M(A a, B b)
                {
                    a.M = b.M = Compute();
                }
                private static int Compute() => 1;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        // 右侧有方法调用，必须只求值一次（临时变量），顺序 b → a。
        Assert.Contains("var __cs2jT", output);
        Assert.Contains("b = b with { M = __cs2jT", output);
        Assert.Contains("a = a with { M = __cs2jT", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS6_ChainedAssignment_InnerRecordSkipped_KeepsPlainAssignment()
    {
        var src = """
            public record struct A { public int M { get; set; } }
            public record struct B { public int N { get; set; } public int M { get; set; } }
            public class C
            {
                // B 因含事件等原因无法转换时仍可变：内层保留普通赋值，外层 with 取值。
                public void M(A a, B b, int v)
                {
                    a.M = b.M = v;
                }
            }
            """;
        var options = new RecordReadOnlyOptions
        {
            SkipQualifiedNames = new HashSet<string>(StringComparer.Ordinal) { "B" },
        };
        var (output, stats, _) = Rewrite(src, options);
        Assert.Contains("b.M = __cs2jT", output);
        Assert.Contains("a = a with { M = __cs2jT", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS5_NullConditionalAssign_OnNullableStruct_BecomesSwitch()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C { public void M(P? p) { p?.X = 1; } }
            """;
        var (output, stats, _) = Rewrite(src, version: LanguageVersion.Preview);
        Assert.Contains("p = p switch", output);
        Assert.Contains("null => null", output);
        Assert.Contains("{ } q => q with { X = 1 }", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS5_NullConditionalCompoundAndIncrement_UseQ()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P? p)
                {
                    p?.X += 2;
                    p?.X++;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src, version: LanguageVersion.Preview);
        Assert.Contains("{ } q => q with { X = q.X + 2 }", output);
        Assert.Contains("{ } q => q with { X = q.X + 1 }", output);
        Assert.Equal(2, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS5_NullConditionalReceiverWithSideEffect_BlocksWithRR051()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public P? Get() => null;
                public void M() { Get()?.X = 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src, version: LanguageVersion.Preview);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR051);
    }

    [Fact]
    public void MS7_ListElement_Writable()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P { public int X { get; set; } }
            public class C { public void M(List<P> list) { list[2].X = 4; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("list[2] = list[2] with { X = 4 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS7_SpanElement_RefIndexer_Writable()
    {
        var src = """
            using System;
            public record struct P { public int X { get; set; } }
            public class C { public void M(Span<P> span) { span[0].X = 4; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("span[0] = span[0] with { X = 4 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS7_ReadOnlyList_BlocksWithRR054()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P { public int X { get; set; } }
            public class C { public void M(IReadOnlyList<P> list) { list[0].X = 4; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR054);
    }

    [Fact]
    public void MS17_NestedChain_AllRecordContainers_RecursiveWith()
    {
        var src = """
            public record struct A { public B B { get; set; } }
            public record struct B { public int C { get; set; } }
            public class C2
            {
                public void M(A a) { a.B.C = 8; }
            }
            """;
        var (output, stats, diags) = Rewrite(src);
        _output.WriteLine($"OUTPUT:\n{output}");
        _output.WriteLine($"Stats: MutationSitesRewritten={stats.MutationSitesRewritten}");
        foreach (var d in diags)
            _output.WriteLine($"Diag: {d.Code} {d.Message}");
        Assert.Contains("a = a with { B = a.B with { C = 8 } };", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS17_NestedChain_RootPlainSettable_PlainOuterWrite()
    {
        var src = """
            public record struct B { public int C { get; set; } }
            public class Holder { public B B { get; set; } }
            public class C2
            {
                public void M(Holder h) { h.B.C = 8; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("h.B = h.B with { C = 8 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS17_GetOnlyIntermediateOnNonRecord_BlocksWithRR050()
    {
        var src = """
            public record struct B { public int C { get; set; } }
            public class Holder { public B B { get; } }
            public class C2
            {
                public void M(Holder h) { h.B.C = 8; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MS17_NestedChainReceiverSideEffect_BlocksWithRR051()
    {
        var src = """
            public record struct A { public B B { get; set; } }
            public record struct B { public int C { get; set; } }
            public class C2
            {
                public A Get() => default;
                public void M() { Get().B.C = 8; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR051);
    }

    [Fact]
    public void MS17_CompoundOnNestedChain_StaysPendingRR020()
    {
        var src = """
            public record struct A { public B B { get; set; } }
            public record struct B { public int C { get; set; } }
            public class C2
            {
                public void M(A a) { a.B.C += 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR020);
    }

    // ----- Task 15: MS-10/MS-11/MS-12/MS-14/MS-16/MS-18 -----

    [Fact]
    public void MS10_InTreeRefRecordArgument_CallSiteUntouched_BodyRewritten()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void Update(ref P p) { p.X = 3; }
                public void Call()
                {
                    var p = new P();
                    Update(ref p);
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 3 };", output);
        Assert.Contains("Update(ref p);", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS10_OutParameterAssignedThenMemberWritten_Works()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void Create(out P p)
                {
                    p = new P();
                    p.X = 9;
                }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p with { X = 9 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS10_ExternalRefRecordArgument_BlocksWithRR055()
    {
        var src = """
            using System.Threading;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M()
                {
                    var p = new P();
                    Interlocked.Exchange(ref p, default);
                }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR055);
    }

    [Fact]
    public void MS11_RefRecordMemberToExternal_BlocksWithRR056()
    {
        var src = """
            using System.Threading;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P p) { Interlocked.Increment(ref p.X); }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR056);
    }

    [Fact]
    public void MS12_ExistingWithExpressions_AreUntouched()
    {
        var src = """
            public record struct P { public int X { get; set; } public int Y { get; set; } }
            public class C
            {
                public P M(P p)
                {
                    var q = p with { X = 1 };
                    return q with { Y = 2, X = 3 };
                }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("p with { X = 1 }", output);
        Assert.Equal(0, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS14_DynamicReceiver_BlocksAllRecordsWithRR057()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(dynamic d) { d.X = 1; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR057);
    }

    [Fact]
    public void MS14_ReflectionSetValue_BlocksAllRecordsWithRR057()
    {
        var src = """
            using System.Reflection;
            public record struct P { public int X { get; set; } }
            public class C
            {
                public void M(P p, PropertyInfo prop) { prop.SetValue(p, 1); }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR057);
    }

    [Fact]
    public void MS16_MutationInsideTupleExpression_BlocksWithRR053()
    {
        var src = """
            public record struct P { public int X { get; set; } }
            public class C
            {
                public (int, int) M(P p) => ((p.X = 1), 2);
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MS18_SelfDeclaredIndexer_RecordBlockedAtDeclarationRR042()
    {
        var src = """
            public record struct R
            {
                public int this[int i] { get => 0; set { } }
            }
            public class C { public void M(R r) { r[0] = 1; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR042);
    }

    // ----- Task 16: MM-1..MM-6 void method migration -----

    [Fact]
    public void MM_VoidMutatingMethod_PromotesAndReturnsRecord()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Bump() { X = 1; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public P Bump()", output);
        Assert.Contains("var __x = this.X;", output);
        Assert.Contains("__x = 1;", output);
        AssertContainsIgnoringWhitespace("return this with { X = __x };", output);
        Assert.Equal(1, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_ThisAssignment_BecomesPromotedCopies()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Reset(P other) { this = other; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public P Reset(P other)", output);
        Assert.Contains("__x = other.X;", output);
        AssertContainsIgnoringWhitespace("return this with { X = __x };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_CallToMigratedMethodOnThis_AbsorbsResultIntoPromoted()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A()
                {
                    X = 1;
                    B();
                }
                public void B() { X = 2; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = this.B();", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_CtorCallingMigratedMethod_ThisWritebackAfterAssigned()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public P(int x) { X = x; Bump(); }
                public void Bump() { X += 1; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("this = this.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_ExternalStatementCallSite_WritebackReceiver()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Bump() { X = 1; }
            }
            public class C
            {
                public void M(P p) { p.Bump(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_ExternalCallOnReadonlyReceiver_BlocksWithRR050()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Bump() { X = 1; }
            }
            public class C
            {
                public P P { get; }
                public void M() { P.Bump(); }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void MM_MigratedBody_ExternalMigratedCallGetsWriteback()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A(P other) { X = 1; other.Bump(); }
                public void Bump() { X = 2; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("__x = 1;", output);
        Assert.Contains("other = other.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM_MigratedBody_ExternalMutationSiteStillRewrittenWith()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A(P other) { X = 1; other.X = 2; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("other = other with { X = 2 };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM6_IteratorMethod_BlocksWithRR060()
    {
        var src = """
            using System.Collections.Generic;
            public record struct P
            {
                public int X { get; set; }
                public IEnumerable<int> Evens()
                {
                    X = 1;
                    yield return X;
                }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR060);
    }

    [Fact]
    public void MM6_PartialMethod_BlocksWithRR061()
    {
        var src = """
            public partial record struct P
            {
                partial void Bump();
            }
            public partial record struct P
            {
                public int X { get; set; }
                partial void Bump() { X = 1; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR061);
    }

    [Fact]
    public void MM6_CallGraphCycle_BlocksWithRR062()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void A() { X = 1; B(); }
                public void B() { X = 2; A(); }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR062);
    }

    [Fact]
    public void MM6_GetterMutatingMember_BlocksWithRR063()
    {
        var src = """
            public record struct P
            {
                private int _x;
                public int X
                {
                    get { _x = 1; return _x; }
                    set { _x = value; }
                }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR063);
    }

    [Fact]
    public void MM6_ThisAssignmentWithGetOnlyState_BlocksWithRR064()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Y { get; }
                public P(int y) { Y = y; }
                public void Reset(P other) { this = other; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR064);
    }

    // ----- Task 17: MM-5 non-void out-result migration + RR053/RR065 -----

    [Fact]
    public void MM5_NonVoidDiscarded_BecomesOutDiscardWriteback()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { p.Next(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public P Next(out int result)", output);
        Assert.Contains("p = p.Next(out _);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidSingleDeclarator_BecomesOutVarWriteback()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { var v = p.Next(); System.Console.WriteLine(v); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var v);", output);
        Assert.DoesNotContain("var v = p.Next();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidNestedInArgument_HoistsTempBeforeStatement()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { Use(p.Next()); }
                private static void Use(int v) { }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var __o0);", output);
        Assert.Contains("Use(__o0);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidInReturn_HoistsTemp()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public int M(P p) { return p.Next(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var __o0);", output);
        Assert.Contains("return __o0;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidInIfCondition_HoistsIntoBlock()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public bool Ready() { X = 1; return X > 0; }
            }
            public class C
            {
                public void M(P p) { if (p.Ready()) { Tick(); } }
                private static void Tick() { }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Ready(out var __o0);", output);
        Assert.Contains("if (__o0)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_NonVoidInFieldInitializer_BlocksWithRR053()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public P _p;
                public int Field = new P().Next();
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MM5_NonVoidInExpressionBodiedMember_BlocksWithRR053()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                private P _p;
                public int V => new P().Next();
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MM5_NonVoidInMultiDeclarator_BlocksWithRR053()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
            }
            public class C
            {
                public void M(P p) { int a = 1, b = p.Next(); }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR053);
    }

    [Fact]
    public void MM5_MigratedBody_AbsorbsNonVoidThisCallWithOutVar()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Next() { X += 1; return X; }
                public int A() { var z = Next(); return z; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("public P A(out int result)", output);
        Assert.Contains("var __r0 = this.Next(out var z);", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Contains("result = z;", output);
        AssertContainsIgnoringWhitespace("return this with { X = __x };", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_ExpressionBodiedNonVoidMethod_ExpandedToResultAssignment()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public int Bump() => X += 1;
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public P Bump(out int result)", output);
        Assert.Contains("result = __x += 1;", output);
        AssertContainsIgnoringWhitespace("return this with { X = __x };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM5_CtorDiscardsNonVoidCall_ThisWritebackOutDiscard()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public P(int x) { X = x; Next(); }
                public int Next() { X += 1; return X; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("this = this.Next(out _);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM6_ObjectOverrideMutating_BlocksWithRR065()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public override string ToString() { X = 1; return X.ToString(); }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR065);
    }

    [Fact]
    public void MM7_VoidExtension_ReturnsRecordAndPromotesReceiver()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X += 1; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public static P Bump(this P p)", output);
        Assert.Contains("var __x = p.X;", output);
        Assert.Contains("__x += 1;", output);
        AssertContainsIgnoringWhitespace("return p with { X = __x };", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_VoidExtension_ReducedCallGetsWriteback()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X += 1; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { p.Bump(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_VoidExtension_StaticQualifiedCallGetsWriteback()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X += 1; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { E.Bump(p); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = E.Bump(p);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_VoidExtension_StaticUnqualifiedCallInsideSameClassGetsWriteback()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X = 1; }
                public static void Other(P p) { Bump(p); }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = Bump(p);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_NonVoidExtension_OutResultAfterThisParameter()
    {
        var src = """
            public static class E
            {
                public static int Next(this P p) { p.X += 1; return p.X; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { var v = p.Next(); }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public static P Next(this P p, out int result)", output);
        Assert.Contains("p = p.Next(out var v);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_NonVoidExtension_NestedCallHoists()
    {
        var src = """
            public static class E
            {
                public static int Next(this P p) { p.X += 1; return p.X; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            public class C
            {
                public void M(P p) { Use(p.Next()); }
                private static void Use(int v) { }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("p = p.Next(out var __o0);", output);
        Assert.Contains("Use(__o0);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_ExtensionBody_ReceiverReadsSubstituted()
    {
        var src = """
            public static class E
            {
                public static void Set(this P p, int v) { p.X = p.X + v; }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("var __x = p.X;", output);
        Assert.Contains("__x = __x + v;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_ExtensionBody_AbsorbsMigratedInstanceCall()
    {
        var src = """
            public record struct P
            {
                public int X { get; set; }
                public void Inc() { X += 1; }
            }
            public static class E
            {
                public static void Bump(this P p) { p.Inc(); }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = p.Inc();", output);
        Assert.Contains("__x = __r0.X;", output);
        AssertContainsIgnoringWhitespace("return p with { X = __x };", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_InstanceBody_AbsorbsReducedExtensionCall()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X = 1; }
            }
            public record struct P
            {
                public int X { get; set; }
                public void A() { this.Bump(); }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = this.Bump();", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MM7_ExtensionBody_AbsorbsReducedExtensionCall()
    {
        var src = """
            public static class E
            {
                public static void A(this P p) { p.X = 1; }
                public static void B(this P p) { p.A(); }
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("var __r0 = p.A();", output);
        Assert.Contains("__x = __r0.X;", output);
        Assert.Equal(2, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void MS15_MethodGroupBindingToMigratedExtension_BlocksWithRR058()
    {
        var src = """
            public static class E
            {
                public static void Bump(this P p) { p.X = 1; }
                public static System.Action<P> Make() => Bump;
            }
            public record struct P
            {
                public int X { get; set; }
            }
            """;
        var (_, _, diags) = Rewrite(src);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR058);
    }

    [Fact]
    public void MM7_ExtensionOnNonRecord_Unchanged()
    {
        var src = """
            public static class S
            {
                public static int Len(this string s) => s.Length;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(src, output);
        Assert.Equal(0, stats.MethodsMigrated);
    }
    [Fact]
    public void IR1_InterfaceGainsWithMember_RecordGetsStubsAndPublicWith()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        // IR-1：既有成员不动，追加抽象 With 成员（int 经 UseSpecialTypes 保持关键字形态）。
        Assert.Contains("int P { get; set; }", output);
        Assert.Contains("global::I WithP(int value);", output);
        // IR-2：公共属性 set→init；旧接口契约显式桩 get 转发 / set throw。
        Assert.Contains("public int P { get; init; }", output);
        Assert.Contains("int global::I.P", output);
        Assert.Contains("set => throw new global::System.NotSupportedException(", output);
        // IR-2：显式 With 委托公共强类型版；公共版 with 出口（避免装箱）。
        Assert.Contains("global::I global::I.WithP(int value) => this.WithP(value);", output);
        AssertContainsIgnoringWhitespace("public global::R WithP(int value) => this with { P = value };", output);
        Assert.Equal(1, stats.InterfaceRipples);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_RecordTypedReceiver_RewrittenToPublicWithCall()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(R r) { r.P = 5; } }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("r = r.WithP(5);", output);
        Assert.DoesNotContain("r = r with", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_InterfaceTypedReceiver_RewrittenToInterfaceWithCall()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(I i) { i.P = 5; } }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("i = i.WithP(5);", output);
        Assert.Equal(1, stats.MutationSitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_InterfaceReceiverInParam_BlocksWithRR050()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(in I i) { i.P = 5; } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("WithP(5)", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void IR2_TwoInterfacesSameProperty_DedupPublicWith()
    {
        var src = """
            public interface I1 { int P { get; set; } }
            public interface I2 { int P { get; set; } }
            public record struct R : I1, I2 { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        // IR-1：两个接口各加一份 WithP。
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(output,
            @"global::I[12] WithP\(int value\);").Count);
        // IR-2：每接口一份显式桩与显式 With。
        Assert.Contains("int global::I1.P", output);
        Assert.Contains("int global::I2.P", output);
        Assert.Contains("global::I1 global::I1.WithP(int value) => this.WithP(value);", output);
        Assert.Contains("global::I2 global::I2.WithP(int value) => this.WithP(value);", output);
        // 公共强类型版同形状跨接口只生成一份。
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(output,
            @"public global::R WithP\(int value\)").Count);
        Assert.Equal(2, stats.InterfaceRipples);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR7_WithNameAlreadyDeclared_RR071AtomicSkip()
    {
        var src = """
            public interface I { int P { get; set; } int WithP(int value); }
            public record struct R : I
            {
                public int P { get; set; }
                public int WithP(int value) => default;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR071);
    }

    [Fact]
    public void IR7_ExternalInterface_RR070AtomicSkip()
    {
        var lib = "public interface IExt { int P { get; set; } }";
        var src = "public record struct R : IExt { public int P { get; set; } }";
        var (output, _, diags) = RewriteWithMetadata(src, lib);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR070);
    }

    [Fact]
    public void IR1_CrossFile_RecordSideGeneratedWhenInterfaceLivesInOtherTree()
    {
        // CLI 逐文件驱动时接口文件会由它自己的 RewriteFile 调用补上 WithP；
        // 此处只验证 record 文件一侧在跨树语义模型下正确产出（接口追加在 M6 工程级测试整树验证）。
        var extra = "public interface I { int P { get; set; } }";
        var src = """
            public record struct R : I { public int P { get; set; } }
            public class C { public void M(R r) { r.P = 1; } }
            """;
        var (output, stats, _) = Rewrite(src, null, null, extra);
        AssertContainsIgnoringWhitespace("public global::R WithP(int value) => this with { P = value };", output);
        Assert.Contains("r = r.WithP(1);", output);
        Assert.Equal(0, stats.InterfaceRipples); // WithP 追加落在接口文件，不计入本文件
        Assert.Equal(1, stats.FilesTransformed);
    }
    [Fact]
    public void IR3_ClassImplementer_GetsFluentExplicitFill()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class C : I { public int P { get; set; } }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("global::I WithP(int value);", output);
        Assert.Contains("global::R WithP(int value)", output);
        AssertContainsIgnoringWhitespace(
            "global::I global::I.WithP(int value) { P = value; return this; }", output);
        // 1 个接口新增 + 1 个其他实现者 fill；record 侧显式桩不计入 InterfaceRipples。
        Assert.Equal(2, stats.InterfaceRipples);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_StructImplementer_PublicProperty_GetsFluentFill()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public struct S : I { public int P { get; set; } }
            """;
        var (output, _, _) = Rewrite(src);
        AssertContainsIgnoringWhitespace(
            "global::I global::I.WithP(int value) { P = value; return this; }", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_ClassWithExplicitProperty_CallsOldContractThroughInterfaceCast()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public class E : I
            {
                private int _p;
                int I.P { get => _p; set => _p = value; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("((global::I)this).P = value;", output);
        Assert.Contains("return this;", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_UnconvertedRecordClass_GetsFluentFill()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public record class Q : I { public int P { get; set; } }
            """;
        var (output, _, _) = Rewrite(src,
            new RecordReadOnlyOptions { ConvertRecordClasses = false });
        Assert.Contains("public int P { get; set; }", output);
        Assert.Contains("global::I global::I.WithP(int value)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_StructExplicitProperty_BlocksWithRR070()
    {
        var src = """
            public interface I { int P { get; set; } }
            public record struct R : I { public int P { get; set; } }
            public struct Bad : I
            {
                private int _p;
                int I.P { get => _p; set => _p = value; }
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR070);
    }

    [Fact]
    public void IR1_VoidInterfaceMethod_AddsWithAndMigratesRecordMethod()
    {
        var src = """
            public interface I { void Bump(); }
            public record struct R : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
            }
            public class C { public void Use(I i, R r) { i.Bump(); r.Bump(); } }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Contains("global::I WithBump();", output);
        Assert.Contains("public R Bump()", output);
        Assert.Contains("void global::I.Bump()", output);
        Assert.Contains("global::I global::I.WithBump() => Bump();", output);
        Assert.Contains("i = i.WithBump();", output);
        Assert.Contains("r = r.Bump();", output);
        Assert.Equal(1, stats.MethodsMigrated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR1_NonVoidInterfaceMethod_UsesOutResultShape()
    {
        var src = """
            public interface I { int Next(); }
            public record struct R : I
            {
                public int X { get; set; }
                public int Next() => ++X;
            }
            public class C
            {
                public void Use(I i, R r)
                {
                    var a = i.Next();
                    var b = r.Next();
                }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("global::I WithNext(out int __result);", output);
        Assert.Contains("public R Next(out int result)", output);
        Assert.Contains("int global::I.Next()", output);
        Assert.Contains("global::I global::I.WithNext(out int __result) => Next(out __result);", output);
        Assert.Contains("i = i.WithNext(out var a);", output);
        Assert.Contains("r = r.Next(out var b);", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR2_ExplicitInterfaceMethod_PromotedToPublicStrongMethod()
    {
        var src = """
            public interface I { void Bump(); }
            public record struct R : I
            {
                public int X { get; set; }
                void I.Bump() { X++; }
            }
            """;
        var (output, _, _) = Rewrite(src);
        Assert.Contains("public R Bump()", output);
        Assert.DoesNotContain("void global::I.Bump() {", output);
        Assert.Contains("void global::I.Bump() => throw new global::System.NotSupportedException(", output);
        Assert.Contains("global::I global::I.WithBump() => Bump();", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR3_OtherClassImplementer_GetsVoidAndNonVoidMethodFills()
    {
        var src = """
            public interface I { void Bump(); int Next(); }
            public record struct R : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
                public int Next() => ++X;
            }
            public class C : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
                public int Next() => ++X;
            }
            """;
        var (output, _, _) = Rewrite(src);
        AssertContainsIgnoringWhitespace(
            "global::I global::I.WithBump() { Bump(); return this; }", output);
        AssertContainsIgnoringWhitespace(
            "global::I global::I.WithNext(out int __result) { __result = Next(); return this; }",
            output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IR4_InterfaceMethodReceiverInParam_BlocksWithRR050()
    {
        var src = """
            public interface I { void Bump(); }
            public record struct R : I { public int X { get; set; } public void Bump() { X++; } }
            public class C { public void Use(in I i) { i.Bump(); } }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("WithBump()", output.Replace("void global::I.Bump", string.Empty));
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR050);
    }

    [Fact]
    public void IR7_MethodWithNameCollision_BlocksWithRR071()
    {
        var src = """
            public interface I { void Bump(); int WithBump(); }
            public record struct R : I
            {
                public int X { get; set; }
                public void Bump() { X++; }
                public int WithBump() => 0;
            }
            """;
        var (output, _, diags) = Rewrite(src);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR071);
    }

    [Fact]
    public void IR7_ExternalInterfaceMethod_BlocksWithRR070()
    {
        var lib = "public interface IExt { void Bump(); }";
        var src = """
            public record struct R : IExt
            {
                public int X { get; set; }
                public void Bump() { X++; }
            }
            """;
        var (output, _, diags) = RewriteWithMetadata(src, lib);
        Assert.DoesNotContain("readonly record struct", output);
        Assert.Contains(diags, d => d.Code == RecordReadOnlyDiagnosticCode.RR070);
    }
}









