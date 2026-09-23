using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// 数值字面量擦除安全化的单元测试:十进制无符号越界值转十六进制、十进制前导零
/// 去除,其余形态逐字保留,且改写前后每个字面量的 C# 类型与常量值不变。
/// </summary>
public class UnsignedLiteralRewriterUnitTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("unsigned-literal-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    private static string Rewrite(string src)
    {
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed || result.OutputCode == src, "rewrite output must round-trip");
        return result.OutputCode;
    }

    // ---- 十进制 u 系后缀越界 → 十六进制(同后缀) ----

    [Theory]
    [InlineData("4294967295u", "0xFFFFFFFFu")]
    [InlineData("2147483648u", "0x80000000u")]
    [InlineData("4294967295U", "0xFFFFFFFFU")]
    [InlineData("3_221_225_219u", "0xBFFFFF03u")]
    [InlineData("18446744073709551615ul", "0xFFFFFFFFFFFFFFFFul")]
    [InlineData("18446744073709551615UL", "0xFFFFFFFFFFFFFFFFUL")]
    [InlineData("18446744073709551615Ul", "0xFFFFFFFFFFFFFFFFUl")]
    [InlineData("18446744073709551615u", "0xFFFFFFFFFFFFFFFFu")]
    [InlineData("9223372036854775808uL", "0x8000000000000000uL")]
    [InlineData("10000000000000000000lu", "0x8AC7230489E80000lu")]
    [InlineData("9223372036854775808L", "0x8000000000000000L")]      // Roslyn: dec+L 超 long.MaxValue 定型为 ulong
    [InlineData("18446744073709551615L", "0xFFFFFFFFFFFFFFFFL")]
    [InlineData("9223372036854775809", "0x8000000000000001")]         // dec 无后缀高值同样按 ulong 接受
    public void UnsignedOutOfRangeDecimal_RewrittenToHex(string literal, string expected)
    {
        var output = Rewrite($"public class C {{ public object M() {{ return {literal}; }} }}");
        Assert.Contains($"return {expected};", output);
    }

    // ---- 可表示范围 / 兼容形态:逐字保留 ----

    [Theory]
    [InlineData("42u")]            // uint,十进制在 int 范围内
    [InlineData("2147483647u")]    // uint,恰好 int.MaxValue
    [InlineData("4294967296u")]    // u 后缀溢出为 ulong,十进制在 long 范围内
    [InlineData("5000000000u")]    // 同上
    [InlineData("42ul")]
    [InlineData("9223372036854775807ul")]  // ulong,恰好 long.MaxValue
    [InlineData("42")]
    [InlineData("5000000000")]     // 无后缀 long
    [InlineData("9223372036854775807L")]
    [InlineData("0xFFFFFFFFu")]    // 十六进制恒可表示
    [InlineData("0xFFFFFFFF")]     // 十六进制无后缀(uint)
    [InlineData("0xFFFFFFFFFFFFFFFFul")]
    [InlineData("0xFFFFFFFFFFFFFFFF")]  // 十六进制无后缀(ulong)
    [InlineData("0b1010u")]
    [InlineData("0X1Fu")]
    [InlineData("0B11ul")]
    [InlineData("0xFF_FFu")]
    [InlineData("1.5f")]
    [InlineData(".5")]
    [InlineData("1e3")]
    [InlineData("1.5E-3d")]
    [InlineData("2.5m")]
    [InlineData("3F")]
    [InlineData("4d")]
    [InlineData("007.5")]          // 实数前导零:Java 浮点无八进制问题
    [InlineData("1_000.5")]
    public void RepresentableLiteral_KeptVerbatim(string literal)
    {
        var src = $"public class C {{ public object M() {{ return {literal}; }} }}";
        var output = Rewrite(src);
        Assert.Contains($"return {literal};", output);
    }

    // ---- 十进制整数前导零 → 去除(Java 八进制陷阱) ----

    [Theory]
    [InlineData("042", "42")]
    [InlineData("0042", "42")]
    [InlineData("007u", "7u")]
    [InlineData("004294967295u", "0xFFFFFFFFu")]   // 前导零 + 越界,一并处理
    [InlineData("00L", "0L")]
    [InlineData("000", "0")]
    public void LeadingZeroDecimal_StripLeadingZeros(string literal, string expected)
    {
        var output = Rewrite($"public class C {{ public object M() {{ return {literal}; }} }}");
        Assert.Contains($"return {expected};", output);
    }

    [Fact]
    public void ZeroLiteral_KeptVerbatim()
    {
        var output = Rewrite("public class C { public object M() { return 0; } }");
        Assert.Contains("return 0;", output);
    }

    // ---- 字符/字符串/字符类别不处理 ----

    [Fact]
    public void CharAndStringLiterals_Untouched()
    {
        var src = "public class C { public char A = 'u'; public string B = \"042\"; public string S = $\"x{4294967295u}y\"; }";
        var output = Rewrite(src);
        Assert.Contains("'u'", output);
        Assert.Contains("\"042\"", output);
        // 插值表达式里的数值字面量仍要改写,插值文本部分保持。
        Assert.Contains("$\"x{0xFFFFFFFFu}y\"", output);
    }

    // ---- 语义保持:类型 + 常量值逐字面量相等 ----

    [Fact]
    public void LiteralTypeAndValue_PreservedAcrossRewrite()
    {
        var src = """
            public class C
            {
                public uint A = 4294967295u;
                public ulong B = 18446744073709551615ul;
                public uint D = 2147483648u;
                public ulong E = 10000000000000000000u;
                public object M()
                {
                    var x = 4_294_967_295u;
                    var y = 042;
                    return (x, y);
                }
            }
            """;
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Equal(CollectLiterals(src), CollectLiterals(result.OutputCode));
    }

    /// <summary>用 Roslyn 语义模型收集每个数值字面量的(类型, 值),作为等价性基准。</summary>
    private static List<(SpecialType type, string value)> CollectLiterals(string src)
    {
        var (tree, model) = ParseWithSemantics(src);
        return tree.GetRoot().DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(n => n.IsKind(SyntaxKind.NumericLiteralExpression))
            .Select(n => (model.GetTypeInfo(n).Type?.SpecialType ?? SpecialType.None, n.Token.ValueText))
            .ToList();
    }

    // ---- 常量上下文保持合法 ----

    [Fact]
    public void ConstAndEnumAndAttributeLiterals_StillValidConstants()
    {
        var src = """
            public class C
            {
                public const uint Max = 4294967295u;
                public enum E : ulong { Big = 18446744073709551615ul }
                [System.Obsolete(null, false)]
                public void M(byte[] buffer = null)
                {
                    const uint local = 2147483648u;
                    switch (local)
                    {
                        case 4294967295u:
                            break;
                    }
                    var sized = new byte[042];
                }
            }
            """;
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed);

        var reparsed = CSharpSyntaxTree.ParseText(result.OutputCode);
        Assert.Empty(reparsed.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var recompilation = CSharpCompilation.Create("const-check",
            new[] { reparsed },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(recompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error
            && d.Id is not "CS0518" and not "CS1701"));

        // 常量折叠结果不变:重写后的常量仍是 4294967295 / ulong.MaxValue。
        var retype = recompilation.GetSemanticModel(reparsed);
        var field = retype.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "Max");
        var constant = retype.GetConstantValue(field.Initializer!.Value);
        Assert.True(constant.HasValue);
        Assert.Equal(4294967295u, constant.Value);
        Assert.Equal(4294967295u, constant);
    }

    // ---- 与既有运算符重写的组合 ----

    [Fact]
    public void LongMinValueUnaryMinusSpecialCase_StillFoldsAfterRewrite()
    {
        // C#/Java 共同特例:-9223372036854775808L(long.MinValue)。十六进制形态
        // -0x8000000000000000L 在两语言同样成立,改写不破坏折叠。
        var src = "public class C { public long Min = -9223372036854775808L; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("-0x8000000000000000L", result.OutputCode);

        var reparsed = CSharpSyntaxTree.ParseText(result.OutputCode);
        var recompilation = CSharpCompilation.Create("min-check",
            new[] { reparsed },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(recompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error
            && d.Id is not "CS0518" and not "CS1701"));
        var field = recompilation.GetSemanticModel(reparsed).SyntaxTree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "Min");
        var constant = recompilation.GetSemanticModel(reparsed).GetConstantValue(field.Initializer!.Value);
        Assert.True(constant.HasValue);
        Assert.Equal(long.MinValue, constant.Value);
    }

    [Fact]
    public void HighLiteralAsOperatorOperand_NormalizedInsideHelperCall()
    {
        var output = Rewrite("public class C { public uint M(uint u) { return u + 4000000000u; } }");
        // 4000000000 = 0xEE6B2800
        Assert.Contains("unchecked((int)0xEE6B2800u)", output);
    }

    // ---- 仅字面量(无 unsigned 关键字)的文件也要被处理 ----

    [Fact]
    public void LiteralOnlyFile_IsRewritten()
    {
        var (tree, model) = ParseWithSemantics("public class C { public object M() { return 4294967295u; } }");
        var result = new UnsignedTypeRewriter().Rewrite("public class C { public object M() { return 4294967295u; } }", model);
        Assert.True(result.Changed);
        Assert.Contains("0xFFFFFFFFu", result.OutputCode);
        Assert.Equal(1, result.Statistics.LiteralsRewritten);
    }

    [Fact]
    public void CleanFileWithoutUnsafeLiterals_StillClean()
    {
        var src = "public class C { public int Value; public object M() { return 42 + 1.5 + 0xFF; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void DisabledViaOption_LeavesLiteralsAlone()
    {
        var src = "public class C { public object M() { return 4294967295u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(
            src, model, new UnsignedTypeRewriterOptions(EnableLiteralRewrite: false));
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }
}
