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
        Assert.Contains(diags, d => d.Severity == EnumDeclarationSeverity.Error
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
        Assert.Equal(EnumDeclarationSeverity.Error, ed020.Severity);
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
