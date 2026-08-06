using System.Text;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsBasicClassEmissionTests : TsConversionTestBase
{
    private static string EmitType(JavaTypeDeclaration type)
    {
        var table = new TsMemberMappingTable(null, null);
        var imports = new TsImportCollector();
        var emitter = new TypeScriptEmitter();
        return emitter.EmitType(type, table, imports);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
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
    public void String_Field_Default_Null_Widens_To_Nullable()
    {
        // 零值 null 在 strictNullChecks 下不可赋给 string（TS2322）：
        // 无初始化器且默认零值为 null 时，字段类型放宽为 string | null
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Public,
            Type = "string",
            Name = "Name",
        });
        var result = EmitType(cls);
        Assert.Contains("public Name: string | null = null;", result);
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

    [Fact]
    public void Multiple_Constructors_Only_First_Emits_Constructor_Keyword()
    {
        // TS 只允许一个 constructor（TS2392）：第二起以诊断注释占位，不发射非法语法
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Body = "this.X = 0;",
        });
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Parameters = { new JavaParameter("int", "x") },
            Body = "this.X = x;",
        });
        var result = EmitType(cls);
        Assert.Contains("constructor() {", result);
        Assert.Contains("// TODO(TS): 第 2 个构造器(x: number) 参数无法直译，需工厂/参数对象占位", result);
        Assert.Equal(1, CountOccurrences(result, "constructor("));
    }

    [Fact]
    public void Multiple_Constructors_With_This_Delegation_Emits_Comment_Only()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Body = "this.X = 0;",
        });
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Foo",
            Initializer = "this(0)",
            Body = "this.Y = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("// TODO(TS): 委托构造 this(0) 无法直译，仅注释说明", result);
        Assert.Equal(1, CountOccurrences(result, "constructor("));
    }

    [Fact]
    public void Derived_Class_Constructor_Emits_Super_Without_Args()
    {
        // 派生类构造器必须发射 super()（TS2377/TS17009）：无 base 实参时补 super();
        var cls = new JavaClassDeclaration { Name = "Derived", Modifiers = JavaModifiers.Public, ExtendedType = "Base" };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Derived",
            Body = "this.X = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("super();", result);
        Assert.Contains("export class Derived extends Base {", result);
    }

    [Fact]
    public void Derived_Class_Constructor_Emits_Super_With_Args()
    {
        var cls = new JavaClassDeclaration { Name = "Derived", Modifiers = JavaModifiers.Public, ExtendedType = "Base" };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Derived",
            Initializer = "super(1, \"a\")",
            Body = "this.X = 0;",
        });
        var result = EmitType(cls);
        Assert.Contains("super(1, \"a\");", result);
    }

    [Fact]
    public void Expression_Body_Method_Emits_Return_Block()
    {
        // 类成员体不支持 => 语法（TS1144）：表达式体方法转 { return expr; }
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "Double",
            Body = "this.X * 2",
            IsBodyExpression = true,
        });
        var result = EmitType(cls);
        Assert.Contains("public Double(): number {", result);
        Assert.Contains("return this.X * 2;", result);
        Assert.DoesNotContain("=>", result);
    }

    [Fact]
    public void Expression_Body_Void_Method_Emits_Statement_Block()
    {
        // void 表达式体：{ expr; } 不带 return（对齐 JavaMemberDeclaration 语义）
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Name = "Log",
            Body = "this.X = 0",
            IsBodyExpression = true,
        });
        var result = EmitType(cls);
        Assert.Contains("public Log(): void {", result);
        Assert.Contains("this.X = 0;", result);
        Assert.DoesNotContain("=>", result);
        Assert.DoesNotContain("return this.X = 0;", result);
    }

    [Fact]
    public void Nested_Type_Is_Emitted_At_Top_Level_After_Outer()
    {
        // 类体内不允许嵌套类型声明（TS1068）：嵌套类型提升到外层类之后、文件顶层
        var cls = new JavaClassDeclaration { Name = "Outer", Modifiers = JavaModifiers.Public };
        cls.NestedTypes.Add(new JavaClassDeclaration { Name = "Inner", Modifiers = JavaModifiers.Public });
        var result = EmitType(cls);
        var outerIdx = result.IndexOf("export class Outer", StringComparison.Ordinal);
        var outerCloseIdx = result.IndexOf("}", StringComparison.Ordinal);
        var innerIdx = result.IndexOf("export class Inner", StringComparison.Ordinal);
        Assert.True(outerIdx >= 0 && innerIdx > outerCloseIdx && outerCloseIdx > outerIdx, result);
    }

    [Fact]
    public void Long_Field_Initializers_Produce_Valid_Bigint_Literals()
    {
        // RenderConstantValue 产出 100L/0xFF00L/1_000_000L（Java 风味），必须转合法 TS bigint 字面量
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Ticks", Initializer = "100L" });
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "HexMask", Initializer = "0xFF00L" });
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Separated", Initializer = "1_000_000L" });
        var result = EmitType(cls);
        Assert.Contains("public Ticks: bigint = 100n;", result);
        Assert.Contains("public HexMask: bigint = 0xFF00n;", result);
        Assert.Contains("public Separated: bigint = 1_000_000n;", result);
    }

    [Fact]
    public void Float_Field_Initializer_Strips_F_Suffix()
    {
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "float", Name = "Ratio", Initializer = "1.5f" });
        var result = EmitType(cls);
        Assert.Contains("public Ratio: number = 1.5;", result);
    }

    [Fact]
    public void Interface_Method_Uses_Mapped_Types()
    {
        // 接口成员参数/返回类型必须走 Java→TS 映射（与类成员一致，I3）
        var iface = new JavaInterfaceDeclaration { Name = "ICounter", Modifiers = JavaModifiers.Public };
        iface.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "long",
            Name = "Increment",
            Parameters = { new JavaParameter("long", "step") },
        });
        var result = EmitType(iface);
        Assert.Contains("Increment(step: bigint): bigint;", result);
    }

    [Fact]
    public void Private_Constructor_Emits_Private()
    {
        // TS 支持 private/protected constructor：可见性直译
        var cls = new JavaClassDeclaration { Name = "Singleton", Modifiers = JavaModifiers.Public };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Private,
            ClassName = "Singleton",
        });
        var result = EmitType(cls);
        Assert.Contains("private constructor() {", result);
    }

    [Fact]
    public void Protected_Constructor_Emits_Protected()
    {
        var cls = new JavaClassDeclaration { Name = "AbstractBase", Modifiers = JavaModifiers.Public | JavaModifiers.Abstract };
        cls.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Protected,
            ClassName = "AbstractBase",
        });
        var result = EmitType(cls);
        Assert.Contains("protected constructor() {", result);
    }

    [Fact]
    public void All_Fixed_Features_Compile_With_Tsc()
    {
        // L3：汇总本轮审查修复的全部特性产物，走共享 tsc 工程 strict 编译（spec §5.2/§5.5）
        var sb = new StringBuilder();
        sb.AppendLine(EmitType(new JavaClassDeclaration { Name = "Base", Modifiers = JavaModifiers.Public }));

        var derived = new JavaClassDeclaration { Name = "Derived", Modifiers = JavaModifiers.Public, ExtendedType = "Base" };
        derived.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "boolean", Name = "Ready" });
        derived.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Derived",
            Body = "this.Ready = true;",
        });
        sb.AppendLine(EmitType(derived));

        var multi = new JavaClassDeclaration { Name = "Multi", Modifiers = JavaModifiers.Public };
        multi.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "int", Name = "X" });
        multi.Constructors.Add(new JavaConstructorDeclaration { Modifiers = JavaModifiers.Public, ClassName = "Multi" });
        multi.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ClassName = "Multi",
            Parameters = { new JavaParameter("int", "x") },
            Body = "this.X = x;",
        });
        sb.AppendLine(EmitType(multi));

        var exprBody = new JavaClassDeclaration { Name = "ExprBody", Modifiers = JavaModifiers.Public };
        exprBody.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "int", Name = "X", Initializer = "1" });
        exprBody.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "number",
            Name = "Double",
            Body = "this.X * 2",
            IsBodyExpression = true,
        });
        exprBody.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Name = "Reset",
            Body = "this.X = 0",
            IsBodyExpression = true,
        });
        sb.AppendLine(EmitType(exprBody));

        var outer = new JavaClassDeclaration { Name = "Outer", Modifiers = JavaModifiers.Public };
        outer.NestedTypes.Add(new JavaClassDeclaration { Name = "Inner", Modifiers = JavaModifiers.Public });
        sb.AppendLine(EmitType(outer));

        var nullable = new JavaClassDeclaration { Name = "Nullable", Modifiers = JavaModifiers.Public };
        nullable.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "string", Name = "Name" });
        sb.AppendLine(EmitType(nullable));

        var longs = new JavaClassDeclaration { Name = "Longs", Modifiers = JavaModifiers.Public };
        longs.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Ticks", Initializer = "100L" });
        longs.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Mask", Initializer = "0xFFL" });
        longs.Fields.Add(new JavaFieldDeclaration { Modifiers = JavaModifiers.Public, Type = "long", Name = "Big", Initializer = "1_000L" });
        sb.AppendLine(EmitType(longs));

        var iface = new JavaInterfaceDeclaration { Name = "ICounter", Modifiers = JavaModifiers.Public };
        iface.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "long",
            Name = "Increment",
            Parameters = { new JavaParameter("long", "step") },
        });
        sb.AppendLine(EmitType(iface));

        var priv = new JavaClassDeclaration { Name = "Priv", Modifiers = JavaModifiers.Public };
        priv.Constructors.Add(new JavaConstructorDeclaration { Modifiers = JavaModifiers.Private, ClassName = "Priv" });
        sb.AppendLine(EmitType(priv));

        var prot = new JavaClassDeclaration { Name = "Prot", Modifiers = JavaModifiers.Public };
        prot.Constructors.Add(new JavaConstructorDeclaration { Modifiers = JavaModifiers.Protected, ClassName = "Prot" });
        sb.AppendLine(EmitType(prot));

        AssertCompiles(sb.ToString());
    }
}