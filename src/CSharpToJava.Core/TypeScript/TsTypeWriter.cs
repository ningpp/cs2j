using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 类型声明发射器：class/interface/enum 发射为 TS（spec §2.5 类型声明部分）。
/// </summary>
public sealed class TsTypeWriter
{
    private readonly TsMemberWriter _memberWriter;
    private readonly string _indent;

    public TsTypeWriter(TsMemberWriter memberWriter, string indent = "    ")
    {
        _memberWriter = memberWriter;
        _indent = indent;
    }

    public string Emit(JavaTypeDeclaration type, int indentLevel = 0)
    {
        return type switch
        {
            JavaClassDeclaration cls => EmitClass(cls, indentLevel),
            JavaInterfaceDeclaration iface => EmitInterface(iface, indentLevel),
            JavaEnumDeclaration en => EmitEnum(en, indentLevel),
            _ => $"/* cs2j: unsupported type {type.GetType().Name} */",
        };
    }

    private string EmitClass(JavaClassDeclaration cls, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // export
        sb.Append("export ");

        // abstract
        if ((cls.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        // sealed -> 注释标记（spec §2.5）
        if ((cls.Modifiers & JavaModifiers.Sealed) != 0) sb.Append("/* sealed */ ");

        sb.Append("class ");
        sb.Append(TsNaming.SafeClassName(cls.Name));

        // 类型参数（TS 约束仅支持单一类型，多 bound 降级见 FormatTypeParameters）
        sb.Append(FormatTypeParameters(cls.TypeParameters, _memberWriter.MapType));

        // 基类
        if (!string.IsNullOrEmpty(cls.ExtendedType))
        {
            sb.Append($" extends {cls.ExtendedType}");
        }

        // 接口
        if (cls.ImplementedTypes.Count > 0)
        {
            sb.Append($" implements {string.Join(", ", cls.ImplementedTypes)}");
        }

        sb.AppendLine(" {");

        // 字段
        foreach (var field in cls.Fields)
        {
            sb.AppendLine(_memberWriter.EmitField(field, indentLevel + 1));
        }

        if (cls.Fields.Count > 0 && (cls.Constructors.Count > 0 || cls.Methods.Count > 0))
        {
            sb.AppendLine();
        }

        // 构造器：TS 只允许一个 constructor（TS2392）。第一个发射为 constructor(...)，
        // 第二个起降级为诊断注释（M3 重载转工厂/参数对象占位），不发射非法语法。
        for (int i = 0; i < cls.Constructors.Count; i++)
        {
            if (i == 0)
            {
                sb.AppendLine(_memberWriter.EmitConstructor(cls.Constructors[i], cls.ExtendedType, indentLevel + 1));
            }
            else
            {
                var ctor = cls.Constructors[i];
                var parms = string.Join(", ", ctor.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_memberWriter.MapType(p.Type)}"));
                sb.Append(ind).Append(_indent)
                  .AppendLine($"// TODO(TS): 第 {i + 1} 个构造器({parms}) 参数无法直译，需工厂/参数对象占位");
                if (!string.IsNullOrEmpty(ctor.Initializer) && ctor.Initializer.StartsWith("this("))
                {
                    sb.Append(ind).Append(_indent)
                      .AppendLine($"// TODO(TS): 委托构造 {ctor.Initializer} 无法直译，仅注释说明");
                }
            }
            sb.AppendLine();
        }

        // 方法
        for (int i = 0; i < cls.Methods.Count; i++)
        {
            sb.AppendLine(_memberWriter.EmitMethod(cls.Methods[i], indentLevel + 1));
            if (i < cls.Methods.Count - 1) sb.AppendLine();
        }

        // 静态初始化块 -> static { } （ES2022）；实际属性是 Statements（List<string>，非 Body）
        foreach (var staticInit in cls.StaticInitializers)
        {
            sb.Append(ind).Append(_indent).AppendLine("static {");
            foreach (var stmtLine in staticInit.Statements)
            {
                if (!string.IsNullOrWhiteSpace(stmtLine))
                {
                    sb.Append(ind).Append(_indent).Append(_indent).AppendLine(stmtLine.Trim());
                }
            }
            sb.Append(ind).Append(_indent).AppendLine("}");
        }

        sb.Append(ind).Append('}');

        // 嵌套类型提升到外层类之后、文件顶层发射（TS1068：类体内不允许嵌套类型声明；
        // spec 顶层顺序：主类型 → 其嵌套类型；多个顶层类型各自后跟其嵌套类型）
        foreach (var nested in cls.NestedTypes)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(Emit(nested, indentLevel));
        }

        return sb.ToString();
    }

    private string EmitInterface(JavaInterfaceDeclaration iface, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        sb.Append("export interface ");
        sb.Append(TsNaming.SafeClassName(iface.Name));

        // 类型参数（TS 约束仅支持单一类型，多 bound 降级见 FormatTypeParameters）
        sb.Append(FormatTypeParameters(iface.TypeParameters, _memberWriter.MapType));

        // 继承的接口（实际属性名：ExtendedTypes，非 ImplementedTypes）
        if (iface.ExtendedTypes.Count > 0)
        {
            sb.Append($" extends {string.Join(", ", iface.ExtendedTypes)}");
        }

        sb.AppendLine(" {");

        // 成员（接口无修饰符，无 body；参数/返回类型经 Java→TS 映射，与类成员一致）
        foreach (var method in iface.Methods)
        {
            var parms = string.Join(", ", method.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_memberWriter.MapType(p.Type)}"));
            sb.Append(ind).Append(_indent)
              .AppendLine($"{TsNaming.SafeIdentifier(method.Name)}{FormatTypeParameters(method.TypeParameters, _memberWriter.MapType)}({parms}): {_memberWriter.MapType(method.ReturnType)};");
        }

        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    private string EmitEnum(JavaEnumDeclaration en, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // 普通 enum 直译（[Flags] 在 M2 Task 17 处理）；实际属性名：Values（非 Members）
        sb.AppendLine($"export enum {TsNaming.SafeClassName(en.Name)} {{");
        for (int i = 0; i < en.Values.Count; i++)
        {
            var comma = i < en.Values.Count - 1 ? "," : "";
            sb.Append(ind).Append(_indent).AppendLine($"{en.Values[i]}{comma}");
        }
        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 泛型类型参数格式化（spec §1.4.3：约束仅部分表达）。
    /// TS 约束只允许单一类型：无 bound → 裸名；单 bound → `T extends B`；
    /// 多 bound（Java "A & B" 交叉约束）丢弃其余并加诊断注释；
    /// 名称过 TsNaming.SafeIdentifier；首个 bound 经 Java→TS 类型表映射
    /// （IR 为 Java 风味名，如 Comparable → object，保证产物为合法 TS）。
    /// 静态化 + 注入 bound 映射函数，以便 TsMemberWriter.EmitMethod 复用
    /// （TsTypeWriter 依赖 TsMemberWriter，实例方法反向调用不可行）。
    /// </summary>
    public static string FormatTypeParameters(IReadOnlyList<JavaTypeParameter> typeParams, Func<string, string> mapBound)
    {
        if (typeParams.Count == 0) return "";

        var parts = typeParams.Select(tp =>
        {
            if (tp.Bounds.Count == 0) return TsNaming.SafeIdentifier(tp.Name);

            // TS 约束仅支持单一类型：取首个 bound；多 bound（Java "A & B"）
            // 丢弃其余并加诊断注释（spec §1.4.3：约束仅部分表达）
            var extra = tp.Bounds.Count > 1
                ? " /* cs2j: dropped bounds: " + string.Join(" & ", tp.Bounds.Skip(1)) + " */"
                : "";
            return $"{TsNaming.SafeIdentifier(tp.Name)} extends {mapBound(tp.Bounds[0])}{extra}";
        });
        return $"<{string.Join(", ", parts)}>";
    }
}
