using System.Text;
using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// yield → generator 发射 rewriter（Task 27，M3，spec §1.4.4）。C#→Java 共享管线把
/// 含 yield 的方法/属性 getter 降级为“收集到列表”形态（`CSharpList&lt;T&gt; _yieldResult =
/// new CSharpList&lt;T&gt;();` + `_yieldResult.add(x);` + `return _yieldResult;`），TS 目标统一为原生
/// generator（spec：`yield return` → `function*` + `IterableIterator&lt;T&gt;`）：
/// ① 方法 → `*name(...): IterableIterator&lt;T&gt;`（IsGenerator 标记供 TsMemberWriter 发射 `*`），
///    元素类型从收集声明 `CSharpList&lt;X&gt;` 提取并经 TS 映射（Integer→number、
///    String→string 等）；
/// ② 属性 getter → `get name(): IterableIterator&lt;T&gt; { return (function* () {...})(); }`
///    （TS 不支持 generator accessor，IIFE 包裹）；
/// ③ body 文本：删除收集声明前导、`_yieldResult.add(expr);` → `yield expr;`、
///    `return _yieldResult[.iterator()];` → `return;`；随后整体过 TsRawStatementNormalizer
///    （方法 Body 字符串不经过语句发射器，经典 for 头 let 化等必须在此补齐）。
/// 已知限制（登记计划）：yield 方法体内 try/catch 组合未覆盖；
/// `IEnumerator&lt;T&gt;` 迭代器形态同样映射为 generator；非 yield 的
/// `IEnumerable&lt;T&gt;` 返回（CSharpGenericIterable.from/stream 链）归 M4 Task 36 LINQ 目标分叉。
/// </summary>
public sealed class TsYieldRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext? _context;
    private readonly IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? _accessorRoles;
    private readonly TsMemberMappingTable _mappings = new(null, null);

    private static readonly Regex YieldResultDeclPattern = new(
        @"^\s*CSharpList<(?<elem>[^>]+)>\s+_yieldResult\s*=\s*new\s+CSharpList<[^>]+>\(\);\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ReturnYieldResultPattern = new(
        @"return\s+_yieldResult(?:\.iterator\(\))?\s*;",
        RegexOptions.Compiled);

    public TsYieldRewriter(
        ConversionContext? context = null,
        IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? accessorRoles = null)
    {
        _context = context;
        _accessorRoles = accessorRoles;
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Body)
            && node.Body.Contains("_yieldResult", StringComparison.Ordinal))
        {
            RewriteYield(node);
        }
        return base.VisitMethodDeclaration(node);
    }

    private void RewriteYield(JavaMethodDeclaration method)
    {
        var elem = ExtractElementType(method.Body!);
        var elemTs = elem != null && _mappings.TryMapType(elem, out var mapped, out _)
            ? mapped
            : "unknown";
        var body = RewriteYieldBody(method.Body!);
        // 方法 Body 字符串不经过语句发射器，经典 for 头 let 化/字符字面量等在此补齐
        body = TsRawStatementNormalizer.Normalize(body, MapType, null);
        method.ReturnType = $"IterableIterator<{elemTs}>";
        if (IsAccessor(method))
        {
            // TS 不支持 generator accessor：getter 体用 IIFE generator 包裹
            method.Body = $"return (function* () {{\n{body}\n}})();";
        }
        else
        {
            method.Body = body;
            method.IsGenerator = true;
        }
    }

    private bool IsAccessor(JavaMethodDeclaration method)
        => _accessorRoles != null && _accessorRoles.ContainsKey(method);

    private static string? ExtractElementType(string body)
    {
        var m = YieldResultDeclPattern.Match(body);
        return m.Success ? m.Groups["elem"].Value : null;
    }

    private static string RewriteYieldBody(string body)
    {
        body = YieldResultDeclPattern.Replace(body, string.Empty);
        body = RewriteYieldAdd(body);
        body = ReturnYieldResultPattern.Replace(body, "return;");
        return body.TrimStart('\r', '\n');
    }

    /// <summary>`_yieldResult.add(expr);` → `yield expr;`；实参可含任意嵌套括号/字符串。</summary>
    private static string RewriteYieldAdd(string body)
    {
        const string needle = "_yieldResult.add(";
        var sb = new StringBuilder(body.Length);
        var i = 0;
        while (i < body.Length)
        {
            var ch = body[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(body, i);
                sb.Append(body, start, i - start);
                continue;
            }
            if (AtIdentifierBoundary(body, i)
                && body.AsSpan(i).StartsWith(needle, StringComparison.Ordinal))
            {
                var open = i + needle.Length - 1;
                var close = FindMatchingParen(body, open);
                if (close > open)
                {
                    var inner = body[(open + 1)..close].Trim();
                    sb.Append("yield ").Append(inner).Append(';');
                    i = close + 1;
                    if (i < body.Length && body[i] == ';')
                    {
                        i++;
                    }
                    continue;
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private string MapType(string javaType)
        => _mappings.TryMapType(javaType, out var ts, out _) ? ts : javaType;

    private static bool AtIdentifierBoundary(string text, int index)
        => index == 0 || !IsIdentifierChar(text[index - 1]);

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '$';

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
            if (text[i] == quote)
            {
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }

    private static int FindMatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }
}
