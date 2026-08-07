using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// M5 / MSAGL full conversion: Java-flavor generic type names that reach TS type
/// positions (field/parameter/return/cast) must map to compilable TS forms:
/// Class&lt;?&gt; -> any, CSharpGenericIterable&lt;?&gt; -> Iterable&lt;any&gt;,
/// Map.Entry&lt;K,V&gt; -> CsKeyValuePair&lt;K,V&gt; (compat import),
/// java.util.List&lt;X&gt; -> Array&lt;X&gt;, java.util.Set&lt;X&gt; ->
/// CSharpHashSet&lt;X&gt;, Map&lt;K,V&gt; -> CSharpDictionary&lt;K,V&gt;, project
/// FQCN (Microsoft.Msagl.Core.Geometry.Point) -> last segment.
/// </summary>
public class TsJavaFlavorTypeMappingTests : TsConversionTestBase
{
    private static string EmitType(JavaTypeDeclaration type)
    {
        var table = new TsMemberMappingTable(null, null);
        var imports = new TsImportCollector();
        var emitter = new TypeScriptEmitter();
        return emitter.EmitType(type, table, imports);
    }

    private static (string Code, TsImportCollector Imports) FieldTypeEmission(string javaFlavorType)
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = javaFlavorType,
            Name = "Value",
        });
        var table = new TsMemberMappingTable(null, null);
        var imports = new TsImportCollector();
        var emitter = new TypeScriptEmitter();
        return (emitter.EmitType(cls, table, imports), imports);
    }

    [Fact]
    public void Class_Wildcard_Type_Maps_To_Any()
    {
        var (code, _) = FieldTypeEmission("Class<?>");
        Assert.Contains("public Value: any = null;", code);
        Assert.DoesNotContain("Class<?>", code);
    }

    [Fact]
    public void CSharpGenericIterable_Wildcard_Maps_To_Iterable_Any()
    {
        var (code, _) = FieldTypeEmission("CSharpGenericIterable<?>");
        Assert.Contains("public Value: Iterable<any> | null = null;", code);
        Assert.DoesNotContain("CSharpGenericIterable", code);
    }

    [Fact]
    public void Map_Entry_Maps_To_CsKeyValuePair_With_Compat_Import()
    {
        var (code, imports) = FieldTypeEmission("Map.Entry<String, Integer>");
        Assert.Contains("public Value: CsKeyValuePair<string, number> | null = null;", code);
        Assert.Contains(imports.GetImports(), i => i.ModulePath == "@csharptotypescript/compat" && i.NamedImports.Contains("CsKeyValuePair"));
    }

    [Fact]
    public void JavaUtil_List_Maps_To_Array()
    {
        var (code, _) = FieldTypeEmission("java.util.List<Integer>");
        Assert.Contains("public Value: Array<number> | null = null;", code);
        Assert.DoesNotContain("java.util.List", code);
    }

    [Fact]
    public void JavaUtil_Set_Maps_To_CSharpHashSet()
    {
        var (code, _) = FieldTypeEmission("java.util.Set<Integer>");
        Assert.Contains("public Value: CSharpHashSet<number> | null = null;", code);
    }

    [Fact]
    public void Map_Maps_To_CSharpDictionary()
    {
        var (code, _) = FieldTypeEmission("Map<String, CSharpList<Integer>>");
        Assert.Contains("public Value: CSharpDictionary<string, CSharpList<number>> | null = null;", code);
    }

    [Fact]
    public void Project_Fqcn_Shortens_To_Last_Segment()
    {
        var (code, _) = FieldTypeEmission("Microsoft.Msagl.Core.Geometry.Point");
        Assert.Contains("public Value: Point | null = null;", code);
        Assert.DoesNotContain("Microsoft.Msagl", code);
    }

    [Fact]
    public void Project_Fqcn_Array_Shortens_To_Last_Segment()
    {
        var (code, _) = FieldTypeEmission("Microsoft.Msagl.Core.Geometry.Point[]");
        Assert.Contains("public Value: Point[] = [];", code);
    }

    [Fact]
    public void Project_Fqcn_Generic_Arg_Shortens_Inside_Container()
    {
        var (code, _) = FieldTypeEmission("CSharpList<Microsoft.Msagl.Core.Data.Node>");
        Assert.Contains("public Value: CSharpList<Node> | null = null;", code);
    }

    [Fact]
    public void Iterable_Keeps_Name_And_Maps_Args()
    {
        var (code, _) = FieldTypeEmission("Iterable<Integer>");
        Assert.Contains("public Value: Iterable<number> | null = null;", code);
    }

    [Fact]
    public void CSharpDictionary_Nested_Fqcn_Args_Shorten()
    {
        var (code, _) = FieldTypeEmission("CSharpDictionary<String, Microsoft.Msagl.Core.Data.Node>");
        Assert.Contains("public Value: CSharpDictionary<string, Node> | null = null;", code);
    }
}
