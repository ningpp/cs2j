using System.IO;
using System.Reflection;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.EnumPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C05 Enum 声明归一预处理器单元测试（设计 §7.1）：
/// 声明分类/合成成员/九类使用点/ED020/ED010 口径/幂等/E04→C05 双 pass。
/// </summary>
public class EnumDeclarationRewriterUnitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "ed-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static (string Code, bool Changed, EnumDeclarationStatistics Stats,
        List<EnumDeclarationDiagnostic> Diags) Rewrite(
        string src, EnumDeclarationOptions? options = null)
    {
        var model = ParseWithSemantics(src);
        var pp = new EnumDeclarationPreprocessor(options ?? new EnumDeclarationOptions());
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, result.Changed, stats, diags);
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

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("ed-verify",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private const string NonIntSource = """
        using System;
        enum Small : byte { A = 1, B = 2, C = 2 }
        enum Normal { N1, N2 }
        class C
        {
            static readonly Small Keep = Small.A;
            const Small ConstField = Small.A;
            static void M(Small p)
            {
                Small a = (Small)1;
                Small d = default(Small);
                Small e = new Small();
                Small z = default;
                byte v = (byte)a;
                int vi = (int)a;
                object o = a;
                Small back = (Small)o;
                Small fromNormal = (Small)Normal.N1;
                Normal toNormal = (Normal)a;
                bool eq = a == p;
                bool neq = a != p;
                bool lt = a < p;
                bool hf = a.HasFlag(Small.B);
                string s = a.ToString();
                string fx = $"{a:X2}";
                string gd = Enum.Format(typeof(Small), a, "G");
                string nm = Enum.GetName(typeof(Small), (byte)2);
                bool def = Enum.IsDefined(typeof(Small), a);
                var vals = Enum.GetValues(typeof(Small));
                var names = Enum.GetNames(typeof(Small));
                Small one = Enum.ToObject(typeof(Small), (byte)1);
                bool sameType = Enum.GetUnderlyingType(typeof(Small)) == typeof(byte);
                switch (p)
                {
                    case Small.A: break;
                    case Small.B:
                    case Small.C:
                        Console.WriteLine("two");
                        break;
                    default:
                        break;
                }
                int n = p switch { Small.A => 1, Small.B => 2, _ => 0 };
                bool isA = p is Small.A;
                bool notA = p is not Small.A;
                bool orP = p is Small.A or Small.B;
                const Small local = Small.B;
                Small q = local;
                Console.WriteLine(v + vi + (eq?1:0) + (neq?1:0) + (lt?1:0) + (hf?1:0)
                    + s + fx + gd + nm + (def?1:0) + vals.Length + names.Length
                    + sameType + n + (isA?1:0) + (notA?1:0) + (orP?1:0) + q + d + e + z
                    + back + fromNormal + toNormal + one);
            }
        }
        """;

    [Fact]
    public void NonIntEnum_WithDuplicates_NormalizesToSealedClass_AndRewritesAllSites()
    {
        var (code, changed, stats, diags) = Rewrite(NonIntSource);

        Assert.True(changed);
        Assert.Equal(1, stats.FilesTransformed);
        Assert.Contains("sealed class Small", code);
        Assert.Contains("private readonly byte _value", code);
        Assert.Contains("public byte GetValue()", code);
        Assert.Contains("public static readonly Small DEFAULT = new Small((byte)0)", code);
        Assert.Contains("FromValue((byte)", code);
        Assert.Contains(".GetValue()", code);
        Assert.Contains("object.Equals(", code);
        Assert.Contains(".HasFlag(", code);
        Assert.DoesNotContain("[Flags]", code);
        AssertOutputCompiles(code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
    }

    [Fact]
    public void FlagsEnum_GeneratesDecomposingToString_AndFlagsParse()
    {
        var src = """
            using System;
            [Flags] enum F { None = 0, X = 1, Y = 2, Z = 4 }
            class C
            {
                static void M()
                {
                    F f = F.X | F.Y;
                    string s = f.ToString();
                    F g = Enum.Parse<F>("X, Y");
                    F g2 = Enum.Parse(typeof(F), "z", true);
                    bool ok = Enum.TryParse<F>("Z", false, out var h);
                    bool ok2 = Enum.TryParse(typeof(F), "9", true, out var h2);
                    F num = Enum.Parse<F>("3");
                    Console.WriteLine(s + g + g2 + ok + ok2 + h + h2 + num);
                }
            }
            """;
        var (code, changed, stats, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.Contains("sealed class F", code);
        Assert.Contains("StringBuilder", code);
        Assert.Contains("t.Split(',')", code);
        AssertOutputCompiles(code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        Assert.Equal(1, stats.FlagsCount);
    }

    [Fact]
    public void FlagsEnum_ToString_PrintsNamesInAscendingBitOrder_RuntimeMatchesDotNet()
    {
        // Regression: the synthesizer used StringBuilder.Append while walking
        // members in descending bit order, emitting "Z, X". .NET
        // InternalFlagsFormat consumes descending but prints ascending
        // ("X, Z"); the fix prepends each matched member.
        var src = """
            using System;
            [Flags] enum F { None = 0, X = 1, Y = 2, Z = 4 }
            class C { static void M() { F f = Enum.Parse<F>("X, Z"); Console.WriteLine(F.X | F.Z | f); } }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        AssertOutputCompiles(code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        // Descending consumption with prepend: Z insert must precede X insert.
        int zInsert = code.IndexOf("\"Z\" + \", \"", StringComparison.Ordinal);
        int xInsert = code.IndexOf("\"X\" + \", \"", StringComparison.Ordinal);
        Assert.True(zInsert >= 0 && xInsert >= 0, "both prepend inserts expected");
        Assert.True(zInsert < xInsert, "Z (bit 4) consumed and inserted before X (bit 1)");

        var asm = EmitAssembly(code, "ed-emit-flags-order-" + Guid.NewGuid().ToString("N"));
        var type = asm.GetType("F")!;
        var combo = type.GetMethod("Parse", new[] { typeof(string) })!
            .Invoke(null, new object[] { "X, Z" });
        var printed = (string)type.GetMethod("ToString", Type.EmptyTypes)!
            .Invoke(combo, null)!;
        Assert.Equal("X, Z", printed);
    }

    [Fact]
    public void ExplicitValueEnum_NormalizesWithoutFlags()
    {
        var src = """
            enum Code { Ok = 200, NotFound = 404, Error = 500 }
            class C { static int M(Code c) => c switch {
                Code.Ok => 0, Code.NotFound => 1, _ => 2 }; }
            """;
        var (code, changed, stats, diags) = Rewrite(src);
        Assert.True(changed);
        Assert.Contains("sealed class Code", code);
        Assert.Contains("200", code);
        AssertOutputCompiles(code);
        Assert.Equal(1, stats.ExplicitCount);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
    }

    [Fact]
    public void EnumApi_AndPatternSites_CompileAfterRewrite()
    {
        var src = """
            using System;
            [Flags] enum Opt { A = 1, B = 2 }
            class C
            {
                static void M(Opt? maybe)
                {
                    if (maybe is Opt.A or not Opt.B) { }
                    var q = maybe switch { Opt.A => 1, not Opt.B => 2, _ => 3 };
                    Console.WriteLine(q);
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);
        Assert.True(changed);
        AssertOutputCompiles(code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
    }

    [Fact]
    public void AttributeArgument_OnProblemEnum_IsED020_AndPlanStaysRealEnum()
    {
        var src = """
            using System;
            [Flags] enum Color { Red = 1 }
            class MyAttr : Attribute { public MyAttr(Color c) { C = c; } public Color C { get; } }
            [MyAttr(Color.Red)]
            class C { }
            """;
        var (code, changed, stats, diags) = Rewrite(src);
        Assert.False(changed);
        Assert.Contains("enum Color", code);
        Assert.Contains(diags, d => d.Severity == EnumDeclarationSeverity.Warning
            && d.Code == EnumDeclarationDiagnosticCode.ED020);
    }

    [Fact]
    public void DefaultParameterValue_OnProblemEnum_IsED020()
    {
        var src = """
            [System.Flags] enum Color { Red = 1 }
            class C { static void M(Color c = Color.Red) { } }
            """;
        var (code, changed, _, diags) = Rewrite(src);
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED020);
    }

    [Fact]
    public void GenericStructEnumConstraint_OnProblemEnum_IsED020()
    {
        var src = """
            using System;
            [Flags] enum Color { Red = 1 }
            class C
            {
                static void G<T>() where T : struct, Enum { }
                static void M() { G<Color>(); }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED020);
    }

    [Fact]
    public void NullableEnumUnwrapCast_IsED020()
    {
        var src = """
            [System.Flags] enum Color { Red = 1 }
            class C { static void M(Color? n) { Color c = (Color)n; } }
            """;
        var (code, changed, _, diags) = Rewrite(src);
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED020);
    }

    [Fact]
    public void PlainIntEnum_IsByteLevelUntouched()
    {
        var src = """
            enum Plain { One, Two, Three }
            class C { static int M(Plain p) => p switch { Plain.One => 1, _ => 0 }; }
            """;
        var (code, changed, stats, diags) = Rewrite(src);
        Assert.False(changed);
        Assert.Equal(src, code);
        Assert.Equal(0, stats.EnumsScanned);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
    }

    [Fact]
    public void Rewrite_IsIdempotent_SecondPassMakesNoChanges()
    {
        var (code1, changed1, _, _) = Rewrite(NonIntSource);
        Assert.True(changed1);

        var (code2, changed2, _, _) = Rewrite(code1);
        Assert.False(changed2);
        Assert.Equal(code1, code2);
        AssertOutputCompiles(code2);
    }

    [Fact]
    public void E04BitwisePass_ThenC05_MutationSitesDeferAndKeepRealEnum()
    {
        // P2-4：复合位赋值（&=）与 enum 算术站点不再判 ED020 回滚，而是静默
        // Deferred —— 保真 enum，由链中更晚的 numeric-boundary 族降低站点。
        var src = """
            using System;
            [Flags] enum Color { None = 0, Red = 1, Green = 2, Blue = 4 }
            class C
            {
                static void M()
                {
                    Color c = Color.Red | Color.Blue;
                    c &= Color.Green;
                    Color all = Color.Red | Color.Green | Color.Blue;
                    bool h = c.HasFlag(Color.Red);
                    Console.WriteLine(c + all + h);
                }
            }
            """;

        var e04Model = ParseWithSemantics(src);
        var e04 = new EnumPreprocessor().Preprocess(src, e04Model, new EnumPreprocessorOptions());
        Assert.True(e04.Changed);

        var c05Model = ParseWithSemantics(e04.OutputCode);
        var pp = new EnumDeclarationPreprocessor();
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(e04.OutputCode, c05Model, stats, diags);

        Assert.False(result.Changed);
        Assert.Contains("enum Color", result.OutputCode);
        Assert.DoesNotContain("sealed class Color", result.OutputCode);
        Assert.Equal(1, stats.PlansDeferred);
        // 注意：E04 已把 |= 降为 (Color)((int)…&…)，而残留的 enum + enum
        // 算术站点保留原样，等链中更晚的 numeric-boundary 族降低；此处只跑
        // E04+C05，不做 C# 可编译断言（整链由 Java e2e 慢门兜底）。
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
    }

    [Fact]
    public void E04BitwisePass_ThenC05_PureBitwiseFlagsStillConvertsToClass()
    {
        var src = """
            using System;
            [Flags] enum Color { None = 0, Red = 1, Green = 2, Blue = 4 }
            class C
            {
                static void M()
                {
                    Color c = Color.Red | Color.Blue;
                    Color all = Color.Red | Color.Green | Color.Blue;
                    bool h = c.HasFlag(Color.Red);
                    Console.WriteLine((int)c + (int)all + (h ? 1 : 0));
                }
            }
            """;

        var e04Model = ParseWithSemantics(src);
        var e04 = new EnumPreprocessor().Preprocess(src, e04Model, new EnumPreprocessorOptions());
        Assert.True(e04.Changed);

        var c05Model = ParseWithSemantics(e04.OutputCode);
        var pp = new EnumDeclarationPreprocessor();
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(e04.OutputCode, c05Model, stats, diags);

        Assert.True(result.Changed);
        Assert.Equal(0, stats.PlansDeferred);
        Assert.Contains("sealed class Color", result.OutputCode);
        Assert.Contains("FromValue", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
    }

    [Fact]
    public void ReportInfoFalse_SuppressesED001()
    {
        var src = """
            using System;
            [Flags] enum Color { Red = 1 }
            class C { static void M() { var x = Color.Red; Console.WriteLine(x); } }
            """;
        var (code, changed, _, diags) = Rewrite(src,
            new EnumDeclarationOptions { ReportInfo = false });
        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED001);
    }

    [Fact]
    public void NestedEnum_InsideGenericClass_Normalizes()
    {
        var src = """
            class Outer<T> where T : class
            {
                [System.Flags]
                public enum Kind { None = 0, A = 1, B = 2 }
                public Kind K { get; set; }
                public int M() => K switch { Kind.A => 1, Kind.B => 2, _ => 0 };
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);
        Assert.True(changed);
        AssertOutputCompiles(code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 缺陷修复 1：X 十六进制渲染（有符号窄类型负值 mask / 显式宽度）
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sbyte", "-1", "X2", "FF", "(ulong)v & 255UL")]
    [InlineData("short", "-1", "X4", "FFFF", "(ulong)v & 65535UL")]
    [InlineData("int", "-1", "X8", "FFFFFFFF", "(ulong)v & 4294967295UL")]
    [InlineData("long", "-1", "X16", "FFFFFFFFFFFFFFFF", "ulong x = (ulong)v;")]
    // 显式宽度大于 U 位宽 → 在 U 位宽表示上补前导零（sbyte -1 X4 = 00FF，.NET 实测语义）。
    [InlineData("sbyte", "-1", "X4", "00FF", "(ulong)v & 255UL")]
    // 显式宽度小于数值所需位数 → 数值需要优先（int -1 仍是 8 个 F，不被截断）。
    [InlineData("int", "-1", "X2", "FFFFFFFF", "(ulong)v & 4294967295UL")]
    // byte 正数不回归。
    [InlineData("byte", "1", "X2", "01", "(ulong)v & 255UL")]
    [InlineData("byte", "255", "X2", "FF", "(ulong)v & 255UL")]
    public void HexFormat_NegativeValues_MaskedByUnderlyingWidth_AndRuntimeMatchesDotNet(
        string u, string valueLiteral, string format, string expected, string maskShape)
    {
        var src = $$"""
            enum H : {{u}} { M = {{valueLiteral}} }
            class C { static string F(H h) => h.ToString("{{format}}"); }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.Contains(maskShape, code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);

        // 运行时行为断言：实际执行生成的 sealed class，对齐 .NET Enum X 格式输出。
        var asm = EmitAssembly(code, "ed-emit-hex-" + Guid.NewGuid().ToString("N"));
        var type = asm.GetType("H")!;
        var instance = type.GetField("M")!.GetValue(null)!;
        var actual = type.GetMethod("ToString", new[] { typeof(string) })!
            .Invoke(instance, new object?[] { format });
        Assert.Equal(expected, actual);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 缺陷修复 2：无符号 U 的 Parse/TryParse 数字 token（负数回绕 / ulong 极值）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnsignedNumericToken_NegativeOne_WrapsTo255_ForByteParseAndTryParse()
    {
        var src = """
            using System;
            enum P : byte { A = 1 }
            class C
            {
                static void M()
                {
                    P a = Enum.Parse<P>("-1");
                    bool ok = Enum.TryParse<P>("-1", false, out var b);
                    P c = Enum.Parse<P>("256");
                    bool bad = Enum.TryParse<P>("nope", false, out var d);
                    Console.WriteLine($"{a}{ok}{b}{c}{bad}{d}");
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        AssertOutputCompiles(code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        // 形状：先 long.TryParse（覆盖负数），再 ulong.TryParse（覆盖 >long.MaxValue）。
        Assert.Contains("long.TryParse(token, out long n)", code);
        Assert.Contains("ulong.TryParse(token, out ulong un)", code);
        Assert.Contains("unchecked((byte)n)", code);
        Assert.Contains("unchecked((byte)un)", code);

        var asm = EmitAssembly(code, "ed-emit-parse-byte-" + Guid.NewGuid().ToString("N"));
        var type = asm.GetType("P")!;

        var parseMinusOne = type.GetMethod("Parse", new[] { typeof(string) })!
            .Invoke(null, new object[] { "-1" });
        Assert.Equal((byte)255, type.GetMethod("GetValue")!.Invoke(parseMinusOne, null));

        var parseOverflow = type.GetMethod("Parse", new[] { typeof(string) })!
            .Invoke(null, new object[] { "256" });
        Assert.Equal((byte)0, type.GetMethod("GetValue")!.Invoke(parseOverflow, null));

        var tryParse = type.GetMethod("TryParse",
            new[] { typeof(string), typeof(bool), type.MakeByRefType() })!;
        var args = new object?[] { "-1", false, null };
        var ok = (bool)tryParse.Invoke(null, args)!;
        Assert.True(ok);
        Assert.Equal((byte)255, type.GetMethod("GetValue")!.Invoke(args[2], null));

        var badArgs = new object?[] { "nope", false, null };
        Assert.False((bool)tryParse.Invoke(null, badArgs)!);
    }

    [Fact]
    public void UnsignedNumericToken_AboveLongMax_ParsesForUlong()
    {
        const string extreme = "18446744073709551615";
        var src = $$"""
            using System;
            enum Q : ulong { A = 1 }
            class C
            {
                static void M()
                {
                    Q q = Enum.Parse<Q>("{{extreme}}");
                    bool ok = Enum.TryParse<Q>("{{extreme}}", false, out var q2);
                    Console.WriteLine($"{q}{ok}{q2}");
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        AssertOutputCompiles(code);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        Assert.Contains("long.TryParse(token, out long n)", code);
        Assert.Contains("ulong.TryParse(token, out ulong un)", code);
        Assert.Contains("unchecked((ulong)un)", code);

        var asm = EmitAssembly(code, "ed-emit-parse-ulong-" + Guid.NewGuid().ToString("N"));
        var type = asm.GetType("Q")!;

        var parsed = type.GetMethod("Parse", new[] { typeof(string) })!
            .Invoke(null, new object[] { extreme });
        Assert.Equal(ulong.MaxValue, type.GetMethod("GetValue")!.Invoke(parsed, null));

        var tryParse = type.GetMethod("TryParse",
            new[] { typeof(string), typeof(bool), type.MakeByRefType() })!;
        var args = new object?[] { extreme, false, null };
        Assert.True((bool)tryParse.Invoke(null, args)!);
        Assert.Equal(ulong.MaxValue, type.GetMethod("GetValue")!.Invoke(args[2], null));
    }

    // ────────────────────────────────────────────────────────────────────────
    // ED010：成员常量无法求值 → plan 不建立（保持真 enum）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NonConstantEnumMember_IsED010_AndPlanIsNotEstablished()
    {
        // 引用外部非常量 static 字段作 enum 成员初值（CS0133）：
        // IFieldSymbol.ConstantValue 为 null，plan 必须判 ED010 且不落地。
        var src = """
            class Other { public static int Seed = 42; }
            enum Bad : int { A = Other.Seed, B = 1 }
            class C { static void M() { System.Console.WriteLine(Bad.B); } }
            """;
        var (code, changed, stats, diags) = Rewrite(src);

        Assert.False(changed);
        Assert.Contains("enum Bad", code);
        Assert.DoesNotContain("sealed class Bad", code);
        // 成员级 ED010（含枚举名与成员名）+ plan 级 ED010（plan 不建立）各一条。
        var ed010 = diags.Where(d => d.Code == EnumDeclarationDiagnosticCode.ED010).ToList();
        Assert.Equal(2, ed010.Count);
        Assert.All(ed010, d => Assert.Equal(EnumDeclarationSeverity.Error, d.Severity));
        var memberDiag = Assert.Single(ed010, d => d.Message.Contains("member 'A'"));
        Assert.Contains("Bad", memberDiag.Message);
        Assert.Equal(0, stats.EnumsRewritten);
    }

    // ────────────────────────────────────────────────────────────────────────
    // ED040：名字契约特性盘点（JsonStringEnumConverter / EnumMember）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JsonStringEnumConverterAttribute_IsED040_WithTypeAndAttributeNames()
    {
        var src = """
            using System;
            namespace Stubs
            {
                [AttributeUsage(AttributeTargets.All)]
                public sealed class JsonConverterAttribute : Attribute
                {
                    public JsonConverterAttribute(Type t) { }
                }
                public sealed class JsonStringEnumConverter { }
            }
            [Stubs.JsonConverter(typeof(Stubs.JsonStringEnumConverter))]
            enum Color { Red = 1, Green = 2 }
            class C { static void M() { Console.WriteLine(Color.Red); } }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        AssertOutputCompiles(code);
        var ed040 = Assert.Single(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED040);
        Assert.Equal(EnumDeclarationSeverity.Warning, ed040.Severity);
        Assert.Contains("Color", ed040.Message);
        Assert.Contains("JsonConverter", ed040.Message);
        Assert.Contains("JsonStringEnumConverter", ed040.Message);
    }

    [Fact]
    public void EnumMemberAttribute_OnMember_IsED040_WithTypeMemberAndAttributeNames()
    {
        var src = """
            using System;
            namespace Stubs
            {
                [AttributeUsage(AttributeTargets.All)]
                public sealed class EnumMember : Attribute
                {
                    public string? Value { get; set; }
                }
            }
            enum Mode { [Stubs.EnumMember(Value = "on")] On = 1, Off = 2 }
            class C { static void M() { Console.WriteLine(Mode.On); } }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        AssertOutputCompiles(code);
        var ed040 = Assert.Single(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED040);
        Assert.Equal(EnumDeclarationSeverity.Warning, ed040.Severity);
        Assert.Contains("Mode", ed040.Message);
        Assert.Contains("On", ed040.Message);
        Assert.Contains("EnumMember", ed040.Message);
    }

    // ────────────────────────────────────────────────────────────────────────
    // D16：成员名撞合成内部名 → 确定性序号消解；撞固定签名方法 → ED020
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MemberNamesCollidingWithSynthesizedNames_GetDeterministicNumericSuffix()
    {
        var src = """
            enum Coll : int
            {
                GetValue = 1,
                FromValue = 2,
                DEFAULT = 3,
                _value = 4,
                Other = 5,
            }
            class C
            {
                static int M(Coll c)
                {
                    Coll d = Coll.DEFAULT;
                    return (int)c + (int)d
                        + (int)Coll.GetValue + (int)Coll.FromValue + (int)Coll._value;
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        // 合成内部名确定性加序号（Value → Value2 口径）。
        Assert.Contains("private readonly int _value2;", code);
        Assert.Contains("public int GetValue2()", code);
        Assert.Contains("public static Coll FromValue2(int value)", code);
        Assert.Contains("public static readonly Coll DEFAULT2 = new Coll((int)0);", code);
        // 原成员名保留为 static readonly 字段。
        Assert.Contains("public static readonly Coll GetValue = new Coll((int)1);", code);
        Assert.Contains("public static readonly Coll FromValue = new Coll((int)2);", code);
        Assert.Contains("public static readonly Coll DEFAULT = new Coll((int)3);", code);
        Assert.Contains("public static readonly Coll _value = new Coll((int)4);", code);
        AssertOutputCompiles(code);
    }

    [Theory]
    [InlineData("Equals")]
    [InlineData("GetHashCode")]
    [InlineData("ToString")]
    public void MemberNameCollidingWithFixedSignatureMethod_IsED020(string memberName)
    {
        var src = $"enum Kick : int {{ {memberName} = 1, Other = 2 }} class C {{ }}";
        var (code, changed, _, diags) = Rewrite(src);

        Assert.False(changed);
        Assert.Contains("enum Kick", code);
        var ed020 = Assert.Single(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED020);
        Assert.Equal(EnumDeclarationSeverity.Warning, ed020.Severity);
        Assert.Contains(memberName, ed020.Message);
    }

    // ────────────────────────────────────────────────────────────────────────
    // ED030/ED031 决策接缝：TryAttributeDiagnostic（整树验证错误归属）+
    // ExcludePlans（ED031 原子回滚重放）。适配器文件循环是机械重放，接缝在 Core。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewError_InsideOnePlanTouchedType_IsAttributedToThatPlan_ED031Path()
    {
        var src = """
            enum Alpha : byte { A1 = 1, A2 = 2 }
            enum Beta { B1 = 10, B2 = 20 }
            class Owned { Alpha a = Alpha.A1; Beta b = Beta.B1; }
            """;
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create("ed-attr",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var pp = new EnumDeclarationPreprocessor();
        pp.BindCompilation(compilation);
        Assert.Equal(2, pp.Plans.Count);

        var root = tree.GetRoot();
        var alphaMember = root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>()
            .First(m => m.Identifier.Text == "A1");
        var diag = SyntheticError(tree, alphaMember.Identifier.Span);

        var attributed = pp.TryAttributeDiagnostic(
            diag, new Dictionary<string, string> { [tree.FilePath] = tree.FilePath },
            out var planId);
        Assert.True(attributed);
        Assert.Equal("Alpha", planId);
    }

    [Fact]
    public void NewError_OutsideAnyPlanTouch_IsNotAttributed_ED030Path()
    {
        var src = """
            enum Alpha : byte { A1 = 1, A2 = 2 }
            enum Beta { B1 = 10, B2 = 20 }
            class Owned { Alpha a = Alpha.A1; Beta b = Beta.B1; }
            """;
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create("ed-attr2",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var pp = new EnumDeclarationPreprocessor();
        pp.BindCompilation(compilation);

        var root = tree.GetRoot();
        var owned = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var diag = SyntheticError(tree, owned.Identifier.Span);

        var attributed = pp.TryAttributeDiagnostic(
            diag, new Dictionary<string, string> { [tree.FilePath] = tree.FilePath },
            out var planId);
        Assert.False(attributed);
        Assert.Equal(string.Empty, planId);
    }

    [Fact]
    public void ExcludePlans_ReplaysThatEnumAsRealEnum_AndOutputStillCompiles_ED031Effect()
    {
        var src = """
            enum Alpha : byte { A1 = 1, A2 = 2 }
            enum Beta { B1 = 10, B2 = 20 }
            class Owned { Alpha a = Alpha.A1; Beta b = Beta.B1; }
            """;
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create("ed-excl",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var pp = new EnumDeclarationPreprocessor();
        pp.BindCompilation(compilation);
        pp.ExcludePlans(new[] { "Alpha" });

        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(src, compilation.GetSemanticModel(tree), stats, diags);

        Assert.True(result.Changed);
        // ED031 回滚效果：被排除 plan 原子回到真 enum，其余 plan 仍归一，整树可编译。
        Assert.Contains("enum Alpha", result.OutputCode);
        Assert.DoesNotContain("sealed class Alpha", result.OutputCode);
        Assert.Contains("sealed class Beta", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 回归：case 标签/模式本身是 enum pass 降级形态 (E)((int)E.A | (int)E.B) 时，
    // 语义决策必须先于 Visit 捕获，否则对游离节点 GetSymbolInfo 抛
    // "Syntax node is not within syntax tree"（Uri.cs L3147 崩溃）。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchStatement_WithEnumPassLoweredCaseLabel_CapturesSemanticsBeforeVisit()
    {
        var src = """
            using System;
            [Flags] enum E { None = 0, A = 1, B = 2, C = 4 }
            class C
            {
                static int M(E p)
                {
                    int r;
                    switch (p)
                    {
                        case (E)((int)E.A | (int)E.B):
                            r = 3;
                            break;
                        case E.C:
                            r = 4;
                            break;
                        default:
                            r = -1;
                            break;
                    }
                    return r;
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.DoesNotContain("switch (", code);
        Assert.DoesNotContain("case ", code);
        // 折叠常量位值（A|B=3、C=4）在 if 链谓词中。
        Assert.Contains("== 3", code);
        Assert.Contains("== 4", code);
        Assert.Contains(".GetValue()", code);
    }

    [Fact]
    public void SwitchExpression_WithEnumPassLoweredConstantArm_CapturesSemanticsBeforeVisit()
    {
        var src = """
            using System;
            [Flags] enum E { None = 0, A = 1, B = 2, C = 4 }
            class C
            {
                static int M(E p) => p switch
                {
                    (E)((int)E.A | (int)E.B) => 3,
                    E.C => 4,
                    _ => 0
                };
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.DoesNotContain("switch", code);
        Assert.Contains("== 3", code);
        Assert.Contains("== 4", code);
    }

    [Fact]
    public void IsPattern_WithEnumPassLoweredConstant_CapturesSemanticsBeforeVisit()
    {
        var src = """
            using System;
            [Flags] enum E { None = 0, A = 1, B = 2, C = 4 }
            class C
            {
                static bool M(E p) => p is (E)((int)E.A | (int)E.B);
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("== 3", code);
    }

    [Fact]
    public void ConstField_OfConvertedEnum_WithLiteralCastInitializer_IsDowngradedToStaticReadonly()
    {
        // 回归 Uri.cs V1ToStringUnescape：enum 变 class 后 const 非法（CS0133），
        // 即使初值是纯字面量 cast 也必须降级（DeclarationReferencesActiveEnum
        // 不能只检查枚举成员引用）。
        var src = """
            using System;
            [Flags] enum E { None = 0, A = 1 }
            class C
            {
                const E Sentinel = (E)0x7FFF;
                static int Use() => (int)Sentinel;
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        Assert.DoesNotContain("const ", code);
        Assert.Contains("static readonly E Sentinel", code);
        Assert.Contains("FromValue((int)(0x7FFF))", code);
        AssertOutputCompiles(code);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 跨枚举成员初值耦合：enum D { X = S.M } 保持真 enum 时，S 也不能转换
    // （S.M 变 static readonly → D 的常量初值 CS0029；XmlSubtreeReader.State→
    // ReadState ED030 回归）。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForcedRollback_ReferencingEnumExcluded_ForcesReferencedEnumBack()
    {
        var src = """
            enum S { Sa = 10, Sb = 20 }
            enum D { Da = S.Sa, Db = S.Sb }
            class C { static void M() { int x = (int)D.Da + (int)S.Sb; } }
            """;
        var pp = new EnumDeclarationPreprocessor();
        pp.BindCompilation(ParseWithSemantics(src).Compilation);
        Assert.Equal(2, pp.Plans.Count);

        var forced = pp.ComputeForcedRollbacks(new[] { "D" });

        Assert.Equal(new[] { "S" }, forced);
    }

    [Fact]
    public void ForcedRollback_IsTransitiveAcrossInitializerChain()
    {
        var src = """
            enum C { Ca = 1 }
            enum B { Ba = C.Ca, Bb = 2 }
            enum A { Aa = B.Ba }
            class X { static void M() { int x = (int)A.Aa + (int)B.Ba + (int)C.Ca; } }
            """;
        var pp = new EnumDeclarationPreprocessor();
        pp.BindCompilation(ParseWithSemantics(src).Compilation);

        var forced = pp.ComputeForcedRollbacks(new[] { "A" });

        Assert.Equal(new[] { "B", "C" }, forced.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ForcedRollback_InfeasibleReferencingEnum_ForcesReferencedEnumBack_FromRoundOne()
    {
        // D 因固定签名成员名冲突 ED020 不可行（保持真 enum），首轮就必须连带 S 回滚。
        var src = """
            enum S { Sa = 10 }
            enum D { Equals = S.Sa }
            class C { static void M() { int x = (int)D.Equals; } }
            """;
        var pp = new EnumDeclarationPreprocessor();
        pp.BindCompilation(ParseWithSemantics(src).Compilation);

        var dPlan = Assert.Single(pp.Plans.Where(p => p.Id == "D"));
        Assert.False(dPlan.Feasible);
        var forced = pp.ComputeForcedRollbacks(Array.Empty<string>());

        Assert.Equal(new[] { "S" }, forced);
    }

    [Fact]
    public void ForcedRollback_BothEnumsConvert_NoForcedPlan_AndOutputCompiles()
    {
        var src = """
            using System;
            enum S { Sa = 10 }
            enum D { Da = S.Sa }
            class C { static void M() { Console.WriteLine(D.Da); } }
            """;
        var pp = new EnumDeclarationPreprocessor();
        var model = ParseWithSemantics(src);
        pp.BindCompilation(model.Compilation);

        Assert.Empty(pp.ComputeForcedRollbacks(Array.Empty<string>()));

        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        // 依赖方转换后初值由编译期常量折叠，输出中不残留 S.Sa 引用。
        Assert.DoesNotContain("S.Sa", result.OutputCode);
    }

    [Fact]
    public void ForcedRollback_ReferencedEnumKeptReal_E2E_RewriteLeavesBothEnumsAndCompiles()
    {
        var src = """
            enum S { Sa = 10, Sb = 20 }
            enum D { Da = S.Sa, Db = S.Sb }
            class C { static int M() => (int)D.Da; }
            """;
        var pp = new EnumDeclarationPreprocessor();
        var model = ParseWithSemantics(src);
        pp.BindCompilation(model.Compilation);
        pp.ExcludePlans(new[] { "D" });
        pp.ExcludePlans(pp.ComputeForcedRollbacks(new[] { "D" }));

        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);

        Assert.False(result.Changed);
        Assert.Contains("enum S", result.OutputCode);
        Assert.Contains("enum D", result.OutputCode);
        Assert.DoesNotContain("sealed class", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ────────────────────────────────────────────────────────────────────────
    // switch → if-else 链降级时的跨 case 段局部作用域（CS0103 'result' 回归，
    // CompiledXPathExpr.Compare 形态）。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Switch_LoweredToIfChain_HoistsLocalSharedAcrossSections()
    {
        var src = """
            using System;
            enum Kind { Text = 0, Number = 1, Other = 2 }
            class C
            {
                int Compare(int x, int y, Kind k)
                {
                    switch (k)
                    {
                        case Kind.Text:
                            int result = x + y;
                            if (result != 0) return result;
                            result = x - y;
                            return result;
                        case Kind.Number:
                            result = x.CompareTo(y);
                            return result;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.DoesNotContain("switch (", code);
        // 声明提升到 if 链之外，原位置变为赋值。
        Assert.Contains("int result;", code);
        Assert.Contains("result = x + y;", code);
        Assert.Contains("result = x.CompareTo(y);", code);
    }

    [Fact]
    public void Switch_LoweredToIfChain_HoistedVarLocal_ResolvedToConcreteType()
    {
        var src = """
            using System;
            enum Kind { Text = 0, Number = 1, Other = 2 }
            class C
            {
                int M(int x, int y, Kind k)
                {
                    switch (k)
                    {
                        case Kind.Text:
                            var result = x.CompareTo(y);
                            return result;
                        case Kind.Number:
                            result = y;
                            return result;
                        default:
                            return 0;
                    }
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("int result;", code);
        Assert.DoesNotContain("var result", code);
    }

    [Fact]
    public void Switch_LoweredToIfChain_MultiVariableDecl_OnlyCrossReferencedOneHoisted()
    {
        var src = """
            using System;
            enum Kind { A = 0, B = 1, C = 2 }
            class P
            {
                int M(Kind k)
                {
                    switch (k)
                    {
                        case Kind.A:
                            int a = 1, b = 2;
                            return a + b;
                        case Kind.B:
                            a = 3;
                            return a;
                        default:
                            return 0;
                    }
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("int a;", code);
        Assert.Contains("int b = 2", code);
    }

    [Fact]
    public void Switch_LocalFunctionReferencedFromAnotherSection_IsED020()
    {
        var src = """
            using System;
            enum Kind { Text = 0, Number = 1, Other = 2 }
            class C
            {
                int M(Kind k)
                {
                    switch (k)
                    {
                        case Kind.Text:
                            int Local() => 1;
                            return Local();
                        case Kind.Number:
                            return Local();
                        default:
                            return 0;
                    }
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.False(changed);
        Assert.Contains("enum Kind", code);
        var ed020 = Assert.Single(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED020);
        Assert.Equal(EnumDeclarationSeverity.Warning, ed020.Severity);
    }

    [Fact]
    public void Switch_ConstLocalReferencedFromAnotherSection_IsED020()
    {
        var src = """
            using System;
            enum Kind { Text = 0, Number = 1, Other = 2 }
            class C
            {
                int M(Kind k)
                {
                    switch (k)
                    {
                        case Kind.Text:
                            const int Z = 5;
                            return Z;
                        case Kind.Number:
                            return Z;
                        default:
                            return 0;
                    }
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.False(changed);
        Assert.Contains("enum Kind", code);
        Assert.Contains(diags, d => d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Severity == EnumDeclarationSeverity.Warning);
    }

    // ────────────────────────────────────────────────────────────────────────
    // == / != 重写为 object.Equals 时，右操作数尾随的单行注释（//…）不能把合成
    // 的闭合括号注释掉（CS1026/CS1002 → ED031 本地解析失败 → ED030 整轮回滚，
    // XPathParser.IsStep 形态）。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Comparison_TrailingLineCommentOnRightOperand_CommentMovedAfterCallAndCompiles()
    {
        var src = """
            using System;
            enum Kind { A = 1, B = 2, C = 3 }
            class P
            {
                bool IsStep(Kind lexKind)
                {
                    return (
                        lexKind == Kind.A ||
                        lexKind == Kind.B ||
                        lexKind == Kind.C          // NodeTest is also Name
                    );
                }
                bool NotStep(Kind lexKind)
                {
                    return lexKind != Kind.A      // anything but A
                        ;
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("object.Equals(lexKind, Kind.C)", code);
        Assert.Contains("!object.Equals(lexKind, Kind.A)", code);
        // 注释没有丢，而是挂在合成调用之后，且后面的 token 仍能被解析。
        Assert.Contains("// NodeTest is also Name", code);
        Assert.Contains("// anything but A", code);
    }

    [Fact]
    public void CastAndEnumApi_TrailingLineCommentOnEmbeddedOperand_DoesNotSwallowSynthesizedParen()
    {
        var src = """
            using System;
            enum Kind { A = 1, B = 2 }
            class P
            {
                string NameOf(object o)
                {
                    Kind a = (Kind)o          // unboxed enum
                        ;
                    int v = (int)a            // underlying value
                        ;
                    return Enum.GetName(typeof(Kind), 1   // the value to name
                        ) + v + a;
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("// unboxed enum", code);
        Assert.Contains("// underlying value", code);
        Assert.Contains("// the value to name", code);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 值类型 const（字段/局部）初始化器整体是编译期常量时，折叠成字面量并保留
    // const——它可能正被 case 标签等常量语境引用，降级 static readonly 会
    // CS9135/CS0182（ctestbase.TEST_PASS 形态）。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConstField_IntegerInitializerReferencingEnum_FoldedToLiteral_StaysConst_AndCaseLabelsCompile()
    {
        var src = """
            using System;
            enum Status { Failed = 0, Passed = 1, NotRun = 2 }
            abstract class C
            {
                public const int TEST_FAIL = (int)Status.Failed;
                public const int TEST_PASS = (int)Status.Passed;
                public const int TEST_SKIPPED = (int)Status.NotRun;

                int Handle(int result)
                {
                    switch (result)
                    {
                        case TEST_PASS:
                        case TEST_SKIPPED:
                            return result;
                        default:
                            return TEST_FAIL;
                    }
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("const int TEST_PASS = 1;", code);
        Assert.Contains("const int TEST_FAIL = 0;", code);
        Assert.Contains("const int TEST_SKIPPED = 2;", code);
        Assert.DoesNotContain("static readonly int TEST", code);
        Assert.Contains("case TEST_PASS:", code);
    }

    [Fact]
    public void ConstLocal_FoldedToLiteral_StaysConst()
    {
        var src = """
            enum Status { Failed = 0, Passed = 1 }
            class C
            {
                int M()
                {
                    const int Passed = (int)Status.Passed;
                    const bool Ok = Status.Passed == (Status)1;
                    return Passed + (Ok ? 1 : 0);
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("const int Passed = 1;", code);
        Assert.Contains("const bool Ok = true;", code);
    }

    // ────────────────────────────────────────────────────────────────────────
    // null/default 实参的重载消歧：归一 enum 变 class 后，原本只匹配引用类型
    // 参数重载的 null 在两个重载间 CS0121（XmlPreloadedResolver 形态）。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NullArgument_ConstructorInitializer_AmbiguousAfterEnumClassNormalization_GetsExplicitCast()
    {
        var src = """
            using System;
            [Flags]
            enum Dtds { None = 0, Xhtml = 1, Rss = 2, All = 3 }
            class Resolver { }
            class Preloaded
            {
                public Preloaded()
                    : this(null)
                {
                }

                public Preloaded(Dtds dtds)
                    : this(null, dtds, null)
                {
                }

                public Preloaded(Resolver fallback)
                    : this(fallback, Dtds.All, null)
                {
                }

                public Preloaded(Resolver fallback, Dtds dtds, object tag)
                {
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        // 默认构造函数的 this(null) 原本绑定 Resolver 重载，补 cast 消歧。
        Assert.Contains("this((global::Resolver)null)", code);
    }

    [Fact]
    public void NullArgument_MethodAndObjectCreation_AmbiguousAfterNormalization_GetsExplicitCast()
    {
        var src = """
            using System;
            [Flags]
            enum Mode { None = 0, Fast = 1 }
            class Box { }
            class Worker
            {
                void Run()
                {
                    Configure(null);
                    var w = new Worker(null);
                }
                void Configure(Mode mode) { }
                void Configure(Box box) { }
                Worker() { }
                Worker(Mode mode) { }
                Worker(Box box) { }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.Contains("Configure((global::Box)null)", code);
        Assert.Contains("new Worker((global::Box)null)", code);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 隐式常量表达式转换：字面量 0 可出现在任意 enum 类型位置（字段初始化、
    // 三元分支、return、比较）；归一为 class 后替换为默认实例（CS0029 回归，
    // DataTypeImplementation.RestrictionFlags 形态）。
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ZeroLiteral_AtEnumTypedSites_ReplacedWithDefaultInstance_AndCompiles()
    {
        var src = """
            using System;
            [Flags]
            enum RestrictionFlags { None = 0, Pattern = 1, WhiteSpace = 2 }
            class Facets
            {
                internal RestrictionFlags Flags = 0;
                internal RestrictionFlags FixedFlags = 0;

                RestrictionFlags Pick(Facets other)
                {
                    RestrictionFlags flags = other != null ? other.Flags : 0;
                    if (flags != 0)
                    {
                        return 0;
                    }
                    return flags;
                }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.True(changed);
        Assert.DoesNotContain(diags, d => d.Severity == EnumDeclarationSeverity.Error);
        AssertOutputCompiles(code);
        Assert.DoesNotContain("= 0;", code);
        Assert.Contains("other.Flags : global::RestrictionFlags.DEFAULT", code);
        Assert.Contains("return global::RestrictionFlags.DEFAULT;", code);
        Assert.Contains("object.Equals(flags, global::RestrictionFlags.DEFAULT)", code);
    }

    [Fact]
    public void ZeroLiteral_AtAttributeArgumentAndDefaultValue_IsED020()
    {
        var src = """
            using System;
            [Flags]
            enum Mode { None = 0, Fast = 1 }
            [AttributeUsage(AttributeTargets.All)]
            sealed class MarkAttribute : Attribute
            {
                public MarkAttribute(Mode mode) { }
            }
            class C
            {
                [Mark(0)]
                void M(Mode mode = 0) { }
            }
            """;
        var (code, changed, _, diags) = Rewrite(src);

        Assert.False(changed);
        Assert.Contains("enum Mode", code);
        var ed020Sites = diags.Where(d => d.Code == EnumDeclarationDiagnosticCode.ED020).ToList();
        Assert.Contains(ed020Sites, d => d.Severity == EnumDeclarationSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InfeasiblePlan_ED020_IsAlwaysAWarning_SafeRollbackNotFailure(bool strict)
    {
        var src = """
            enum Color { Red = 1, Green = 2 }
            [AttributeUsage(AttributeTargets.All)]
            sealed class MyAttrAttribute : Attribute
            {
                public MyAttrAttribute(Color c) { }
            }
            [MyAttr(Color.Red)]
            class C { }
            """;

        var (code, _, _, diags) =
            Rewrite(src, new EnumDeclarationOptions { Strict = strict });
        Assert.Contains("enum Color", code);
        Assert.All(diags.Where(d => d.Code == EnumDeclarationDiagnosticCode.ED020),
            d => Assert.Equal(EnumDeclarationSeverity.Warning, d.Severity));
    }

    [Fact]
    public void Enum_Used_As_Attribute_Ctor_Property_Field_Type_Stays_Native_Without_Application()
    {
        // CS0181 structural gate: even when the attribute is never applied in
        // the analyzed sources (so no attribute-argument site exists), an enum
        // typed as an attribute ctor parameter / property / field cannot be
        // classed into a sealed type.
        var src = """
        using System;
        namespace N
        {
            [Flags]
            public enum Monikers { A = 1, B = 2, C = 4 }

            [AttributeUsage(AttributeTargets.Method)]
            public sealed class SkipAttribute : Attribute
            {
                public SkipAttribute(Monikers frameworks) { }
                public Monikers Preferred { get; set; }
                public Monikers Legacy;
            }
        }
        """;
        var (code, changed, _, _) = Rewrite(src);
        Assert.False(changed);
        Assert.Contains("enum Monikers", code);
        Assert.DoesNotContain("sealed class Monikers", code);
        AssertOutputCompiles(code);
    }

    [Fact]
    public void Ambiguously_Resolved_Enum_In_Attribute_Argument_Keeps_Plan_Enum_Native()
    {
        // Mirrors TargetFrameworkMonikers used as a SkipOnTargetFramework
        // argument while a same-named attribute/enum pair imported from
        // another namespace makes the reference ambiguous: the
        // uniquely-symbol-based argument gate cannot see it, so the
        // ambiguity gate must roll both candidate plans back to real enums.
        // The attribute ctor here takes object so the declaration-shape gate
        // does not cover it.
        var src1 = """
        namespace N1
        {
            [System.Flags]
            public enum Kind { A = 1, B = 2, C = 4 }
            public sealed class MarkAttribute : System.Attribute
            {
                public MarkAttribute(object o) { }
            }
        }
        """;
        var src2 = """
        namespace N2
        {
            [System.Flags]
            public enum Kind { A = 1, B = 2, C = 4 }
            public sealed class MarkAttribute : System.Attribute
            {
                public MarkAttribute(object o) { }
            }
        }
        """;
        var src3 = """
        using N1;
        using N2;
        class C
        {
            [Mark(Kind.A)]
            void M() { }
        }
        """;
        var trees = new[] { src1, src2, src3 }
            .Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create("ed-ambig-attr",
            trees, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var pp = new EnumDeclarationPreprocessor();
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = pp.RewriteFile(
            src1, compilation.GetSemanticModel(trees[0]), stats, diags);

        Assert.False(result.Changed);
        Assert.Contains("enum Kind", result.OutputCode);
        Assert.DoesNotContain("sealed class Kind", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Uninitialized_Field_Local_And_AutoProperty_Of_Converted_Enum_Seed_Default_Instance()
    {
        // A native enum is a value type: uninitialized fields/locals start at
        // the zero member. The class form is a reference type whose default is
        // null, which NREs on the first GetValue()/Equals call (Uri._flags).
        // Non-nullable fields (class containers), locals and auto-properties
        // must be seeded with E.DEFAULT; nullable ones must stay null.
        var src = """
        using System;
        [Flags]
        enum E : byte { None = 0, A = 1, B = 2 }
        class C
        {
            private E _f;
            private static E? _nf;
            public E Auto { get; set; }
            public E Assigned { get; set; } = E.A;

            void M()
            {
                E x;
                Assign(out x);
                Console.WriteLine(x);
            }
            static void Assign(out E x) { x = E.A; }
        }
        abstract class Abs
        {
            // Abstract/extern properties have no backing field here: an
            // initializer would be CS8050, so they must stay unseeded.
            public abstract E AbstractProp { get; }
            public E ConcreteAuto { get; set; }
        }
        """;
        var (code, changed, _, diags) = Rewrite(src);
        Assert.True(changed, string.Join("\n", diags.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("_f = global::E.DEFAULT;", code);
        Assert.Contains("E x = global::E.DEFAULT;", code);
        Assert.Contains("public E Auto { get; set; } = global::E.DEFAULT;", code);
        Assert.Contains("public E ConcreteAuto { get; set; } = global::E.DEFAULT;", code);
        Assert.DoesNotContain("AbstractProp { get; } =", code);
        // Nullable keeps the faithful null default; initialized decls untouched.
        Assert.DoesNotContain("_nf =", code);
        Assert.Contains("Assigned { get; set; } = E.A;", code);
        AssertOutputCompiles(code);

        var asm = EmitAssembly(code, "ed-zerodefault");
        var cType = asm.GetType("C")!;
        var instance = Activator.CreateInstance(cType);
        var fieldValue = cType.GetField(
            "_f", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
        Assert.NotNull(fieldValue);
        Assert.Equal((byte)0, fieldValue!.GetType().GetMethod("GetValue")!.Invoke(fieldValue, null));
        Assert.Null(cType.GetField(
            "_nf", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        var autoValue = cType.GetProperty("Auto")!.GetValue(instance);
        Assert.NotNull(autoValue);
        Assert.Equal((byte)0, autoValue!.GetType().GetMethod("GetValue")!.Invoke(autoValue, null));
    }

    [Fact]
    public void Zero_Initialized_Array_Of_Converted_Enum_Is_Filled_With_Default_Instance()
    {
        // UnicodeCharHelper pattern: a native 'new E[n]' leaves every element at
        // the zero member; the class form leaves nulls, so a read-modify-write
        // like 'table[c] |= flag' NREs on the first access. Zero-initialized
        // 1-D arrays must route through the synthesized zero-filling factory,
        // while explicitly initialized arrays are left intact.
        var src = """
        using System;
        [Flags]
        enum E : byte { None = 0, A = 1, B = 2 }
        class C
        {
            public static E[] MakeTable() => new E[8];
            public static E[] Explicit => new E[] { E.A, E.B };
        }
        """;
        var (code, changed, _, diags) = Rewrite(src);
        Assert.True(changed, string.Join("\n", diags.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains("global::E.__Cs2jNewArray(8)", code);
        // An explicitly initialized array is not rerouted to the factory.
        Assert.DoesNotContain("__Cs2jNewArray()", code);
        Assert.Contains("new E[] { E.A, E.B }", code);
        AssertOutputCompiles(code);

        var asm = EmitAssembly(code, "ed-arrayzero");
        var eType = asm.GetType("E")!;
        var factory = eType.GetMethod("__Cs2jNewArray")!;
        var array = (Array)factory.Invoke(null, new object[] { 8 })!;
        Assert.Equal(8, array.Length);
        var getValue = eType.GetMethod("GetValue")!;
        foreach (var element in array)
        {
            Assert.NotNull(element);
            Assert.Equal((byte)0, getValue.Invoke(element, null));
        }
    }

    private static Diagnostic SyntheticError(SyntaxTree tree, TextSpan span)
    {
        var descriptor = new DiagnosticDescriptor(
            "CS9999", "synthetic", "synthetic whole-tree validation error",
            "test", DiagnosticSeverity.Error, isEnabledByDefault: true);
        return Diagnostic.Create(descriptor, Location.Create(tree, span));
    }

    private static Assembly EmitAssembly(string code, string assemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(assemblyName,
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "failed to emit generated assembly:\n" +
            string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)) +
            "\n--- output ---\n" + code);
        return Assembly.Load(ms.ToArray());
    }
}
