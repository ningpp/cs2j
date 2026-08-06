namespace CSharpToTypeScript.Tests;

public class TsLiteralEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Null_Literal_Emits_Null()
    {
        var result = Convert("""
class Sample
{
    string GetNull()
    {
        return null;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return null;", result.GeneratedCode);
    }

    [Fact]
    public void Boolean_Literals_Emit_True_False()
    {
        var result = Convert("""
class Sample
{
    bool GetTrue()
    {
        return true;
    }
    bool GetFalse()
    {
        return false;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return true;", result.GeneratedCode);
        Assert.Contains("return false;", result.GeneratedCode);
    }

    [Fact]
    public void Hex_Literal_Emits_Directly()
    {
        var result = Convert("""
class Sample
{
    int GetHex()
    {
        return 0xFF;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return 0xFF;", result.GeneratedCode);
    }

    [Fact]
    public void String_With_Escapes_Emits_With_Escapes()
    {
        var result = Convert("""
class Sample
{
    string GetMessage()
    {
        return "hello\nworld";
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return \"hello\\nworld\";", result.GeneratedCode);
    }

    [Fact]
    public void Char_Literal_Emits_As_String()
    {
        var result = Convert("""
class Sample
{
    char GetChar()
    {
        return 'A';
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return \"A\";", result.GeneratedCode);
    }
}
