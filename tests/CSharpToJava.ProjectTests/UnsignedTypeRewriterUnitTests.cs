using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

public class UnsignedTypeRewriterUnitTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("unsigned-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    [Fact]
    public void CleanFile_NoUnsignedKeywords_ReturnedUnchanged()
    {
        var src = "public class C { public int Value; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void HelperFile_IsInternalUnsignedMathWithBlockNamespace()
    {
        var content = UnsignedMathFileContent.Generate();
        Assert.Contains("namespace Cs2jUnsigned", content);
        Assert.Contains("internal static class UnsignedMath", content);
        Assert.Contains("public static int AddUInt32(int a, int b)", content);
        Assert.Contains("public static long ToInt64UInt32(int v)", content);
        Assert.DoesNotContain("public static class UnsignedExtensions", content);
    }

    [Fact]
    public void StaticCallBuilder_EmitsFullyQualifiedInvocation()
    {
        var call = UnsignedMathSyntax.StaticCall(
            "AddUInt32",
            SyntaxFactory.ParseExpression("a"),
            SyntaxFactory.ParseExpression("b"));
        Assert.Equal("Cs2jUnsigned.UnsignedMath.AddUInt32(a,b)", call.ToString());
    }

    [Fact]
    public void UncheckedCastBuilder_EmitsUncheckedCast()
    {
        var cast = UnsignedMathSyntax.UncheckedCast(
            SyntaxKind.IntKeyword,
            SyntaxFactory.ParseExpression("u"));
        Assert.Equal("unchecked((int)u)", cast.ToString());
    }

    [Fact]
    public void FieldDeclaration_KeywordReplacedWithFullName()
    {
        var src = "public class C { public uint Value; public byte B; public ushort S; public ulong L; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("global::System.UInt32 Value", result.OutputCode);
        Assert.Contains("global::System.Byte B", result.OutputCode);
        Assert.Contains("global::System.UInt16 S", result.OutputCode);
        Assert.Contains("global::System.UInt64 L", result.OutputCode);
    }

    [Fact]
    public void UIntAddition_UsesUInt32Domain()
    {
        var src = "public class C { public uint Add(uint a, uint b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), unchecked((int)b))", result.OutputCode);
        Assert.DoesNotContain("using Cs2jUnsigned;", result.OutputCode);
    }

    [Fact]
    public void ByteAddition_StaysNative()
    {
        // byte+byte promotes to int in both C# and Java; no helper needed.
        var src = "public class C { public int Add(byte a, byte b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("return a + b;", result.OutputCode);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
    }

    [Fact]
    public void MixedUShortPlusInt_StaysNative()
    {
        // ushort+int promotes to int in both C# and Java; no helper needed.
        var src = "public class C { public int M(ushort s, int i) { return s + i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("return s + i;", result.OutputCode);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
    }

    [Fact]
    public void MixedUIntPlusInt_PromotesToInt64Domain()
    {
        var src = "public class C { public long M(uint a, int b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.AddInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)a)), b)",
            result.OutputCode);
    }

    [Fact]
    public void MixedBytePlusLong_UsesInt64Domain()
    {
        var src = "public class C { public long M(byte b, long l) { return b + l; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt64(b, l)", result.OutputCode);
    }

    [Fact]
    public void MixedBytePlusUInt_UsesUInt32Domain()
    {
        var src = "public class C { public uint M(byte b, uint u) { return b + u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(b, unchecked((int)u))", result.OutputCode);
    }

    [Fact]
    public void MixedUIntPlusULong_UsesUInt64Domain()
    {
        var src = "public class C { public ulong M(uint u, ulong ul) { return u + ul; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.AddUInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)u)), unchecked((long)ul))",
            result.OutputCode);
    }

    [Fact]
    public void ConstantOperand_UIntPlusFive_StaysUInt32Domain()
    {
        var src = "public class C { public uint M(uint u) { return u + 5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)u), 5)", result.OutputCode);
    }

    [Fact]
    public void ConstantOperand_UIntPlusLongLiteral_UsesInt64Domain()
    {
        var src = "public class C { public long M(uint u) { return u + 5L; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.AddInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)u)), 5L)",
            result.OutputCode);
    }

    [Fact]
    public void ComparisonOperator_ConvertedToCompareUInt32()
    {
        var src = "public class C { public bool Less(uint a, uint b) { return a < b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.CompareUInt32(unchecked((int)a), unchecked((int)b)) < 0",
            result.OutputCode);
    }

    [Fact]
    public void IsInRangeInclusiveUIntOverload_CompareCall_NotDoubleWrapped()
    {
        // Exact expression produced by the CLI unsigned preprocessor for
        // JsonHelpers.IsInRangeInclusive(uint, uint, uint). The (uint) casts
        // inside the CompareUInt32 arguments used to re-trigger the unsigned
        // comparison fallback and double-wrap the comparison.
        var src = """
            public static class JsonHelpers
            {
                public static bool IsInRangeInclusive(System.UInt32 value, System.UInt32 lowerBound, System.UInt32 upperBound)
                    => Cs2jUnsigned.UnsignedMath.CompareUInt32(unchecked((int)(unchecked((uint)Cs2jUnsigned.UnsignedMath.SubtractUInt32(unchecked((int)value), unchecked((int)lowerBound))))), unchecked((int)(unchecked((uint)Cs2jUnsigned.UnsignedMath.SubtractUInt32(unchecked((int)upperBound), unchecked((int)lowerBound)))))) <= 0;
            }
            """;

        var helper = """
            namespace Cs2jUnsigned
            {
                internal static class UnsignedMath
                {
                    public static int SubtractUInt32(int a, int b) => unchecked(a - b);
                    public static int CompareUInt32(int a, int b) => a < b ? -1 : a > b ? 1 : 0;
                }
            }
            """;
        var pipeline = new Core.Pipeline.ConversionPipeline();
        var conv = pipeline.Convert(new Core.Pipeline.ConversionRequest
        {
            SourceCode = src + "\n" + helper,
            FileName = "JsonHelpers.cs",
            Options = new Core.Context.ConversionOptions(),
        });
        Assert.True(conv.Success, string.Join("; ", conv.Diagnostics.Select(d => d.Message)));
        var code = conv.GeneratedCode;
        // The rewriter lowers the uint comparison to CompareUInt32(...) <= 0.
        // The expression transformer must not wrap that compare call in
        // Integer.compareUnsigned(..., 0): the -1/0/1 result would be treated as
        // an unsigned value and in-range comparisons would be false.
        Assert.Contains("UnsignedMath.compareUInt32(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.compareUnsigned(UnsignedMath.compareUInt32", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualityOperator_ConvertedToCompareUInt32()
    {
        var src = "public class C { public bool Eq(uint a, uint b) { return a == b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.CompareUInt32(unchecked((int)a), unchecked((int)b)) == 0",
            result.OutputCode);
    }

    [Fact]
    public void CompoundAssignment_ConvertedToStaticCall()
    {
        var src = "public class C { public void Inc(ref uint a) { a += 5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "a = unchecked((uint)Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), 5))",
            result.OutputCode);
    }

    [Fact]
    public void ByteCompoundAssignment_InlinesArithmeticKeepsTruncation()
    {
        var src = "public class C { public void M(ref byte b) { b += 5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "b = Cs2jUnsigned.UnsignedMath.ToByteInt32(b + 5)",
            result.OutputCode);
    }

    [Fact]
    public void PostIncrement_ConvertedToStaticCall()
    {
        var src = "public class C { public void Inc(uint a) { a++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "a = unchecked((uint)Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), 1))",
            result.OutputCode);
    }

    [Fact]
    public void ByteIncrement_InlinesArithmeticKeepsTruncation()
    {
        var src = "public class C { public void M(byte b) { b++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "b = Cs2jUnsigned.UnsignedMath.ToByteInt32(b + 1)",
            result.OutputCode);
    }

    [Fact]
    public void ULongShiftRight_InlinesUnsignedShift()
    {
        var src = "public class C { public ulong M(ulong a, int n) { return a >> n; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("unchecked((long)a) >>> n", result.OutputCode);
        Assert.DoesNotContain("RightShiftUInt64", result.OutputCode);
    }

    [Fact]
    public void UIntShiftRight_InlinesUnsignedShift()
    {
        var src = "public class C { public uint M(uint a, int n) { return a >> n; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("unchecked((int)a) >>> n", result.OutputCode);
    }

    [Fact]
    public void CompoundShiftAssignment_InlinesShift()
    {
        var src = "public class C { public void M(ref uint a, int n) { a >>= n; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a = unchecked((uint)(unchecked((int)a) >>> n))", result.OutputCode);
    }

    [Fact]
    public void BitwiseNotByte_StaysNative()
    {
        var src = "public class C { public int M(byte b) { return ~b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("return ~b;", result.OutputCode);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
    }

    [Fact]
    public void Cast_UIntToLong_UsesToInt64UInt32()
    {
        var src = "public class C { public long M(uint u) { return (long)u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)u))",
            result.OutputCode);
    }

    [Fact]
    public void Cast_ULongToInt_UsesToInt32UInt64()
    {
        var src = "public class C { public int M(ulong ul) { return (int)ul; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.ToInt32UInt64(unchecked((long)ul))",
            result.OutputCode);
    }

    [Fact]
    public void Cast_IntToByte_UsesToByteInt32()
    {
        var src = "public class C { public byte M(int i) { return (byte)i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToByteInt32(i)", result.OutputCode);
    }

    [Fact]
    public void Cast_UIntToDouble_UsesToDoubleUInt32()
    {
        var src = "public class C { public double M(uint u) { return (double)u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.ToDoubleUInt32(unchecked((int)u))",
            result.OutputCode);
    }

    [Fact]
    public void Cast_UIntToChar_StaysNative()
    {
        var src = "public class C { public char M(uint u) { return (char)u; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("(char)u", result.OutputCode);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
    }

    [Fact]
    public void ConstFieldInitializer_OperatorsNotRewritten()
    {
        var src = "public class C { private const ulong Mask = (0x07ul | 0x06ul << 8 | 0x05ul << 16 | 0x04ul << 24 | 0x03ul << 32 | 0x02ul << 40 | 0x01ul << 48) + 1; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("const global::System.UInt64 Mask", result.OutputCode);
        Assert.DoesNotContain(".Or(", result.OutputCode);
        Assert.DoesNotContain(".LeftShift(", result.OutputCode);
        Assert.DoesNotContain(".Add(1)", result.OutputCode);
    }

    [Fact]
    public void ConstFieldInitializer_CastNotRewritten()
    {
        var src = "public class C { private const uint U = 0xFFFFFFFFu; private const int I = (int)U; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("const global::System.UInt32 U", result.OutputCode);
        Assert.DoesNotContain("U.ToInt32()", result.OutputCode);
        Assert.Contains("(int)U", result.OutputCode);
    }

    [Fact]
    public void CheckedIncrement_NotRewritten()
    {
        var src = "public class C { public void M(uint a) { checked { a++; } } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
        Assert.Contains("a++", result.OutputCode);
    }

    [Fact]
    public void Cast_NegativeLiteral_OperandParenthesized()
    {
        var src = "public class C { private const byte Invalid = unchecked((byte)-1); }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("unchecked((global::System.Byte)(-1))", result.OutputCode);
        Assert.DoesNotContain("(global::System.Byte)-1", result.OutputCode);
    }

    [Fact]
    public void Cast_PointerDereference_OperandParenthesized()
    {
        var src = "public unsafe class C { public void M(byte* pDst, char* pS) { while ((*pDst++ = (byte)*pS++) != 0) ; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToByteInt32(*pS++)", result.OutputCode);
    }

    [Fact]
    public void PropertyCompoundAssignment_NotRewritten()
    {
        var src = "public class C { public uint P { get; set; } public void M() { P += 1; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("UnsignedMath", result.OutputCode);
        Assert.Contains("P += 1", result.OutputCode);
    }

    [Fact]
    public void EnumUnderlyingType_Replaced()
    {
        var src = "public enum Flag : uint { None = 0, All = 0xFFFFFFFFu }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("enum Flag : global::System.UInt32", result.OutputCode);
    }

    [Fact]
    public void StaticMemberAccess_Replaced()
    {
        var src = "public class C { public ulong Max() { return uint.MaxValue; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("global::System.UInt32.MaxValue", result.OutputCode);
    }

    [Fact]
    public void NullableType_Replaced()
    {
        var src = "public class C { public uint? maybe; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("global::System.UInt32? maybe", result.OutputCode);
    }

    [Fact]
    public void CompoundAssignment_AcrossIfDirective_DirectivesStayAtStatementLevel()
    {
        // 回归:重写 bufferLength += (nuint)pOriginalBuffer 时,语句首 token 的
        // 前导 trivia 含未激活的 #if DEBUG 块,曾被打散进参数列表中间导致
        // 中间 C# 无法编译(cs-text Latin1Utility)。
        var src = """
            public unsafe class C
            {
                public static void M(char* pBuffer, char* pOriginalBuffer, ref uint bufferLength)
                {
            #if DEBUG
                    long numCharsRead = pBuffer - pOriginalBuffer;
                    System.Diagnostics.Debug.Assert(numCharsRead >= 0);
            #endif

                    // Adjust remaining buffer length.

                    bufferLength += (uint)(nuint)pOriginalBuffer;
                }
            }
            """;
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed, result.OutputCode);

        // 重写后的语句必须完整,#if/#endif 仍在语句边界上。
        var assignmentIndex = result.OutputCode.IndexOf(
            "bufferLength = unchecked((uint)Cs2jUnsigned.UnsignedMath.AddUInt32(",
            StringComparison.Ordinal);
        Assert.True(assignmentIndex > 0, result.OutputCode);
        var endifIndex = result.OutputCode.IndexOf("#endif", StringComparison.Ordinal);
        Assert.True(endifIndex >= 0 && endifIndex < assignmentIndex, result.OutputCode);

        var reparsed = CSharpSyntaxTree.ParseText(result.OutputCode);
        Assert.Empty(reparsed.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void PostfixInsideIfStatement_NotHoisted()
    {
        // 回归:条件执行的语句里 i++ 不能拆出临时变量,否则条件不满足也会自增。
        var src = "public class C { public int M(uint i, bool c) { int x = 0; if (c) { x = (int)i++; } return x; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("__cs2j_unsigned_", result.OutputCode);
        Assert.Contains("i++", result.OutputCode);
    }

    [Fact]
    public void ImplicitWidening_UIntToLong_UsesHelper()
    {
        var src = "public class C { public long M(uint u) { long l = u; return l; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "long l = Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)u))",
            result.OutputCode);
    }

    [Fact]
    public void ImplicitWidening_UIntToDoubleAssignment_UsesHelper()
    {
        var src = "public class C { public double M(uint u, double d) { d = u; return d; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "d = Cs2jUnsigned.UnsignedMath.ToDoubleUInt32(unchecked((int)u))",
            result.OutputCode);
    }

    [Fact]
    public void MixedUIntAndDouble_Comparison_WrapsUnsignedOperand()
    {
        var src = "public class C { public bool M(uint u) { return u > 0.5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.ToDoubleUInt32(unchecked((int)u)) > 0.5",
            result.OutputCode);
    }

    [Fact]
    public void HelperFile_UsesConverterSafeUnsignedPatterns()
    {
        var content = UnsignedMathFileContent.Generate();
        // (ulong) 强转算术会被 Java 转换器擦成有符号,必须换成 >>> / JDK 算法。
        Assert.DoesNotContain("(ulong)a / (ulong)b", content);
        Assert.DoesNotContain("(ulong)a % (ulong)b", content);
        Assert.DoesNotContain("(ulong)a >>", content);
        Assert.Contains("a >>> (n & 63)", content);
        Assert.Contains("double.Parse(((ulong)v).ToString", content);
    }

    [Fact]
    public void RewrittenOutput_AlwaysParsesCleanly()
    {
        var src = "public class C { public uint M(uint a, uint b) { uint r = 0; r += a; r -= b; r *= 2u; r /= 3u; r <<= 1; r >>= 1; return r++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        var reparsed = CSharpSyntaxTree.ParseText(result.OutputCode);
        Assert.Empty(reparsed.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
