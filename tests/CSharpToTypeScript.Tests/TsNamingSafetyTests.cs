using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsNamingSafetyTests
{
    [Fact]
    public void SafeIdentifier_Passthrough_Normal_Name()
    {
        Assert.Equal("foo", TsNaming.SafeIdentifier("foo"));
    }

    [Fact]
    public void SafeIdentifier_TsReservedWord_Gets_Dollar_Suffix()
    {
        Assert.Equal("let$", TsNaming.SafeIdentifier("let"));
        Assert.Equal("any$", TsNaming.SafeIdentifier("any"));
        Assert.Equal("namespace$", TsNaming.SafeIdentifier("namespace"));
    }

    [Fact]
    public void SafeIdentifier_Csharp_At_Escaped_Strips_Prefix()
    {
        // C# @class -> TS class$ (class is TS reserved-ish)
        Assert.Equal("class$", TsNaming.SafeIdentifier("@class"));
    }

    [Fact]
    public void SafeIdentifier_StrictModeReservedWords_Suffixed()
    {
        Assert.Equal("interface$", TsNaming.SafeIdentifier("interface"));
        Assert.Equal("implements$", TsNaming.SafeIdentifier("implements"));
        Assert.Equal("package$", TsNaming.SafeIdentifier("package"));
        Assert.Equal("bigint$", TsNaming.SafeIdentifier("bigint"));
    }

    [Fact]
    public void SafeIdentifier_At_Only_Returns_Underscore()
    {
        Assert.Equal("_", TsNaming.SafeIdentifier("@"));
    }

    [Fact]
    public void SafeClassName_Reserved_Word_Suffixed()
    {
        Assert.Equal("bigint$", TsNaming.SafeClassName("bigint"));
    }
    [Fact]
    public void OverloadSuffix_Joins_Param_Types()
    {
        var suffix = TsNaming.OverloadSuffix(new[] { "string", "number" });
        Assert.Equal("_string_number", suffix);
    }

    [Fact]
    public void OverloadSuffix_Single_Type()
    {
        Assert.Equal("_number", TsNaming.OverloadSuffix(new[] { "number" }));
    }

    [Fact]
    public void FactoryName_Uses_Ordinal()
    {
        Assert.Equal("Create$1", TsNaming.FactoryName(1));
        Assert.Equal("Create$2", TsNaming.FactoryName(2));
    }
}