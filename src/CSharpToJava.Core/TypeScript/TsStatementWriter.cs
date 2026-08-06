using System.Text;
using System.Text.RegularExpressions;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 语句发射器：遍历 JavaStatement IR 节点，输出 TS 语句文本（spec §2.5 语句部分）。
/// </summary>
public sealed class TsStatementWriter
{
    private static readonly Regex LongLiteralPattern = new(@"(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*)[lL]");

    private static readonly HashSet<string> KnownSimpleTypes = new(StringComparer.Ordinal)
    {
        "number", "string", "boolean", "bigint", "any", "unknown", "void", "never", "object", "symbol", "undefined", "null",
    };

    private readonly TsExpressionWriter _exprWriter;
    private readonly string _indent;

    public TsStatementWriter(TsExpressionWriter exprWriter, string indent = "    ")
    {
        _exprWriter = exprWriter;
        _indent = indent;
    }

    public string Emit(JavaStatement stmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        return stmt switch
        {
            JavaBlockStatement block => EmitBlock(block, indentLevel),
            JavaVariableDeclarationStatement varDecl => EmitVarDecl(varDecl, ind),
            JavaExpressionStatement exprStmt => $"{ind}{_exprWriter.ToInlineString(exprStmt.Expression)};",
            JavaReturnStatement ret => ret.Expression != null
                ? $"{ind}return {_exprWriter.ToInlineString(ret.Expression)};"
                : $"{ind}return;",
            JavaIfStatement ifStmt => EmitIf(ifStmt, indentLevel),
            JavaForEachStatement forEach => EmitForEach(forEach, indentLevel),
            JavaForStatement forStmt => EmitFor(forStmt, indentLevel),
            JavaWhileStatement whileStmt => EmitWhile(whileStmt, indentLevel),
            JavaDoWhileStatement doWhile => EmitDoWhile(doWhile, indentLevel),
            JavaTryCatchStatement tryCatch => EmitTryCatch(tryCatch, indentLevel),
            JavaThrowStatement throwStmt => $"{ind}throw {_exprWriter.ToInlineString(throwStmt.Expression)};",
            JavaBreakStatement => $"{ind}break;",
            JavaContinueStatement => $"{ind}continue;",
            JavaSwitchStatement sw => EmitSwitch(sw, indentLevel),
            JavaRawStatement raw => $"{ind}{raw.Code}",
            _ => $"{ind}/* cs2j: unsupported statement {stmt.GetType().Name} */",
        };
    }

    private string EmitBlock(JavaBlockStatement block, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"{ind}{{");
        foreach (var s in block.Statements)
        {
            sb.AppendLine(Emit(s, indentLevel + 1));
        }
        sb.Append($"{ind}}}");
        return sb.ToString();
    }

    private string EmitVarDecl(JavaVariableDeclarationStatement decl, string ind)
    {
        // 实际属性名：Type/Name；类型需经 Java→TS 映射（IR 为 Java 风味）
        var tsType = _exprWriter.MapType(decl.Type);
        var init = decl.Initializer != null ? $" = {_exprWriter.ToInlineString(decl.Initializer)}" : "";
        return $"{ind}let {TsNaming.SafeIdentifier(decl.Name)}: {tsType}{init};";
    }

    private string EmitIf(JavaIfStatement ifStmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}if ({_exprWriter.ToInlineString(ifStmt.Condition)}) ");
        sb.Append(Emit(ifStmt.ThenBody, indentLevel));
        if (ifStmt.ElseBody != null)
        {
            sb.Append($" else ");
            sb.Append(Emit(ifStmt.ElseBody, indentLevel));
        }
        return sb.ToString();
    }

    private string EmitForEach(JavaForEachStatement forEach, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}for (const {TsNaming.SafeIdentifier(forEach.VariableName)} of {_exprWriter.ToInlineString(forEach.Collection)}) ");
        sb.Append(Emit(forEach.Body, indentLevel));
        return sb.ToString();
    }

    private string EmitFor(JavaForStatement forStmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        // JavaForStatement.Initializer/Increment 均为字符串（非表达式节点）。
        // Initializer 形如 "int i = 0"（Java 风味），转换为 "let i: number = 0"；
        // 无法匹配时原样输出 + 诊断注释（M2 细化）。
        var init = forStmt.Initializer != null ? ConvertForInitializer(forStmt.Initializer) : "";
        var cond = forStmt.Condition != null ? _exprWriter.ToInlineString(forStmt.Condition) : "";
        var upd = forStmt.Increment ?? "";
        var sb = new StringBuilder();
        sb.Append($"{ind}for ({init}; {cond}; {upd}) ");
        sb.Append(Emit(forStmt.Body, indentLevel));
        return sb.ToString();
    }

    /// <summary>
    /// "int i = 0" / "final int i = 0" -> "let i: number = 0"；不匹配则原样保留。
    /// 无法映射的类型（泛型/数组/未知类型）与多变量初始化器输出显式诊断注释，避免静默产出非法 TS。
    /// </summary>
    private string ConvertForInitializer(string javaInit)
    {
        var trimmed = javaInit.Trim();
        var m = Regex.Match(trimmed, @"^(?:final\s+)?(\w[\w.<>\[\], ]*?)\s+(\w+)\s*=\s*(.+)$");
        if (!m.Success) return javaInit;

        var init = m.Groups[3].Value;

        // 多变量（如 "int i = 0, j = 1"）无法用单个 let 表达 → 原样保留 + 显式诊断
        if (HasTopLevelComma(init))
        {
            return $"{trimmed} /* cs2j: multi-variable for-initializer */";
        }

        var javaType = m.Groups[1].Value.Trim();
        var name = m.Groups[2].Value;
        var tsType = _exprWriter.MapType(javaType);

        // 泛型/数组类型，或映射未命中（返回值 == 原类型且非已知 TS 简单类型）
        // → 保留可读形式 + 显式诊断，避免静默产出非法 TS；for 头内 init 段不加 ;
        if (javaType.IndexOfAny(new[] { '<', '>', '[', ']' }) >= 0
            || (tsType == javaType && !KnownSimpleTypes.Contains(javaType)))
        {
            return $"let {TsNaming.SafeIdentifier(name)}: {javaType} = {init} /* cs2j: unmapped for-initializer type */";
        }

        // long 字面量：100L -> 100n、0xFFL -> 0xFFn、1_000L -> 1_000n。
        // 初始化器含 " 时跳过（保守处理，防字符串内容如 "100L" 被篡改）
        if (init.IndexOf('"') < 0)
        {
            init = LongLiteralPattern.Replace(init, "$1n");
        }
        return $"let {TsNaming.SafeIdentifier(name)}: {tsType} = {init}";
    }

    /// <summary>
    /// 判断初始化器字符串中是否存在括号/尖括号外层的顶层逗号（多变量声明特征）。
    /// </summary>
    private static bool HasTopLevelComma(string s)
    {
        var depth = 0;
        foreach (var c in s)
        {
            switch (c)
            {
                case '(':
                case '[':
                case '{':
                case '<':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                case '>':
                    depth--;
                    break;
                case ',':
                    if (depth == 0) return true;
                    break;
            }
        }
        return false;
    }

    private string EmitWhile(JavaWhileStatement whileStmt, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}while ({_exprWriter.ToInlineString(whileStmt.Condition)}) ");
        sb.Append(Emit(whileStmt.Body, indentLevel));
        return sb.ToString();
    }

    private string EmitDoWhile(JavaDoWhileStatement doWhile, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.Append($"{ind}do ");
        sb.Append(Emit(doWhile.Body, indentLevel));
        sb.Append($" while ({_exprWriter.ToInlineString(doWhile.Condition)});");
        return sb.ToString();
    }

    private string EmitTryCatch(JavaTryCatchStatement tryCatch, int indentLevel)
    {
        // 实际属性名：TryBody/FinallyBody（非 TryBlock/FinallyBlock）。
        // JS/TS 文法每个 try 只允许一个 catch 子句，多 catch 转守卫链：
        // if/else-if instanceof 过滤，链尾补 throw 重抛保持 C# 语义；
        // 绑定变量统一用首子句名（C# 多 catch 不同变量名场景 M1 简化，记录）。
        // 分支若使用与绑定名不同的变量名，在分支 body 最前面发射 const 别名
        // （限制：body 内若自声明同名变量会冲突，M1 接受）。
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        // try 体经 EmitBlock 自闭合（以 } 结尾），catch 行直接接在其后，不再输出前导 }
        sb.Append($"{ind}try ");
        sb.Append(Emit(tryCatch.TryBody, indentLevel));

        if (tryCatch.CatchClauses.Count > 0)
        {
            var exVar = string.IsNullOrEmpty(tryCatch.CatchClauses[0].VariableName)
                ? "e"
                : TsNaming.SafeIdentifier(tryCatch.CatchClauses[0].VariableName);
            sb.AppendLine($" catch ({exVar}) {{");

            var isFirst = true;
            var closedWithBare = false;
            foreach (var clause in tryCatch.CatchClauses)
            {
                var isBare = string.IsNullOrEmpty(clause.ExceptionType)
                    || clause.ExceptionType == "Exception"
                    || clause.ExceptionType == "Throwable";

                if (isBare)
                {
                    if (isFirst)
                    {
                        // 唯一裸 catch 场景：catch 区域直接发射 body 内容，无 instanceof 守卫
                        EmitCatchBody(sb, clause, exVar, indentLevel + 1);
                    }
                    else
                    {
                        // 链尾 else 分支直接接 body 内容
                        sb.AppendLine($" else {{");
                        EmitCatchBody(sb, clause, exVar, indentLevel + 2);
                        sb.AppendLine($"{ind}{_indent}}}");
                    }
                    closedWithBare = true;
                    break;
                }

                // 守卫链块：if/else-if 行自带花括号，body 语句直接落在块内（贴合单 catch 输出形状）
                var head = isFirst
                    ? $"{ind}{_indent}if ({exVar} instanceof {clause.ExceptionType}) {{"
                    : $" else if ({exVar} instanceof {clause.ExceptionType}) {{";
                sb.AppendLine(head);
                EmitCatchBody(sb, clause, exVar, indentLevel + 2);
                sb.Append($"{ind}{_indent}}}");
                isFirst = false;
            }

            if (!closedWithBare)
            {
                // 全部为 typed 子句：不匹配任何 catch 则重抛，保持 C# 语义
                sb.AppendLine($" else {{");
                sb.AppendLine($"{ind}{_indent}{_indent}throw {exVar};");
                sb.AppendLine($"{ind}{_indent}}}");
            }

            sb.Append(ind).Append('}');
        }

        if (tryCatch.FinallyBody != null)
        {
            sb.AppendLine();
            sb.Append($"{ind}finally ");
            sb.Append(Emit(tryCatch.FinallyBody, indentLevel));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 发射 catch 分支 body；子句变量名与绑定名不同时先在 body 最前面发射 const 别名。
    /// </summary>
    private void EmitCatchBody(StringBuilder sb, JavaCatchClause clause, string exVar, int indentLevel)
    {
        var bodyIndent = new string(_indent[0], indentLevel * _indent.Length);
        var alias = GetCatchClauseAlias(clause, exVar);
        if (alias != null)
        {
            sb.AppendLine($"{bodyIndent}{alias}");
        }
        foreach (var s in clause.Body.Statements)
        {
            sb.AppendLine(Emit(s, indentLevel));
        }
    }

    /// <summary>
    /// 子句变量名 != 绑定名时生成 "const {ownName} = {exVar};"，否则返回 null（不加别名）。
    /// </summary>
    private static string? GetCatchClauseAlias(JavaCatchClause clause, string exVar)
    {
        if (string.IsNullOrEmpty(clause.VariableName)) return null;
        var ownName = TsNaming.SafeIdentifier(clause.VariableName);
        return ownName == exVar ? null : $"const {ownName} = {exVar};";
    }

    private string EmitSwitch(JavaSwitchStatement sw, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"{ind}switch ({_exprWriter.ToInlineString(sw.Expression)}) {{");
        foreach (var section in sw.Sections)
        {
            foreach (var label in section.Labels)
            {
                // JavaSwitchSection.Labels 已含冒号（如 "case 1:"/"default:"），不可重复追加
                sb.AppendLine($"{ind}{_indent}{label}");
            }
            foreach (var s in section.Statements)
            {
                sb.AppendLine(Emit(s, indentLevel + 2));
            }
        }
        sb.Append($"{ind}}}");
        return sb.ToString();
    }
}