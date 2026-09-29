using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Operators;
using CSharpToJava.Core.Transformers.Member;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>C14 运算符去糖 Core 单测矩阵（设计 §7.1）：内联源码 → RewriteFile →
/// 忽略空白断言 + AssertOutputCompiles；harness 复用 C11 属性单测（internal 同程序集）。</summary>
public class MemberDesugarOperatorUnitTests
{
    internal static (string OutputCode, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diagnostics)
        Rewrite(string src, MemberDesugarOptions? options = null, params string[] extra)
        => MemberDesugarPropertyUnitTests.Rewrite(src, options, extra);

    internal static void AssertOutputCompiles(string output, params string[] extra)
        => MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, extra);

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    internal static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    internal static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    // ---- 决策 1/1b：ConvertOperatorName 与 OpSymbolToJavaName 的 PascalCase 逐名对齐 ----

    [Theory]
    [InlineData("op_Addition", "Add")]
    [InlineData("op_Subtraction", "Subtract")]
    [InlineData("op_Multiply", "Multiply")]
    [InlineData("op_Division", "Divide")]
    [InlineData("op_Modulus", "Mod")]
    [InlineData("op_Equality", "ValueEquals")]
    [InlineData("op_Inequality", "NotEquals")]
    [InlineData("op_GreaterThan", "GreaterThan")]
    [InlineData("op_LessThan", "LessThan")]
    [InlineData("op_GreaterThanOrEqual", "GreaterThanOrEqual")]
    [InlineData("op_LessThanOrEqual", "LessThanOrEqual")]
    [InlineData("op_BitwiseAnd", "And")]
    [InlineData("op_BitwiseOr", "Or")]
    [InlineData("op_ExclusiveOr", "Xor")]
    [InlineData("op_LogicalNot", "Not")]
    [InlineData("op_OnesComplement", "OnesComplement")]
    [InlineData("op_LeftShift", "LeftShift")]
    [InlineData("op_RightShift", "RightShift")]
    [InlineData("op_UnsignedRightShift", "UnsignedRightShift")]
    [InlineData("op_UnaryNegation", "Negate")]
    [InlineData("op_UnaryPlus", "Plus")]
    [InlineData("op_CheckedAddition", "AddChecked")]
    [InlineData("op_CheckedSubtraction", "SubtractChecked")]
    [InlineData("op_CheckedMultiply", "MultiplyChecked")]
    public void OperatorNames_PascalCaseMirrorsConverterJavaNames(string metadataName, string expected)
    {
        Assert.Equal(expected, OperatorNames.ConvertOperatorName(metadataName));
        // 零漂移闭环：C# 名经 MethodTransformer.JavaName（首字母小写）== 转换器今发 Java 名
        var isChecked = metadataName.StartsWith("op_Checked", StringComparison.Ordinal);
        var baseMetadata = isChecked
            ? "op_" + metadataName["op_Checked".Length..]
            : metadataName;
        var javaToday = OperatorTransformer.OpSymbolToJavaName[baseMetadata]
                        + (isChecked ? "Checked" : ""); // OperatorTransformer.cs:96-8 后缀
        Assert.Equal(javaToday, char.ToLowerInvariant(expected[0]) + expected[1..]);
    }

    // ---- 决策 3：转换命名镜像表（算法复刻 BuildConversionMethodSuffix）----

    [Theory]
    [InlineData("double", "Double")]
    [InlineData("int", "Int")]
    [InlineData("long", "Long")]
    [InlineData("float", "Float")]
    [InlineData("boolean", "Boolean")]
    [InlineData("String", "String")]
    [InlineData("BigDecimal", "Decimal")]
    [InlineData("byte", "Byte")]
    [InlineData("byte[]", "ByteArray")]
    [InlineData("int[]", "IntArray")]
    [InlineData("double[]", "DoubleArray")]
    [InlineData("List", "")] // 表外 → 空（失败）
    public void OperatorNames_ConversionMirrorSuffix(string javaType, string expectedSuffix)
    {
        var ok = OperatorNames.TryGetConversionBaseSuffix(javaType, out var suffix);
        if (expectedSuffix.Length == 0)
            Assert.False(ok);
        else
        {
            Assert.True(ok);
            Assert.Equal(expectedSuffix, suffix);
        }
    }

    // ---- 决策 8：确定性碰撞升级 ----

    [Fact]
    public void OperatorNames_CollisionUpgradesDeterministically()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "Add" };
        Assert.Equal("AddOperator", OperatorNames.Disambiguate("Add", taken));
        taken.Add("AddOperator");
        Assert.Equal("AddOperator2", OperatorNames.Disambiguate("Add", taken));
        taken.Add("AddOperator2");
        Assert.Equal("AddOperator3", OperatorNames.Disambiguate("Add", taken));
        Assert.Equal("Add", OperatorNames.Disambiguate("Add", new HashSet<string>()));
        var taken2 = new HashSet<string>(StringComparer.Ordinal) { "ToString" };
        Assert.Equal("ToStringOperator", OperatorNames.Disambiguate("ToString", taken2));
    }
}
