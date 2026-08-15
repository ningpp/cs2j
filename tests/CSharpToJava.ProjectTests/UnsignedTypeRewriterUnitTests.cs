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
        Assert.Contains("System.UInt32 Value", result.OutputCode);
        Assert.Contains("System.Byte B", result.OutputCode);
        Assert.Contains("System.UInt16 S", result.OutputCode);
        Assert.Contains("System.UInt64 L", result.OutputCode);
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
    public void ByteAddition_PromotesToInt32Domain()
    {
        var src = "public class C { public int Add(byte a, byte b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt32(a, b)", result.OutputCode);
    }

    [Fact]
    public void MixedUShortPlusInt_UsesInt32Domain()
    {
        var src = "public class C { public int M(ushort s, int i) { return s + i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddInt32(s, i)", result.OutputCode);
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
    public void ByteCompoundAssignment_WrapsThroughToByte()
    {
        var src = "public class C { public void M(ref byte b, int i) { b += i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "b = Cs2jUnsigned.UnsignedMath.ToByteInt32(Cs2jUnsigned.UnsignedMath.AddInt32(b, i))",
            result.OutputCode);
    }

    [Fact]
    public void UIntCompoundWithInt_UsesInt64Domain()
    {
        var src = "public class C { public void M(ref uint a, int i) { a += i; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "a = unchecked((uint)Cs2jUnsigned.UnsignedMath.AddInt64(Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)a)), i))",
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
    public void ByteIncrement_WrapsThroughToByte()
    {
        var src = "public class C { public void M(byte b) { b++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "b = Cs2jUnsigned.UnsignedMath.ToByteInt32(Cs2jUnsigned.UnsignedMath.AddInt32(b, 1))",
            result.OutputCode);
    }

    [Fact]
    public void ULongShift_UsesLogicalRightShift()
    {
        var src = "public class C { public ulong M(ulong a, int n) { return a >> n; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains(
            "Cs2jUnsigned.UnsignedMath.RightShiftUInt64(unchecked((long)a), n)",
            result.OutputCode);
    }

    [Fact]
    public void BitwiseNotByte_UsesNotInt32()
    {
        var src = "public class C { public int M(byte b) { return ~b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.NotInt32(b)", result.OutputCode);
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
        Assert.Contains("const System.UInt64 Mask", result.OutputCode);
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
        Assert.Contains("const System.UInt32 U", result.OutputCode);
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
        Assert.Contains("unchecked((System.Byte)(-1))", result.OutputCode);
        Assert.DoesNotContain("(System.Byte)-1", result.OutputCode);
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
        Assert.Contains("enum Flag : System.UInt32", result.OutputCode);
    }

    [Fact]
    public void StaticMemberAccess_Replaced()
    {
        var src = "public class C { public ulong Max() { return uint.MaxValue; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32.MaxValue", result.OutputCode);
    }

    [Fact]
    public void NullableType_Replaced()
    {
        var src = "public class C { public uint? maybe; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32? maybe", result.OutputCode);
    }
}
