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
    public void CleanFile_NoUnsignedKeywords_ReturnedUnchanged()
    {
        var src = "public class C { public int Value; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void ExtensionFile_TypeIsInternal()
    {
        var content = UnsignedExtensionsFileContent.Generate();
        Assert.Contains("internal static class UnsignedExtensions", content);
        Assert.DoesNotContain("public static class UnsignedExtensions", content);
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
    public void BinaryOperator_ConvertedToExtensionMethodCall()
    {
        var src = "public class C { public uint Add(uint a, uint b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.Add(b)", result.OutputCode);
        Assert.DoesNotContain("a + b", result.OutputCode);
        Assert.Contains("using Cs2jUnsigned;", result.OutputCode);
    }

    [Fact]
    public void ComparisonOperator_ConvertedToCompareTo()
    {
        var src = "public class C { public bool Less(uint a, uint b) { return a < b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.CompareTo(b) < 0", result.OutputCode);
    }

    [Fact]
    public void EqualityOperator_ConvertedToEquals()
    {
        var src = "public class C { public bool Eq(uint a, uint b) { return a == b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.Equals(b)", result.OutputCode);
    }

    [Fact]
    public void CompoundAssignment_ConvertedToMethodCall()
    {
        var src = "public class C { public void Inc(ref uint a) { a += 5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a = a.Add(5)", result.OutputCode);
    }

    [Fact]
    public void PostIncrement_ConvertedToAddOne()
    {
        var src = "public class C { public void Inc(uint a) { a++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a = a.Add(1)", result.OutputCode);
    }

    [Fact]
    public void Cast_UnsignedToUnsigned_TypeReplacedOnly()
    {
        var src = "public class C { public uint Cast(int x) { return (uint)x; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("(System.UInt32)x", result.OutputCode);
    }

    [Fact]
    public void Cast_UnsignedToSigned_ConvertedToExtensionMethod()
    {
        var src = "public class C { public int ToInt(uint x) { return (int)x; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("x.ToInt32()", result.OutputCode);
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
        Assert.Contains("(System.Byte)(*pS++)", result.OutputCode);
        Assert.DoesNotContain("(System.Byte)*pS++", result.OutputCode);
    }

    [Fact]
    public void OneDimensionalArray_ElementTypeReplaced()
    {
        var src = "public class C { public uint[] arr = new uint[10]; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32[] arr", result.OutputCode);
        Assert.Contains("new System.UInt32[10]", result.OutputCode);
    }

    [Fact]
    public void MultiDimensionalArray_ElementTypeReplaced()
    {
        var src = "public class C { public uint[,] matrix = new uint[3, 4]; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32[,] matrix", result.OutputCode);
        Assert.Contains("new System.UInt32[3, 4]", result.OutputCode);
    }

    [Fact]
    public void JaggedArray_ElementTypeReplaced()
    {
        var src = "public class C { public uint[][] jagged = new uint[2][]; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32[][] jagged", result.OutputCode);
    }

    [Fact]
    public void Generic_TypeArgumentReplaced()
    {
        var src = "using System.Collections.Generic; public class C { public List<uint> list = new List<uint>(); }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("List<System.UInt32>", result.OutputCode);
    }

    [Fact]
    public void GenericDictionary_BothTypeArgsReplaced()
    {
        var src = "using System.Collections.Generic; public class C { public Dictionary<uint, ulong> dict; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Dictionary<System.UInt32, System.UInt64>", result.OutputCode);
    }

    [Fact]
    public void ConstField_TypeReplaced()
    {
        var src = "public class C { const uint MaxVal = 4294967295u; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("const System.UInt32 MaxVal", result.OutputCode);
    }

    [Fact]
    public void ConstFieldInitializer_BinaryOperatorsNotRewritten()
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
    public void StaticMethodCall_Replaced()
    {
        var src = "public class C { public uint Parse(string s) { return uint.Parse(s); } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32.Parse(s)", result.OutputCode);
    }

    [Fact]
    public void NullableType_Replaced()
    {
        var src = "public class C { public uint? maybe; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32? maybe", result.OutputCode);
    }

    [Fact]
    public void RefOutParameter_TypeReplaced()
    {
        var src = "public class C { public void M(ref uint x, out uint y) { y = x; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("ref System.UInt32 x", result.OutputCode);
        Assert.Contains("out System.UInt32 y", result.OutputCode);
    }

    [Fact]
    public void TypeOf_Replaced()
    {
        var src = "public class C { public System.Type T() { return typeof(uint); } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("typeof(System.UInt32)", result.OutputCode);
    }

    [Fact]
    public void Lambda_ParameterAndReturnTypeReplaced()
    {
        var src = "using System; public class C { public Func<uint, uint> f = x => x + 1; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Func<System.UInt32, System.UInt32>", result.OutputCode);
        Assert.Contains("x.Add(1)", result.OutputCode);
    }

    [Fact]
    public void LocalFunction_TypeReplacedAndOperatorRewritten()
    {
        var src = "public class C { public void M() { uint Add(uint a, uint b) => a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32 Add(System.UInt32 a, System.UInt32 b)", result.OutputCode);
        Assert.Contains("a.Add(b)", result.OutputCode);
    }

    [Fact]
    public void MixedTypeOperation_IntOperandNotRewritten()
    {
        var src = "public class C { public int Add(int a, int b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("a.Add(b)", result.OutputCode);
        Assert.Contains("a + b", result.OutputCode);
    }

    [Fact]
    public void ShiftOperator_WithIntVariable_Rewritten()
    {
        var src = "public class C { public uint Shift(uint a, int n) { return a << n; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.LeftShift(n)", result.OutputCode);
    }

    [Fact]
    public void ByteArithmetic_PromotedToInt_NotRewritten()
    {
        var src = "public class C { public int Add(byte a, byte b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a + b", result.OutputCode);
        Assert.DoesNotContain("a.Add(b)", result.OutputCode);
    }

    [Fact]
    public void ByteShift_NotRewritten()
    {
        var src = "public class C { public int Shift(byte a, int n) { return a << n; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("a.LeftShift(n)", result.OutputCode);
    }

    [Fact]
    public void ByteBitwiseNot_NotRewritten()
    {
        var src = "public class C { public int Not(byte a) { return ~a; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("a.Not()", result.OutputCode);
    }

    [Fact]
    public void MixedTypeOperation_LongLiteral_NotRewritten()
    {
        var src = "public class C { public long Add(uint a) { return a + 1L; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("a.Add(", result.OutputCode);
        Assert.Contains("a + 1L", result.OutputCode);
    }

    [Fact]
    public void CheckedIncrement_NotRewritten()
    {
        var src = "public class C { public void M(uint a) { checked { a++; } } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("a.Add(1)", result.OutputCode);
        Assert.Contains("a++", result.OutputCode);
    }

    [Fact]
    public void PostIncrementInExpression_HoistedToTemp()
    {
        var src = "public class C { public uint M(uint a) { uint r = a++; return r; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("a++", result.OutputCode);
        Assert.Contains("a = a.Add(1)", result.OutputCode);
        Assert.Contains("__cs2j_unsigned", result.OutputCode);
    }

    [Fact]
    public void PropertyCompoundAssignment_NotRewritten()
    {
        var src = "public class C { public uint P { get; set; } public void M() { P += 1; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("P = P.Add(1)", result.OutputCode);
        Assert.Contains("P += 1", result.OutputCode);
    }

    [Fact]
    public void IndexerPostIncrement_NotRewritten()
    {
        var src = "public class C { public uint[] A = new uint[1]; public void M(int i) { A[i]++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.DoesNotContain("A[i] = A[i].Add(1)", result.OutputCode);
        Assert.Contains("A[i]++", result.OutputCode);
    }

    [Fact]
    public void FieldPostIncrement_StillRewritten()
    {
        var src = "public class C { public uint F; public void M() { F++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("F = F.Add(1)", result.OutputCode);
    }

    [Fact]
    public void AllOperators_Covered()
    {
        var src = @"public class C {
            public uint a, b;
            public void Test() {
                uint r1 = a + b; uint r2 = a - b; uint r3 = a * b;
                uint r4 = a / b; uint r5 = a % b; uint r6 = a & b;
                uint r7 = a | b; uint r8 = a ^ b; uint r9 = ~a;
                uint r10 = a << 2; uint r11 = a >> 2;
                bool e1 = a == b; bool e2 = a != b;
                bool e3 = a < b; bool e4 = a <= b;
                bool e5 = a > b; bool e6 = a >= b;
                a++; b--; ++a; --b;
                a += b; a -= b; a *= b; a /= b; a %= b;
                a &= b; a |= b; a ^= b;
            }
        }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.Add(b)", result.OutputCode);
        Assert.Contains("a.Subtract(b)", result.OutputCode);
        Assert.Contains("a.Multiply(b)", result.OutputCode);
        Assert.Contains("a.Divide(b)", result.OutputCode);
        Assert.Contains("a.Remainder(b)", result.OutputCode);
        Assert.Contains("a.And(b)", result.OutputCode);
        Assert.Contains("a.Or(b)", result.OutputCode);
        Assert.Contains("a.Xor(b)", result.OutputCode);
        Assert.Contains("a.Not()", result.OutputCode);
        Assert.Contains("a.LeftShift(2)", result.OutputCode);
        Assert.Contains("a.RightShift(2)", result.OutputCode);
        Assert.Contains("a.Equals(b)", result.OutputCode);
        Assert.Contains("!a.Equals(b)", result.OutputCode);
        Assert.Contains("a.CompareTo(b) < 0", result.OutputCode);
        Assert.Contains("a.CompareTo(b) <= 0", result.OutputCode);
        Assert.Contains("a.CompareTo(b) > 0", result.OutputCode);
        Assert.Contains("a.CompareTo(b) >= 0", result.OutputCode);
    }
}
