using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 14：泛型类型/方法/约束发射。
/// 断言修订说明（以 IR/发射器实测为准）：
/// - 方法名为 Java camelCase（既有约定，Task 10/11/13 实证）：identity/max，非 Identity/Max；
/// - TS 语法要求方法类型参数位于方法名之后：identity&lt;T&gt;(x: T): T；
/// - 约束 bound 为 Java 风味名（IComparable → Comparable），经 Java→TS 类型表映射为
///   object（内置映射，见 TsMemberMappingTable；spec §1.4.3 约束仅部分表达）；
/// - 多 bound（Java "A & B" 交叉约束）TS 不支持：取首个 bound，其余以诊断注释丢弃。
/// </summary>
public class TsGenericEmissionTests : TsConversionTestBase
{
    private static string EmitType(JavaTypeDeclaration type)
    {
        var table = new TsMemberMappingTable(null, null);
        var imports = new TsImportCollector();
        var emitter = new TypeScriptEmitter();
        return emitter.EmitType(type, table, imports);
    }

    [Fact]
    public void Generic_Class_Emits_Type_Parameter()
    {
        var result = Convert("""
class Container<T>
{
    T value;
}
""");
        Assert.True(result.Success);
        Assert.Contains("export class Container<T>", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Generic_Class_With_Constraint_Emits_Extends()
    {
        var result = Convert("""
class Container<T> where T : IComparable
{
    T value;
}
""");
        Assert.True(result.Success);
        // C# IComparable 在 IR 中映射为 Java 风味约束名 Comparable，经类型表映射为
        // object（Task 14 内置映射），断言验证 extends 结构 + 映射后的合法 TS bound
        Assert.Contains("Container<T extends object>", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Generic_Method_Emits_Type_Parameter()
    {
        var result = Convert("""
class Utils
{
    T Identity<T>(T x)
    {
        return x;
    }
}
""");
        Assert.True(result.Success);
        // 方法名 Java camelCase（identity）；TS 语法：类型参数在方法名后
        Assert.Contains("identity<T>(x: T): T", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Generic_Method_With_Constraint_Emits_Extends()
    {
        var result = Convert("""
class Utils
{
    T Max<T>(T a, T b) where T : IComparable
    {
        return a;
    }
}
""");
        Assert.True(result.Success);
        // 方法名 Java camelCase（max）；bound 映射为 object
        Assert.Contains("max<T extends object>(a: T, b: T): T", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Multiple_Type_Parameters_Emit_All()
    {
        var result = Convert("""
class Pair<K, V>
{
    K key;
    V value;
}
""");
        Assert.True(result.Success);
        Assert.Contains("Pair<K, V>", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Multiple_Bounds_Drop_Extra_With_Diagnostic_Comment()
    {
        // TS 约束只支持单一类型（无交叉约束）：多 bound 取首个，其余以诊断注释丢弃
        var result = Convert("""
class Multi<T> where T : IComparable, IDisposable
{
    T value;
}
""");
        Assert.True(result.Success);
        Assert.Contains("Multi<T extends object /* cs2j: dropped bounds: AutoCloseable */>", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Type_Parameter_Name_Is_Safe_Identifier()
    {
        // 类型参数名过 TsNaming.SafeIdentifier（TS 保留字 default -> default$）
        var cls = new JavaClassDeclaration { Name = "Foo", Modifiers = JavaModifiers.Public };
        cls.TypeParameters.Add(new JavaTypeParameter("default"));
        var result = EmitType(cls);
        Assert.Contains("Foo<default$>", result);
        AssertCompiles(result);
    }
}
