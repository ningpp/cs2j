using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// record 发射 TS 化 rewriter（Task 27，M3）。共享管线在 TS 目标下同样产出 Java record
/// 形态（IR 仅含 RecordComponents，无字段/构造器/方法），TS 无 record 语法，在此将
/// record 类物化为合法 TS class（spec §4.3.2/§1.4.7）：
/// ① 继承 CsRecord 基类（值相等 equals/hashCode/toString 由兼容库基类提供，M4 语义）；
/// ② 每个位置分量 → public readonly 字段（TS 发射器补默认零值，构造器再赋值）；
/// ③ 位置分量访问器（Java record 惯例 getX()，前端把 C# p.X 译成 p.getX()）→ 合成
///    getter 方法（this. 前缀避免 TS2663）；
/// ④ 主构造器：参数即分量，体为 this.x = x; 逐字段赋值（EmitConstructor 对
///    ExtendedType 自动补 super()）；
/// ⑤ protected recordFields()：字段值列表，CsRecord 基类通用比较/哈希消费。
/// CsRecord 登记进 context.TsCompatSymbols 生成 @csharptotypescript/compat 导入。
/// 继承链：record 派生类已有 ExtendedType（基 record）时保持，链上基类递归处理。
/// </summary>
public sealed class TsRecordRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext? _context;

    public TsRecordRewriter(ConversionContext? context = null)
    {
        _context = context;
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var visited = (JavaClassDeclaration)base.VisitClassDeclaration(node);
        if (!visited.IsRecord)
        {
            return visited;
        }

        // ① 基类：无显式基类（普通 record）→ CsRecord；已有基类（record 继承）保持，
        // 链上基类由本 pass 递归物化（基类亦 IsRecord 时）
        if (string.IsNullOrEmpty(visited.ExtendedType))
        {
            visited.ExtendedType = "CsRecord";
        }

        // ② 字段 + ③ getter 方法
        var componentNames = new List<string>();
        foreach (var component in visited.RecordComponents)
        {
            var fieldName = component.Name;
            componentNames.Add(fieldName);
            if (visited.Fields.Any(f => f.Name == fieldName))
            {
                continue;
            }
            visited.Fields.Add(new JavaFieldDeclaration
            {
                Type = component.Type,
                Name = fieldName,
                Modifiers = JavaModifiers.Public | JavaModifiers.Final,
                IsRecordComponent = true,
            });
            var getterName = "get" + char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
            if (visited.Methods.Any(m => m.Name == getterName))
            {
                continue;
            }
            visited.Methods.Add(new JavaMethodDeclaration
            {
                Name = getterName,
                ReturnType = component.Type,
                Modifiers = JavaModifiers.Public,
                IsBodyExpression = true,
                Body = $"this.{fieldName}",
            });
        }

        // ③b（Task 41）：record 自身实例方法体中的裸分量访问器调用（Java 允许裸
        // getX()，TS 必须 this.getX()——TS2663）；分量 getter 名在物化后可知，
        // 故在 ③ 之后统一补 this. 前缀。静态方法（无 this）与 .getX() 成员访问跳过。
        QualifyBareComponentGetterCalls(visited, componentNames);


        if (componentNames.Count > 0 && visited.Constructors.Count == 0)
        {
            var ctor = new JavaConstructorDeclaration
            {
                ClassName = visited.Name,
                Modifiers = JavaModifiers.Public,
                Body = string.Join("\n", componentNames.Select(n => $"this.{n} = {n};")),
            };
            foreach (var component in visited.RecordComponents)
            {
                ctor.Parameters.Add(new JavaParameter(component.Type, component.Name));
            }
            visited.Constructors.Add(ctor);
        }

        // ⑤ recordFields()：字段值列表（CsRecord 基类 Equals/hashCode/toString 消费）
        if (!visited.Methods.Any(m => m.Name == "recordFields"))
        {
            var sb = new StringBuilder("return [");
            sb.Append(string.Join(", ", componentNames.Select(n => $"this.{n}")));
            sb.Append("];");
            visited.Methods.Add(new JavaMethodDeclaration
            {
                Name = "recordFields",
                ReturnType = "unknown[]",
                Modifiers = JavaModifiers.Protected,
                Body = sb.ToString(),
            });
        }

        _context?.TsCompatSymbols.Add("CsRecord");
        return visited;
    }
    /// <summary>裸分量访问器调用限定：getX( → this.getX(（标识符边界 + 跳字符串/字符字面量）。</summary>
    private static void QualifyBareComponentGetterCalls(JavaClassDeclaration cls, IReadOnlyList<string> componentNames)
    {
        if (componentNames.Count == 0)
        {
            return;
        }
        var getterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in componentNames)
        {
            getterNames.Add("get" + char.ToUpperInvariant(name[0]) + name.Substring(1));
        }
        foreach (var method in cls.Methods)
        {
            if ((method.Modifiers & JavaModifiers.Static) != 0 || string.IsNullOrEmpty(method.Body))
            {
                continue;
            }
            if (!getterNames.Any(g => method.Body.Contains(g, StringComparison.Ordinal)))
            {
                continue;
            }
            method.Body = QualifyBareGetterCalls(method.Body, getterNames);
        }
    }

    private static string QualifyBareGetterCalls(string code, HashSet<string> getterNames)
    {
        var sb = new StringBuilder(code.Length);
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i) - 1;
                sb.Append(code, start, i - start + 1);
                continue;
            }
            if (IsIdentifierChar(ch))
            {
                var start = i;
                while (i + 1 < code.Length && IsIdentifierChar(code[i + 1]))
                {
                    i++;
                }
                var word = code.Substring(start, i - start + 1);
                var isCall = i + 1 < code.Length && code[i + 1] == '(';
                var precededByDot = start > 0 && code[start - 1] == '.';
                if (isCall && !precededByDot && getterNames.Contains(word))
                {
                    sb.Append("this.").Append(word);
                }
                else
                {
                    sb.Append(word);
                }
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '$';
    }

    private static int SkipQuotedLiteral(string code, int start)
    {
        var quote = code[start];
        var i = start + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\' && i + 1 < code.Length)
            {
                i += 2;
                continue;
            }
            if (code[i] == quote)
            {
                return i + 1;
            }
            i++;
        }
        return code.Length;
    }
}
