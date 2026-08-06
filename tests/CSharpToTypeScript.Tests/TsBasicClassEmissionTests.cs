using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsBasicClassEmissionTests
{
    private static string EmitType(JavaTypeDeclaration type)
    {
        var table = new TsMemberMappingTable(null, null);
        var imports = new TsImportCollector();
        var emitter = new TypeScriptEmitter();
        return emitter.EmitType(type, table, imports);
    }

    [Fact]
    public void Empty_Class_Emits_Export_Class()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        var result = EmitType(cls);
        Assert.Contains("export class Foo {", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void Class_With_Field_Emits_Field_With_Type()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "number",
            Name = "X",
        });
        var result = EmitType(cls);
        // 无初始化器时补默认零值（= 0），见 Field_Without_Initializer_Adds_Default_Zero
        Assert.Contains("public X: number", result);
    }

    [Fact]
    public void Field_Without_Initializer_Adds_Default_Zero()
    {
        // 无显式初始化器的字段必须补默认零值（spec §2.5 成员表）
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "number",
            Name = "Count",
            // 无 Initializer
        });
        var result = EmitType(cls);
        Assert.Contains("public Count: number = 0;", result);
    }

    [Fact]
    public void Bool_Field_Default_False()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "boolean",
            Name = "Flag",
        });
        var result = EmitType(cls);
        Assert.Contains("public Flag: boolean = false;", result);
    }

    [Fact]
    public void String_Field_Default_Null()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "string",
            Name = "Name",
        });
        var result = EmitType(cls);
        Assert.Contains("public Name: string = null;", result);
    }

    [Fact]
    public void Method_Emits_With_Body()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "GetX",
            Body = "return this.X;",
        });
        var result = EmitType(cls);
        Assert.Contains("public GetX(): number {", result);
        Assert.Contains("return this.X;", result);
    }

    [Fact]
    public void Constructor_Emits_Constructor_Keyword()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Body = "this.X = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("constructor() {", result);
    }

    [Fact]
    public void Static_Field_Emits_Static()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static | JavaModifiers.Final,
            Type = "number",
            Name = "PI",
            Initializer = "3.14",
        });
        var result = EmitType(cls);
        Assert.Contains("public static readonly PI: number = 3.14;", result);
    }

    [Fact]
    public void Abstract_Method_Emits_Abstract()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public | JavaModifiers.Abstract };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Abstract,
            ReturnType = "number",
            Name = "Compute",
        });
        var result = EmitType(cls);
        Assert.Contains("export abstract class Foo", result);
        Assert.Contains("public abstract Compute(): number;", result);
    }

    [Fact]
    public void Interface_Emits_Export_Interface()
    {
        var iface = new JavaInterfaceDeclaration { Name = "IFoo", Modifiers = JavaModifiers.Public };
        iface.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "Bar",
        });
        var result = EmitType(iface);
        Assert.Contains("export interface IFoo {", result);
        Assert.Contains("Bar(): number;", result);
    }

    [Fact]
    public void Enum_Emits_Export_Enum()
    {
        // 实际属性名：Values（非 Members）
        var en = new JavaEnumDeclaration { Name = "Color", Modifiers = JavaModifiers.Public };
        en.Values.Add("Red");
        en.Values.Add("Green");
        en.Values.Add("Blue");
        var result = EmitType(en);
        Assert.Contains("export enum Color {", result);
        Assert.Contains("Red,", result);
        Assert.Contains("Green,", result);
        Assert.Contains("Blue", result);
    }
}