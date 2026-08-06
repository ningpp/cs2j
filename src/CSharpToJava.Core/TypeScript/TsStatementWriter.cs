using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 语句发射器：遍历 JavaStatement IR 节点，输出 TS 语句文本（spec §2.5 语句部分）。
/// </summary>
public sealed class TsStatementWriter
{
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
    /// </summary>
    private string ConvertForInitializer(string javaInit)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            javaInit.Trim(), @"^(?:final\s+)?(\w[\w.<>\[\], ]*?)\s+(\w+)\s*=\s*(.+)$");
        if (!m.Success) return javaInit;
        var tsType = _exprWriter.MapType(m.Groups[1].Value.Trim());
        return $"let {TsNaming.SafeIdentifier(m.Groups[2].Value)}: {tsType} = {m.Groups[3].Value}";
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
        // 实际属性名：TryBody/FinallyBody（非 TryBlock/FinallyBlock）
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"{ind}try ");
        sb.AppendLine(Emit(tryCatch.TryBody, indentLevel));

        foreach (var clause in tryCatch.CatchClauses)
        {
            // catch 按类型过滤 -> instanceof 链（TS catch 无类型参数，只能捕获单一变量）
            var exVar = string.IsNullOrEmpty(clause.VariableName) ? "e" : TsNaming.SafeIdentifier(clause.VariableName);
            if (!string.IsNullOrEmpty(clause.ExceptionType) && clause.ExceptionType != "Exception" && clause.ExceptionType != "Throwable")
            {
                sb.AppendLine($"{ind}}} catch ({exVar}) {{");
                sb.AppendLine($"{ind}{_indent}if (!({exVar} instanceof {clause.ExceptionType})) throw {exVar};");
            }
            else
            {
                sb.AppendLine($"{ind}}} catch ({exVar}) {{");
            }
            sb.Append(Emit(clause.Body, indentLevel + 1));
        }

        if (tryCatch.FinallyBody != null)
        {
            sb.AppendLine();
            sb.Append($"{ind}finally ");
            sb.Append(Emit(tryCatch.FinallyBody, indentLevel));
        }
        return sb.ToString();
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