using System.Text;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript.Rewriters;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 类型声明发射器：class/interface/enum 发射为 TS（spec §2.5 类型声明部分）。
/// </summary>
public sealed class TsTypeWriter
{
    private readonly TsMemberWriter _memberWriter;
    private readonly IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? _accessorRoles;
    private readonly string _indent;

    public TsTypeWriter(TsMemberWriter memberWriter, string indent = "    ",
        IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? accessorRoles = null)
    {
        _memberWriter = memberWriter;
        _accessorRoles = accessorRoles;
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

        // 接口（implements 类型经 Java→TS 映射：框架接口 Comparable/AutoCloseable → Object；
        // 用户接口未命中类型表时原样发射，泛型实参逐段映射，见 MapHeritageType）
        if (cls.ImplementedTypes.Count > 0)
        {
            sb.Append($" implements {string.Join(", ", cls.ImplementedTypes.Select(MapHeritageType))}");
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

        // 方法（属性访问器重组：Task 16 roles 侧表——自动属性字段简化、只读/受限 setter/
        // 表达式体 → 访问器、只写 → 方法 + 诊断注释；未标记方法维持原发射）
        var methodBlocks = new List<string>();
        foreach (var method in cls.Methods)
        {
            if (_accessorRoles != null && _accessorRoles.TryGetValue(method, out var role))
            {
                var propName = method.Name.Substring(3);
                switch (role)
                {
                    case TsAccessorRole.Getter:
                        methodBlocks.Add(_memberWriter.EmitPropertyAccessorGetter(method, propName, indentLevel + 1));
                        continue;
                    case TsAccessorRole.GetterSetter when method.Name.StartsWith("get"):
                        // getter + setter 访问器对：setter 随后续循环跳过（不重复发射）
                        var setter = cls.Methods.FirstOrDefault(m => m != method && m.Name == "set" + propName);
                        var pairBlock = _memberWriter.EmitPropertyAccessorGetter(method, propName, indentLevel + 1);
                        if (setter != null)
                        {
                            pairBlock += Environment.NewLine + Environment.NewLine
                                       + _memberWriter.EmitPropertyAccessorSetter(setter, propName, indentLevel + 1);
                        }
                        methodBlocks.Add(pairBlock);
                        continue;
                    case TsAccessorRole.GetterSetter:
                        continue;
                    case TsAccessorRole.WriteOnly:
                        // 只写属性无 TS 对应形态：诊断注释 + 维持 setter 方法发射
                        methodBlocks.Add($"{ind}{_indent}/* cs2j: write-only property {propName}: no TS counterpart; kept as setter method */");
                        methodBlocks.Add(_memberWriter.EmitMethod(method, indentLevel + 1));
                        continue;
                    case TsAccessorRole.Field:
                        // 字段简化：访问器方法已由 rewriter 从 IR 移除，防御性跳过
                        continue;
                }
            }
            methodBlocks.Add(_memberWriter.EmitMethod(method, indentLevel + 1));
        }
        if (methodBlocks.Count > 0)
        {
            sb.AppendLine(string.Join(Environment.NewLine + Environment.NewLine, methodBlocks));
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

        // 继承的接口（实际属性名：ExtendedTypes，非 ImplementedTypes；类型经 Java→TS 映射）
        if (iface.ExtendedTypes.Count > 0)
        {
            sb.Append($" extends {string.Join(", ", iface.ExtendedTypes.Select(MapHeritageType))}");
        }

        sb.AppendLine(" {");

        // 属性：C# 属性在 IR 中为自动生成的 getX()/setX(value) 方法对（PropertyTransformer
        // 命名 "get"/"set" + PascalCase，IsAutoGenerated = true），合并为 TS 属性声明：
        // - 有 getter → 属性声明；仅 getter 加 readonly 前缀（类型经 Java→TS 映射）
        // - 仅 setter（只写属性）无 TS 对应形态 → 保留 setter 方法 + 诊断注释
        var propertyGroups = iface.Methods
            .Where(m => IsAccessor(m, out _))
            .GroupBy(m => m.Name.Substring(3))
            .ToList();

        var pureMethods = iface.Methods.Where(m => !IsAccessor(m, out _)).ToList();

        foreach (var grp in propertyGroups)
        {
            var getter = grp.FirstOrDefault(m => m.Name.StartsWith("get"));
            if (getter != null)
            {
                var readOnly = grp.All(m => !m.Name.StartsWith("set"));
                sb.Append(ind).Append(_indent)
                  .AppendLine($"{(readOnly ? "readonly " : "")}{TsNaming.SafeIdentifier(grp.Key)}: {_memberWriter.MapType(getter.ReturnType)};");
            }
            else
            {
                var setter = grp.First(m => m.Name.StartsWith("set"));
                var parms = string.Join(", ", setter.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_memberWriter.MapType(p.Type)}"));
                sb.Append(ind).Append(_indent)
                  .AppendLine($"/* cs2j: write-only property {grp.Key}: no TS counterpart; kept as setter method */");
                sb.Append(ind).Append(_indent)
                  .AppendLine($"{TsNaming.SafeIdentifier(setter.Name)}({parms}): {_memberWriter.MapType(setter.ReturnType)};");
            }
        }

        // 纯方法（接口无修饰符，无 body；参数/返回类型经 Java→TS 映射，与类成员一致）
        foreach (var method in pureMethods)
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
    /// 检测接口属性访问器方法（getX/setX，PropertyTransformer 产出，IsAutoGenerated = true），
    /// 返回去除 get/set 前缀后的 PascalCase 属性名（保持原名，如 X → X）。
    /// </summary>
    private static bool IsAccessor(JavaMethodDeclaration m, out string propName)
    {
        propName = "";
        if (!m.IsAutoGenerated || m.Name.Length <= 3) return false;
        var isGet = m.Name.StartsWith("get");
        var isSet = m.Name.StartsWith("set");
        if (!isGet && !isSet) return false;
        // 带参 getter（如索引器形态 getItem(index)）不可能合并为 TS 属性，保持方法形态；
        // setter 恒带 value 参数，不在此列（索引器 setter 名为裸 set，长度 ≤ 3 已排除）
        if (isGet && m.Parameters.Count > 0) return false;
        propName = m.Name.Substring(3);
        return propName.Length > 0 && char.IsUpper(propName[0]);
    }

    /// <summary>
    /// implements/extends heritage 类型发射：Java 风味名经 Java→TS 类型表映射。
    /// - 整串命中类型表（如 Comparable → object）：implements/extends 位置是表达式/类型引用
    ///   位置，`object` 关键字非法（tsc TS2552，TS 5.x/7.x 一致）→ 降级为全局类型 Object；
    /// - 未命中（用户接口）：泛型实参逐段映射后回拼（IBase&lt;Integer&gt; → IBase&lt;number&gt;）；
    ///   命中且映射结果为非泛型（object）时丢弃原类型实参（Comparable&lt;Point&gt; → Object）。
    /// 嵌套泛型实参（如 IBase&lt;Pair&lt;Integer, Integer&gt;&gt;）逐段映射不递归：产物为
    /// 非法 TS（tsc TS2304），回拼后追加 `/* cs2j: unmapped nested generic args */` 诊断注释，
    /// 完整修复（深度递归映射 + import 收集）随命名管线任务登记。
    /// </summary>
    private string MapHeritageType(string javaOrCsharpType)
    {
        // ① 整串精确匹配（配置表可收录完整泛型形态，保持查找顺序一致）
        var exact = _memberWriter.MapType(javaOrCsharpType);
        if (exact != javaOrCsharpType) return exact == "object" ? "Object" : exact;

        // ② 泛型形态分解：Raw<A, B>
        var lt = javaOrCsharpType.IndexOf('<');
        if (lt > 0 && javaOrCsharpType.EndsWith(">"))
        {
            var raw = javaOrCsharpType[..lt].Trim();
            var mappedRaw = _memberWriter.MapType(raw);
            if (mappedRaw == raw)
            {
                // 用户接口（同文件可解析）：逐参映射后回拼。含嵌套泛型实参
                // （如 Pair<Integer, Integer>）时按逗号切分无法完整映射（Pair 未命中、
                // Integer 留 Java 风味名，产物非法 TS，tsc TS2304）→ 回拼后追加诊断注释；
                // 单层实参全部命中（IBase<string>/IRepo<int>）不加注释。
                var argsText = javaOrCsharpType[(lt + 1)..^1];
                var mappedArgs = argsText
                    .Split(',')
                    .Select(a => _memberWriter.MapType(a.Trim()));
                var joined = $"{raw}<{string.Join(", ", mappedArgs)}>";
                return argsText.Contains('<')
                    ? joined + " /* cs2j: unmapped nested generic args */"
                    : joined;
            }
            // 框架接口（Comparable/AutoCloseable → object）：目标为非泛型对象类型，丢弃实参
            return mappedRaw == "object" ? "Object" : mappedRaw;
        }

        // ③ 非泛型未命中：原样（同文件用户接口）
        return javaOrCsharpType;
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
