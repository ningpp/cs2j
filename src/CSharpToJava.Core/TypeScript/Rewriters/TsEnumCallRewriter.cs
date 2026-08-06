using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 枚举转换调用点改写 rewriter（Task 17）：普通枚举经 C#→Java 共享管线转为 Java 枚举形态，
/// 转换/位运算调用点出现 getValue()/fromValue()/fromValueUnchecked()/ordinal()——TS 数字
/// 枚举即 number，调用点直译：x.getValue()/x.ordinal() → x；E.fromValue(v) → (v) as E
/// （剥除 (int)/(long) 数值转换包装；TS 数字枚举可持有任意数值，对齐 C# 未定义值语义）。
/// Enum.Parse/GetName（EnumHelper.* / Enum.getName）无 TS 对应 → 诊断 + 兼容库桩（M4）；
/// 枚举 ToString 在 TS 返回数字字符串（C# 返回成员名）→ 诊断。
/// 保守策略（C1）：仅当目标类型可解析且为已登记的枚举类型时才改写；不可解析 → 保留方法调用。
/// 结构化分支（JavaMethodCallExpression）与 raw 文本分支（JavaRawStatement/方法 Body/
/// 字段初始化器/静态初始化块）均覆盖；raw 扫描跳过字符串/字符字面量与注释。
/// </summary>
public sealed class TsEnumCallRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext? _context;
    private readonly HashSet<string> _enumTypeNames = new(StringComparer.Ordinal);
    private readonly Stack<MethodContext> _methodStack = new();
    private readonly Stack<ClassContext> _classStack = new();
    private readonly HashSet<string> _emittedDiagnostics = new(StringComparer.Ordinal);

    public TsEnumCallRewriter(ConversionContext? context = null)
    {
        _context = context;
    }

    private sealed class ClassContext
    {
        public required string ClassName { get; init; }
        public required Dictionary<string, string> InstanceFieldTypes { get; init; }
        public required Dictionary<string, string> StaticFieldTypes { get; init; }
    }

    private sealed class MethodContext
    {
        public required Dictionary<string, string> VariableTypes { get; init; }
    }

    // ─── 类型遍历 ─────────────────────────────────────────────────────

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        CollectEnumNames(node.TypeDeclarations);
        return base.VisitCompilationUnit(node);
    }

    private void CollectEnumNames(IEnumerable<JavaTypeDeclaration> types)
    {
        foreach (var type in types)
        {
            switch (type)
            {
                case JavaEnumDeclaration en:
                    _enumTypeNames.Add(en.Name);
                    break;
                case JavaClassDeclaration cls:
                    CollectEnumNames(cls.NestedTypes);
                    break;
                case JavaInterfaceDeclaration iface:
                    CollectEnumNames(iface.NestedTypes);
                    break;
            }
        }
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var instance = new Dictionary<string, string>(StringComparer.Ordinal);
        var statics = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in node.Fields)
        {
            var target = (field.Modifiers & JavaModifiers.Static) != 0 ? statics : instance;
            target[field.Name] = field.Type;
        }
        _classStack.Push(new ClassContext
        {
            ClassName = node.Name,
            InstanceFieldTypes = instance,
            StaticFieldTypes = statics,
        });
        try
        {
            var result = base.VisitClassDeclaration(node);
            // JavaSyntaxRewriter 不遍历静态初始化块（TsTypeWriter 直接发射 Statements），
            // 这里补改其中的枚举调用
            foreach (var block in node.StaticInitializers)
            {
                for (var i = 0; i < block.Statements.Count; i++)
                {
                    block.Statements[i] = RewriteEnumCalls(block.Statements[i]);
                }
            }
            return result;
        }
        finally
        {
            _classStack.Pop();
        }
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        _methodStack.Push(BuildMethodContext(node));
        try
        {
            node = base.VisitMethodDeclaration(node);
            if (!string.IsNullOrEmpty(node.Body))
            {
                node.Body = RewriteEnumCalls(node.Body);
            }
            return node;
        }
        finally
        {
            _methodStack.Pop();
        }
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in node.Parameters)
        {
            variables[parameter.Name] = parameter.Type;
        }
        _methodStack.Push(new MethodContext { VariableTypes = variables });
        try
        {
            node = base.VisitConstructorDeclaration(node);
            if (!string.IsNullOrEmpty(node.Body))
            {
                node.Body = RewriteEnumCalls(node.Body);
            }
            return node;
        }
        finally
        {
            _methodStack.Pop();
        }
    }

    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Initializer))
        {
            node.Initializer = RewriteEnumCalls(node.Initializer);
        }
        return base.VisitFieldDeclaration(node);
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (!string.IsNullOrEmpty(node.Code))
        {
            node.Code = RewriteEnumCalls(node.Code);
        }
        return base.VisitRawStatement(node);
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        if (node is JavaMethodCallExpression call)
        {
            call = base.VisitMethodCallExpression(call);
            return TryRewriteCall(call, out var replacement) ? replacement : call;
        }
        if (node is JavaRawExpression raw)
        {
            raw.Code = RewriteEnumCalls(raw.Code);
            return raw;
        }
        return base.VisitExpression(node);
    }

    // ─── 结构化调用点改写 ─────────────────────────────────────────────

    private bool TryRewriteCall(JavaMethodCallExpression call, out JavaExpression replacement)
    {
        replacement = null!;
        switch (call.MethodName)
        {
            case "getValue":
            case "ordinal":
                if (call.Arguments.Count != 0 || call.Target == null) return false;
                if (!IsEnumTypedExpression(call.Target)) return false;
                replacement = call.Target;
                return true;
            case "fromValue":
            case "fromValueUnchecked":
                if (call.Arguments.Count != 1 || call.Target is not JavaIdentifierExpression typeName) return false;
                if (!_enumTypeNames.Contains(typeName.Name)) return false;
                var arg = StripNumericCast(call.Arguments[0]);
                replacement = new JavaRawExpression($"({arg.ToInlineString()}) as {typeName.Name}");
                return true;
            default:
                return false;
        }
    }

    private bool IsEnumTypedExpression(JavaExpression expr)
    {
        switch (expr)
        {
            case JavaIdentifierExpression ident:
                return IsEnumTypedIdentifier(ident.Name);
            case JavaThisExpression thisExpr:
                return !thisExpr.IsSuper && _classStack.Count > 0
                    && _enumTypeNames.Contains(_classStack.Peek().ClassName);
            case JavaMemberAccessExpression access:
                // E.A 静态成员访问：目标为枚举类型名 → 成员即枚举值
                return access.Target is JavaIdentifierExpression target
                    && _enumTypeNames.Contains(target.Name);
            default:
                return false;
        }
    }

    private bool IsEnumTypedIdentifier(string name)
    {
        if (_methodStack.Count > 0 && _methodStack.Peek().VariableTypes.TryGetValue(name, out var type))
        {
            return _enumTypeNames.Contains(type);
        }
        if (_classStack.Count > 0)
        {
            var context = _classStack.Peek();
            if (context.InstanceFieldTypes.TryGetValue(name, out type)) return _enumTypeNames.Contains(type);
            if (context.StaticFieldTypes.TryGetValue(name, out type)) return _enumTypeNames.Contains(type);
        }
        return false;
    }

    /// <summary>剥除 (int)/(long)/(short)/(byte) 数值转换包装（fromValue 实参由共享管线生成）。</summary>
    private static JavaExpression StripNumericCast(JavaExpression expr)
    {
        if (expr is JavaCastExpression cast && cast.Type is "int" or "long" or "short" or "byte")
        {
            return cast.Expression;
        }
        return expr;
    }

    // ─── raw 文本调用点改写 ───────────────────────────────────────────

    private string RewriteEnumCalls(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        var pendingTargetStart = -1;
        var pendingTargetName = string.Empty;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(text, i);
                sb.Append(text, start, i - start);
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                var start = i;
                i = SkipComment(text, i);
                sb.Append(text, start, i - start);
                continue;
            }
            if (IsIdentifierStart(ch) && AtIdentifierBoundaryBefore(text, i))
            {
                var nameEnd = i + 1;
                while (nameEnd < text.Length && IsIdentifierChar(text[nameEnd])) nameEnd++;
                var name = text[i..nameEnd];
                var precededByDot = i > 0 && text[i - 1] == '.';
                if (nameEnd < text.Length && text[nameEnd] == '(')
                {
                    var close = FindMatchingParen(text, nameEnd);
                    if (close < 0)
                    {
                        sb.Append(ch);
                        i++;
                        continue;
                    }
                    var argText = text[(nameEnd + 1)..close];
                    if (precededByDot && (name == "getValue" || name == "ordinal")
                        && argText.Trim().Length == 0 && IsEnumRawTarget(text, i - 1))
                    {
                        // target.getValue()/target.ordinal() → target：目标文本已累积在 sb，
                        // 仅跳过调用段并回退末尾 '.'（数字枚举即 number，ordinal 仅对隐式值
                        // 枚举与值一致）；标准格式化下 '.' 紧邻方法名，回退一个字符即达目标末尾
                        if (sb.Length > 0 && sb[^1] == '.')
                        {
                            sb.Length--;
                        }
                        i = close + 1;
                        continue;
                    }
                    if (precededByDot && (name == "fromValue" || name == "fromValueUnchecked")
                        && pendingTargetStart >= 0 && _enumTypeNames.Contains(pendingTargetName))
                    {
                        // E.fromValue((int)(v)) → (v) as E：剥除数值转换包装，回滚 sb 到目标起点
                        var inner = StripRawNumericCast(argText.Trim());
                        sb.Length = pendingTargetStart;
                        sb.Append($"({inner}) as {pendingTargetName}");
                        i = close + 1;
                        continue;
                    }
                    if (precededByDot && name == "getName" && pendingTargetName == "Enum")
                    {
                        // System.Enum.GetName(typeof(E), v) → Enum.getName：无 TS 对应
                        WarnOnce("Enum.getName is not supported by the TypeScript target yet (M4 compat stub): output may not compile");
                    }
                    if (precededByDot && name == "toString" && IsEnumRawTarget(text, i - 1))
                    {
                        // 数字枚举 .toString() 返回数字字符串而非成员名（C# 语义差异）
                        WarnOnce("enum ToString() differs in TypeScript (returns numeric string, not member name); CsEnum.getName compat stub (M4) required");
                    }
                }
                if (name == "EnumHelper")
                {
                    // System.Enum.Parse/GetValues 等 → EnumHelper.*：无 TS 对应
                    WarnOnce("Enum.Parse/EnumHelper API is not supported by the TypeScript target yet (M4 compat stub): output may not compile");
                }
                if (nameEnd < text.Length && text[nameEnd] == '.')
                {
                    pendingTargetStart = sb.Length;
                    pendingTargetName = name;
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析 raw 文本中 '.' 前的目标是否枚举类型：单标识符（参数/局部变量/字段）→ 查类型表；
    /// this.标识符 → 当前类字段；枚举类型名.成员 → 枚举成员（静态访问）。
    /// 其余形态（obj.字段 等）不可解析 → 保守 false。
    /// </summary>
    private bool IsEnumRawTarget(string text, int dotIndex)
    {
        var pos = dotIndex - 1;
        if (pos < 0 || !IsIdentifierChar(text[pos])) return false;
        var end = pos + 1;
        while (pos >= 0 && IsIdentifierChar(text[pos])) pos--;
        var ident = text[(pos + 1)..end];
        if (pos >= 0 && text[pos] == '.')
        {
            var ownerEnd = pos;
            var ownerPos = pos - 1;
            if (ownerPos < 0 || !IsIdentifierChar(text[ownerPos])) return false;
            while (ownerPos >= 0 && IsIdentifierChar(text[ownerPos])) ownerPos--;
            var owner = text[(ownerPos + 1)..ownerEnd];
            if (owner == "this")
            {
                return IsEnumTypedIdentifier(ident);
            }
            return _enumTypeNames.Contains(owner);
        }
        return IsEnumTypedIdentifier(ident);
    }

    /// <summary>剥除 fromValue 实参的 (int)(expr)/(long)(expr) 包装（共享管线生成形态）。</summary>
    private static string StripRawNumericCast(string argText)
    {
        foreach (var type in new[] { "int", "long", "short", "byte" })
        {
            var prefix = "(" + type + ")(";
            if (argText.StartsWith(prefix, StringComparison.Ordinal)
                && argText.EndsWith(")", StringComparison.Ordinal))
            {
                return argText[prefix.Length..^1];
            }
        }
        return argText;
    }

    private void WarnOnce(string message)
    {
        if (_context == null || !_emittedDiagnostics.Add(message)) return;
        _context.Diagnostics.Warning(message);
    }

    // ─── 文本扫描辅助（与 TsPropertyReconstructionRewriter 同构） ───────

    private static MethodContext BuildMethodContext(JavaMethodDeclaration method)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in method.Parameters)
        {
            variables[parameter.Name] = parameter.Type;
        }
        if (method.StructuredBody != null)
        {
            CollectStructuredLocals(method.StructuredBody.Statements, variables);
        }
        return new MethodContext { VariableTypes = variables };
    }

    private static void CollectStructuredLocals(
        IReadOnlyList<JavaStatement> statements, Dictionary<string, string> variables)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case JavaVariableDeclarationStatement declaration:
                    variables[declaration.Name] = declaration.Type;
                    break;
                case JavaForEachStatement forEach:
                    if (!string.IsNullOrEmpty(forEach.VariableName))
                        variables[forEach.VariableName] = forEach.VariableType;
                    CollectStructuredLocalsFromStatement(forEach.Body, variables);
                    break;
                case JavaBlockStatement block:
                    CollectStructuredLocals(block.Statements, variables);
                    break;
                case JavaIfStatement ifStatement:
                    CollectStructuredLocalsFromStatement(ifStatement.ThenBody, variables);
                    if (ifStatement.ElseBody != null)
                        CollectStructuredLocalsFromStatement(ifStatement.ElseBody, variables);
                    break;
                case JavaWhileStatement whileStatement:
                    CollectStructuredLocalsFromStatement(whileStatement.Body, variables);
                    break;
                case JavaDoWhileStatement doWhile:
                    CollectStructuredLocalsFromStatement(doWhile.Body, variables);
                    break;
                case JavaForStatement forStatement:
                    CollectForInitDeclarations(forStatement.Initializer, variables);
                    CollectStructuredLocalsFromStatement(forStatement.Body, variables);
                    break;
                case JavaTryCatchStatement tryCatch:
                    CollectStructuredLocals(tryCatch.TryBody.Statements, variables);
                    foreach (var catchClause in tryCatch.CatchClauses)
                    {
                        if (!string.IsNullOrEmpty(catchClause.VariableName))
                            variables[catchClause.VariableName] = catchClause.ExceptionType;
                        CollectStructuredLocals(catchClause.Body.Statements, variables);
                    }
                    if (tryCatch.FinallyBody != null)
                        CollectStructuredLocals(tryCatch.FinallyBody.Statements, variables);
                    break;
                case JavaSwitchStatement switchStatement:
                    foreach (var section in switchStatement.Sections)
                    {
                        CollectStructuredLocals(section.Statements, variables);
                    }
                    break;
            }
        }
    }

    private static void CollectStructuredLocalsFromStatement(
        JavaStatement statement, Dictionary<string, string> variables)
    {
        switch (statement)
        {
            case JavaBlockStatement block:
                CollectStructuredLocals(block.Statements, variables);
                break;
            case JavaVariableDeclarationStatement declaration:
                variables[declaration.Name] = declaration.Type;
                break;
            case JavaForEachStatement forEach:
                if (!string.IsNullOrEmpty(forEach.VariableName))
                    variables[forEach.VariableName] = forEach.VariableType;
                CollectStructuredLocalsFromStatement(forEach.Body, variables);
                break;
            case JavaIfStatement ifStatement:
                CollectStructuredLocalsFromStatement(ifStatement.ThenBody, variables);
                if (ifStatement.ElseBody != null)
                    CollectStructuredLocalsFromStatement(ifStatement.ElseBody, variables);
                break;
            case JavaWhileStatement whileStatement:
                CollectStructuredLocalsFromStatement(whileStatement.Body, variables);
                break;
            case JavaDoWhileStatement doWhile:
                CollectStructuredLocalsFromStatement(doWhile.Body, variables);
                break;
            case JavaForStatement forStatement:
                CollectStructuredLocalsFromStatement(forStatement.Body, variables);
                break;
            case JavaTryCatchStatement tryCatch:
                CollectStructuredLocals(tryCatch.TryBody.Statements, variables);
                foreach (var catchClause in tryCatch.CatchClauses)
                {
                    if (!string.IsNullOrEmpty(catchClause.VariableName))
                        variables[catchClause.VariableName] = catchClause.ExceptionType;
                    CollectStructuredLocals(catchClause.Body.Statements, variables);
                }
                if (tryCatch.FinallyBody != null)
                    CollectStructuredLocals(tryCatch.FinallyBody.Statements, variables);
                break;
            case JavaSwitchStatement switchStatement:
                foreach (var section in switchStatement.Sections)
                {
                    CollectStructuredLocals(section.Statements, variables);
                }
                break;
        }
    }

    private static void CollectForInitDeclarations(string? initializer, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(initializer)) return;
        var declaredType = false;
        foreach (var segment in SplitTopLevelByComma(initializer))
        {
            var name = TryParseDeclarationName(segment, out var hasType);
            if (name != null && (hasType || declaredType)) variables[name] = "var";
            declaredType |= hasType;
        }
    }

    private static IEnumerable<string> SplitTopLevelByComma(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            switch (ch)
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
                    if (depth > 0) depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return text[start..];
    }

    private static string? TryParseDeclarationName(string segment, out bool hasType)
    {
        hasType = false;
        var text = segment.Trim();
        if (text.Length == 0) return null;
        var i = 0;
        var first = ReadIdentifier(text, ref i);
        if (first == null) return null;
        SkipWhitespace(text, ref i);
        while (i < text.Length && (text[i] == '.' || text[i] == '<' || text[i] == '['))
        {
            if (text[i] == '.')
            {
                i++;
                ReadIdentifier(text, ref i);
            }
            else if (text[i] == '<')
            {
                i = SkipBalancedForward(text, i, '<', '>');
            }
            else
            {
                i = SkipBalancedForward(text, i, '[', ']');
            }
            SkipWhitespace(text, ref i);
        }
        var name = ReadIdentifier(text, ref i);
        if (name != null)
        {
            hasType = true;
            return name;
        }
        return first;
    }

    private static string? ReadIdentifier(string text, ref int i)
    {
        SkipWhitespace(text, ref i);
        if (i >= text.Length || !IsIdentifierStart(text[i])) return null;
        var start = i;
        i++;
        while (i < text.Length && IsIdentifierChar(text[i])) i++;
        return text[start..i];
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
    }

    private static int SkipBalancedForward(string text, int i, char open, char close)
    {
        var depth = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i);
                continue;
            }
            if (ch == open) depth++;
            else if (ch == close)
            {
                depth--;
                if (depth == 0) return i + 1;
            }
            i++;
        }
        return text.Length;
    }

    private static bool IsIdentifierStart(char ch)
        => ch == '_' || char.IsLetter(ch);

    private static bool IsIdentifierChar(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);

    private static bool AtIdentifierBoundaryBefore(string text, int index)
    {
        if (index == 0) return true;
        var prev = text[index - 1];
        return !IsIdentifierChar(prev);
    }

    private static int SkipQuotedLiteral(string text, int start)
    {
        var quote = text[start];
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }
            if (text[i] == quote) return i + 1;
            i++;
        }
        return text.Length;
    }

    private static int SkipComment(string text, int start)
    {
        if (start + 1 < text.Length && text[start + 1] == '/')
        {
            var end = text.IndexOf('\n', start + 2);
            return end < 0 ? text.Length : end + 1;
        }
        var close = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + 2;
    }

    private static int FindMatchingParen(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}