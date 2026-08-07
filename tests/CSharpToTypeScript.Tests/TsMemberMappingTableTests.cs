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
    public void TryMapType_Project_Fqcn_Shortens_To_Last_Segment()
    {
        // M5 / MSAGL: unknown multi-segment project FQCNs shorten to the last segment
        // (the import collector registers the short name); this replaced the pre-M5
        // pass-through contract for dotted names.
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.True(table.TryMapType("MyApp.Foo", out var tsType, out _));
        Assert.Equal("Foo", tsType);
    }

    [Fact]
    public void TryMapType_Single_Segment_Unknown_Returns_False()
    {
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"));
        Assert.False(table.TryMapType("Foo", out _, out _));
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
    public void TryMapType_Project_Fqcn_Array_Shortens_To_Last_Segment()
    {
        // M5 / MSAGL: array of an unknown project FQCN shortens element + keeps [].
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("MyApp.Foo[]", out var tsType, out _));
        Assert.Equal("Foo[]", tsType);
    }

    [Fact]
    public void TryMapType_System_Prefixed_Unknown_Returns_False_And_Original()
    {
        // System.* names stay unmapped (they belong to the config tables, not project
        // FQCN shortening); pass-through contract preserved.
        var table = new TsMemberMappingTable(null, null);
        Assert.False(table.TryMapType("System.UnknownType", out var tsType, out _));
        Assert.Equal("System.UnknownType", tsType);
    }

    // ─── Task 21 异常反向映射 ──────────────────────────────────────

    [Fact]
    public void TryMapException_Java_Standard_Name_Maps_To_Compat_Class()
    {
        // IR 为 Java 风味名：IllegalStateException ← C# InvalidOperationException
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapException("IllegalStateException", out var tsType, out var imports, out var isFallback));
        Assert.Equal("InvalidOperationException", tsType);
        Assert.False(isFallback);
        Assert.Contains("@csharptotypescript/compat", imports);
    }

    [Fact]
    public void TryMapException_RuntimeException_Maps_To_CsException_Not_Fallback()
    {
        // C# Exception → IR RuntimeException → 兼容库基类（确定性覆盖映射，非降级）
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapException("RuntimeException", out var tsType, out _, out var isFallback));
        Assert.Equal("CsException", tsType);
        Assert.False(isFallback);
    }

    [Fact]
    public void TryMapException_Identity_Compat_Name_Stays()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapException("ArgumentException", out var tsType, out _, out var isFallback));
        Assert.Equal("ArgumentException", tsType);
        Assert.False(isFallback);
        Assert.True(table.TryMapException("FormatException", out tsType, out _, out isFallback));
        Assert.Equal("FormatException", tsType);
        Assert.False(isFallback);
    }

    [Fact]
    public void TryMapException_Unmapped_Bcl_Exception_Falls_Back_To_CsException()
    {
        // 已知 BCL 异常但兼容库无对应类（spec §4.3.9）→ CsException + 降级标记
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapException("SocketException", out var tsType, out _, out var isFallback));
        Assert.Equal("CsException", tsType);
        Assert.True(isFallback);
        Assert.True(table.TryMapException("NullPointerException", out tsType, out _, out isFallback));
        Assert.Equal("CsException", tsType);
        Assert.True(isFallback);
    }

    [Fact]
    public void TryMapException_Unknown_Type_Returns_False()
    {
        var table = new TsMemberMappingTable(null, null);
        Assert.False(table.TryMapException("MyException", out _, out _, out _));
        Assert.False(table.TryMapException("System.Exception", out _, out _, out _));
    }

    [Fact]
    public void TryMapException_Java_Config_Extends_Known_Set()
    {
        // Java TypeMappings.json 全量提取：ConfigurationErrorsException 不在内置集，
        // 但属配置异常条目（恒等 compat）→ 识别为 BCL 异常；无兼容类 → 降级
        var javaConfig = System.IO.Path.Combine(System.AppContext.BaseDirectory, "config", "TypeMappings.json");
        var table = new TsMemberMappingTable(ConfigPath("TypeMappings.json"), ConfigPath("MemberMappings.json"), javaConfig);
        Assert.True(table.TryMapException("ConfigurationErrorsException", out var tsType, out _, out var isFallback));
        Assert.Equal("CsException", tsType);
        Assert.True(isFallback);
    }

    [Fact]
    public void TryMapType_Exception_Name_Returns_Compat_Type_With_Import()
    {
        // 类型位置（字段/参数/返回值/局部变量）统一经 TryMapType 入口
        var table = new TsMemberMappingTable(null, null);
        Assert.True(table.TryMapType("IllegalStateException", out var tsType, out var imports));
        Assert.Equal("InvalidOperationException", tsType);
        Assert.Contains("@csharptotypescript/compat", imports);
    }
}
