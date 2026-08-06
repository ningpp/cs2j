using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsMemberMappingTableTests
{
    private static string ConfigPath(string name) =>
        System.IO.Path.Combine(System.AppContext.BaseDirectory, "config", "typescript", name);

    [Fact]
    public void TryMapType_Int32_To_Number()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapType("System.Int32", out var tsType, out _));
        Assert.Equal("number", tsType);
    }

    [Fact]
    public void TryMapType_Int64_To_Bigint()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapType("System.Int64", out var tsType, out _));
        Assert.Equal("bigint", tsType);
    }

    [Fact]
    public void TryMapType_Decimal_To_CsDecimal_With_Import()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapType("System.Decimal", out var tsType, out var imports));
        Assert.Equal("CsDecimal", tsType);
        Assert.Contains("@csharptotypescript/compat", imports);
    }

    [Fact]
    public void TryMapMethod_String_ToUpper_To_ToUpperCase()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapMethod("System.String", "ToUpper", out var tsMethod, out _));
        Assert.Equal("toUpperCase", tsMethod);
    }

    [Fact]
    public void TryMapMethod_List_Add_To_Push()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapMethod("System.Collections.Generic.List`1", "Add", out var tsMethod, out _));
        Assert.Equal("push", tsMethod);
    }

    [Fact]
    public void TryMapMember_String_Length_To_length()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapMember("System.String", "Length", out var tsMember));
        Assert.Equal("length", tsMember);
    }

    [Fact]
    public void TryMapType_Unknown_Returns_False()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.False(table.TryMapType("MyApp.Foo", out _, out _));
    }

    [Fact]
    public void Builtin_MapSize_To_Length()
    {
        // 内置表：Java .size() -> TS .length（无配置）
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapMethod("java.util.List", "size", out var tsMethod, out _));
        Assert.Equal("length", tsMethod);
    }

    [Fact]
    public void TryMapType_Java_Flavor_String_To_string()
    {
        // IR 中的类型是 Java 风味，内置 Java→TS 类型表覆盖
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("String", out var tsType, out _));
        Assert.Equal("string", tsType);
    }

    [Fact]
    public void TryMapType_Java_Flavor_Primitives()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("int", out var t1, out _));
        Assert.Equal("number", t1);
        Assert.True(table.TryMapType("long", out var t2, out _));
        Assert.Equal("bigint", t2);
        Assert.True(table.TryMapType("boolean", out var t3, out _));
        Assert.Equal("boolean", t3);
    }

    [Fact]
    public void TryMapMember_Builtin_List_Size_Maps_To_Length()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapMember("java.util.List", "size", out var tsMember));
        Assert.Equal("length", tsMember);
    }

    [Fact]
    public void TryMapMethod_Unmapped_Returns_False_And_Original()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.False(table.TryMapMethod("Some.Type", "Foo", out var tsMethod, out _));
        Assert.Equal("Foo", tsMethod);
    }

    [Fact]
    public void TryMapType_Unmapped_Returns_False_And_Original()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.False(table.TryMapType("System.UnknownType", out var tsType, out _));
        Assert.Equal("System.UnknownType", tsType);
    }

    [Fact]
    public void TryMapMethod_Config_Static_Imports_Passed_Through()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapMethod("System.String", "Split", out var tsMethod, out var imports));
        Assert.Equal("CsString.split", tsMethod);
        Assert.Contains("@csharptotypescript/compat", imports);
    }
    [Fact]
    public void TryMapType_Int_Array_To_Number_Array()
    {
        // I1：int[] 未收录内置表，兜底前按元素类型递归映射并回拼 []
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("int[]", out var tsType, out _));
        Assert.Equal("number[]", tsType);
    }

    [Fact]
    public void TryMapType_String_Array_To_String_Array()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("String[]", out var tsType, out _));
        Assert.Equal("string[]", tsType);
    }

    [Fact]
    public void TryMapType_Jagged_Int_Array_To_Number_Array()
    {
        // int[][] → 元素 int[] → 元素 int → number，回拼两次 []
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("int[][]", out var tsType, out _));
        Assert.Equal("number[][]", tsType);
    }

    [Fact]
    public void TryMapType_Unmapped_Element_Array_Returns_False_And_Original()
    {
        // 元素未命中时维持原行为：返回原值 + false
        var table = new TsMemberMappingTable(null, null);
        Assert.False(table.TryMapType("MyApp.Foo[]", out var tsType, out _));
        Assert.Equal("MyApp.Foo[]", tsType);
    }
}
