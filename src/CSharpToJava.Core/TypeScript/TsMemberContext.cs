using System.Collections.Generic;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript.Rewriters;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 类成员上下文（Round 16，MSAGL TS2663/TS2662 收敛）：raw 文本发射时把裸实例成员名
/// 补 this. 前缀、裸静态成员名补 ClassName. 前缀。集合内名字均为**发射后**的名字
/// （与发射点使用完全相同的命名函数：字段/方法 SafeIdentifier、属性访问器用属性名），
/// 方法与 TsMemberWriter/TsStatementWriter/TsExpressionWriter 的可变 MemberContext
/// 属性对接；null/空集合保持既有行为零变化。
/// </summary>
public sealed class TsMemberContext
{
    /// <summary>发射用类名（SafeClassName，不含泛型实参）；静态成员限定前缀。</summary>
    public string? ClassName { get; init; }

    /// <summary>发射后实例成员名（字段/方法/属性访问器名）。</summary>
    public IReadOnlySet<string>? InstanceMembers { get; init; }

    /// <summary>发射后静态成员名（字段/方法/属性访问器名）。</summary>
    public IReadOnlySet<string>? StaticMembers { get; init; }

    /// <summary>类类型参数名（禁止加前缀的类型位置标识符）。</summary>
    public IReadOnlySet<string>? TypeParameters { get; init; }

    /// <summary>
    /// 类型名（嵌套类型发射名）：裸引用即顶层类名/类型位，永不补 this./类名. 前缀。
    /// C# "Color Color" 模式（属性名 == 嵌套类型名，如 Curve.ParallelogramNodeOverICurve）
    /// 下裸标识符是类型限定静态调用（Type.createX()），不是实例成员访问。
    /// </summary>
    public IReadOnlySet<string>? TypeNames { get; init; }

    /// <summary>
    /// 自类型成员（"Color Color" 模式：成员名 == 自身类型名，如
    /// `SolvingMethod SolvingMethod` 属性 / `ParallelogramNodeOverICurve` 属性）：
    /// 这类成员名后跟 '.' 时在 C# 中是**类型限定静态访问**（SolvingMethod.Precond...、
    /// Type.createX()），不是实例成员访问，禁止补 this. 前缀；不跟 '.' 的值位置
    /// （x = SolvingMethod;）仍是属性读，正常补前缀。
    /// </summary>
    public IReadOnlySet<string>? SelfTypedMembers { get; init; }

    /// <summary>无任何可限定成员（或类名缺失）时 pass 直接原样返回。</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(ClassName)
        || ((InstanceMembers == null || InstanceMembers.Count == 0)
            && (StaticMembers == null || StaticMembers.Count == 0));

    /// <summary>
    /// 从类声明构建成员上下文。方法名与发射点完全一致：属性访问器角色
    /// （Getter/GetterSetter）按 TsTypeWriter.EmitClass 的 accessorRoles 重组为属性名
    /// （method.Name.Substring(3) 经 SafeIdentifier）；WriteOnly/无角色方法保持方法名
    /// （WriteOnly 仍经 EmitMethod 发射）；字段直接取 SafeIdentifier(字段名)。
    /// isEnumTypeName：项目级枚举类型名判定（[Flags]/显式值枚举映射为 int 后 IR 类型
    /// 丢失枚举名，TypeLastName 比对失效——MSAGL ScanDirection/VertexEntry 的
    /// "Direction Direction" 属性即此形态）；成员名命中枚举类型名时也进 SelfTypedMembers。
    /// </summary>
    public static TsMemberContext From(
        JavaClassDeclaration cls,
        IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? accessorRoles,
        Func<string, bool>? isEnumTypeName = null)
    {
        var instance = new HashSet<string>(System.StringComparer.Ordinal);
        var statics = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var field in cls.Fields)
        {
            var name = TsNaming.SafeIdentifier(field.Name);
            ((field.Modifiers & JavaModifiers.Static) != 0 ? statics : instance).Add(name);
        }

        foreach (var method in cls.Methods)
        {
            var isStatic = (method.Modifiers & JavaModifiers.Static) != 0;
            string name;
            if (accessorRoles != null
                && accessorRoles.TryGetValue(method, out var role)
                && role is TsAccessorRole.Getter or TsAccessorRole.GetterSetter)
            {
                // 与 TsTypeWriter.EmitClass 的 propName = method.Name.Substring(3) 一致
                // （发射时再经 SafeIdentifier；此处同步应用同一命名函数）。
                name = method.Name.Length > 3
                    ? TsNaming.SafeIdentifier(method.Name.Substring(3))
                    : TsNaming.SafeIdentifier(method.Name);
            }
            else
            {
                name = TsNaming.SafeIdentifier(method.Name);
            }
            (isStatic ? statics : instance).Add(name);
        }

        var typeParameters = cls.TypeParameters.Count > 0
            ? new HashSet<string>(cls.TypeParameters.Select(tp => tp.Name), System.StringComparer.Ordinal)
            : null;

        // 嵌套类型发射为文件顶层类（TsTypeWriter.EmitClass 的 NestedTypes 循环），
        // 裸引用即顶层类名：属性/嵌套类型同名时不得补 this. 前缀。
        var typeNames = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var nested in cls.NestedTypes)
        {
            typeNames.Add(TsNaming.SafeClassName(nested.Name));
        }

        // 自类型成员（成员名 == 自身类型名）：后跟 '.' 时是类型限定静态访问。
        // 字段取 field.Type；属性访问器取 getter 的 ReturnType（发射名为属性名）。
        var selfTyped = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var field in cls.Fields)
        {
            var fname = TsNaming.SafeIdentifier(field.Name);
            if (TypeLastName(field.Type) == fname || (isEnumTypeName != null && isEnumTypeName(fname)))
            {
                selfTyped.Add(fname);
            }
        }
        foreach (var method in cls.Methods)
        {
            string name;
            if (accessorRoles != null
                && accessorRoles.TryGetValue(method, out var role)
                && role is TsAccessorRole.Getter or TsAccessorRole.GetterSetter)
            {
                name = method.Name.Length > 3
                    ? TsNaming.SafeIdentifier(method.Name.Substring(3))
                    : TsNaming.SafeIdentifier(method.Name);
            }
            else
            {
                name = TsNaming.SafeIdentifier(method.Name);
            }
            if (TypeLastName(method.ReturnType) == name
                || (isEnumTypeName != null && isEnumTypeName(name)))
            {
                selfTyped.Add(name);
            }
        }

        return new TsMemberContext
        {
            ClassName = TsNaming.SafeClassName(cls.Name),
            InstanceMembers = instance,
            StaticMembers = statics,
            TypeParameters = typeParameters,
            TypeNames = typeNames.Count == 0 ? null : typeNames,
            SelfTypedMembers = selfTyped.Count == 0 ? null : selfTyped,
        };
    }

    /// <summary>
    /// 类型字符串的最外层类型名（去泛型实参/数组后缀/命名空间前缀）：
    /// "SolvingMethod" → "SolvingMethod"；"CSharpList&lt;Point&gt;" → "CSharpList"；
    /// "int[]" → "int"；"Microsoft.Msagl.Core.Geometry.Point" → "Point"。
    /// </summary>
    private static string TypeLastName(string type)
    {
        var t = type;
        var lt = t.IndexOf('<');
        var lb = t.IndexOf('[');
        if (lt >= 0 || lb >= 0)
        {
            var cut = lt < 0 ? lb : (lb < 0 ? lt : Math.Min(lt, lb));
            t = t.Substring(0, cut);
        }
        var dot = t.LastIndexOf('.');
        if (dot >= 0)
        {
            t = t.Substring(dot + 1);
        }
        return t.Trim();
    }
}
