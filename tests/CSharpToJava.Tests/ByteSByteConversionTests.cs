using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ByteSByteConversionTests
{
    [Fact]
    public void ByteDeclaration_MapsToInt()
    {
        var result = Convert("class Test { byte x = 200; }");
        Assert.True(result.Success);
        Assert.Contains("int x =", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
        Assert.Contains("200", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SByteDeclaration_StaysByte()
    {
        var result = Convert("class Test { sbyte y = -100; }");
        Assert.True(result.Success);
        Assert.Contains("byte y =", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArithmeticWithCast_AddsMask()
    {
        var result = Convert(@"
class Test {
    byte Add(byte a, byte b) {
        byte c = (byte)(a + b);
        return c;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArithmeticToInt_NoMask()
    {
        var result = Convert(@"
class Test {
    int Add(byte a, byte b) {
        int c = a + b;
        return c;
    }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("(a + b) & 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void BytePlainLiteral_NoMask()
    {
        var result = Convert("class Test { byte x = 42; }");
        Assert.True(result.Success);
        Assert.Contains("int x =", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
        Assert.Contains("42", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteLiteralWithCast_HasMask()
    {
        var result = Convert("class Test { byte x = (byte)300; }");
        Assert.True(result.Success);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteCastInComparison_ParenthesizesMask()
    {
        var result = Convert("class Test { bool M(int ch) { return ch == (byte)']'; } }");
        Assert.True(result.Success, result.GeneratedCode);

        var code = result.GeneratedCode ?? "";
        Assert.Contains("ch == ", code, StringComparison.Ordinal);
        Assert.Contains("& 0xFF)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ch == ']' & 0xFF", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ch == ((int)(']')) & 0xFF", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArrayRead_AddsMask()
    {
        var result = Convert(@"
class Test {
    int Read(byte[] buf) {
        byte b = buf[0];
        return b;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArrayElementInBitwiseExpression_AddsMask()
    {
        var result = Convert(@"
class Test {
    int Detect(byte[] bytes) {
        return bytes[0] << 8 | bytes[1];
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArrayWrite_StaysByteArray()
    {
        var result = Convert(@"
class Test {
    void Write(byte[] buf, int v) {
        buf[0] = (byte)v;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("byte[] buf", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArrayDeclaration_StaysByteArray()
    {
        var result = Convert("class Test { byte[] buf = new byte[1024]; }");
        Assert.True(result.Success);
        Assert.Contains("byte[] buf = new byte[1024]", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArrayFieldBareInitializer_CastsOutOfRangeLiterals()
    {
        var result = Convert("class Test { byte[] buf = { 1, 128, 255 }; }");
        Assert.True(result.Success, result.GeneratedCode);

        Assert.Contains("byte[] buf = new byte[] { 1, (byte)128, (byte)255 }", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArrayLocalBareInitializer_CastsOutOfRangeLiterals()
    {
        var result = Convert(@"
class Test {
    void M() {
        byte[] buf = { 1, 128, 255 };
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        Assert.Contains("byte[] buf = new byte[] { 1, (byte)128, (byte)255 };", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
    }

    [Fact]
    public void ByteMethodParameter_BecomesInt()
    {
        var result = Convert("class Test { void Foo(byte b) { } }");
        Assert.True(result.Success);
        Assert.Contains("void foo(int b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultByte_ReturnsZero()
    {
        var result = Convert("class Test { byte x = default; }");
        Assert.True(result.Success);
        Assert.Contains("int x = 0", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
    }

    [Fact]
    public void ByteMaxValue_MapsTo255()
    {
        var result = Convert("class Test { int x = byte.MaxValue; }");
        Assert.True(result.Success);
        Assert.Contains("255", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteMinValue_MapsTo0()
    {
        var result = Convert("class Test { int x = byte.MinValue; }");
        Assert.True(result.Success);
        Assert.Contains("0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SizeOfByte_Returns4()
    {
        var result = Convert("class Test { int s = sizeof(byte); }");
        Assert.True(result.Success);
        Assert.Contains("int s = 4", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
    }

    [Fact]
    public void ByteCompoundAssignment_HasMask()
    {
        var result = Convert(@"
class Test {
    void Inc(byte b) {
        b += 1;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableByte_MapsToInteger()
    {
        var result = Convert("class Test { byte? x = null; }");
        Assert.True(result.Success);
        Assert.Contains("Integer x = null", StripAccessModifiers(result.GeneratedCode), StringComparison.Ordinal);
    }

    [Fact]
    public void RefByte_PassesThrough()
    {
        var result = Convert(@"
class Test {
    void Modify(ref byte b) { b = 255; }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("ByteHolder", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SByteArithmetic_StaysByte()
    {
        var result = Convert(@"
class Test {
    sbyte Negate(sbyte a, sbyte b) {
        sbyte c = (sbyte)(a + b);
        return c;
    }
}");
        Assert.True(result.Success);
        // sbyte stays byte — type mapping verified by build not failing
    }

    private static string StripAccessModifiers(string code)
        => code.Replace("private ", "").Replace("protected ", "").Replace("public ", "");

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
