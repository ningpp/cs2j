using System.Text;
using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// async/await TS 化 rewriter（Task 27，M3，spec §1.4.5）。C#→Java 共享管线把
/// Task/Task&lt;T&gt; 映射为 CompletableFuture 并降级 await → `.join()`、返回 →
/// `CompletableFuture.completedFuture(x)`，TS 目标统一为原生 Promise + async/await：
/// ① async 方法（MethodTransformer 落 IsAsync 标记）→ TsMemberWriter 发射 `async ` 前缀，
///    返回类型经 TsMemberMappingTable 映射 CompletableFuture&lt;X&gt; → Promise&lt;X&gt;；
/// ② async 方法体（Body 字符串 / raw 语句 / 结构化语句表达式三路）：
///    `X.join()` → `await X`（跳过 String.join / StringHelper.join / CsString.join 字符串联接；
///    嵌套 await 如 `F(G().join()).join()` 按从内到外递归改写）、
///    `return CompletableFuture.completedFuture(X);` → `return X;`（null → `return;`）；
/// ③ 全方法体（含非 async 的 Task 返回方法与字段初始化器）：
///    `CompletableFuture.completedFuture(X)` → `Promise.resolve(X)`（null → `Promise.resolve()`）、
///    `CompletableFuture.delayedExecutor(X)`（Task.Delay）→
///    `new Promise&lt;void&gt;(resolve => setTimeout(resolve, X))`、
///    `CompletableFuture.yield()`（Task.Yield）→ `Promise.resolve()`、
///    `CompletableFuture.allOf(...)`（Task.WhenAll）→ `Promise.all([...])`、
///    `CompletableFuture.runAsync/supplyAsync(...)`（Task.Run）→ `Promise.resolve((fn)())`
///    同步降级 + 诊断（保持 Task→Promise 类型契约）。
/// ④ 表达式体 async：MethodTransformer 已包 `CompletableFuture.completedFuture(X)`，
///    解包为裸表达式（null → 空体）后随 IsBodyExpression 发射 `return X;`。
/// 已知限制（登记计划）：非 async 方法的 `.join()`（Task.Result 阻塞语义）无 TS
/// 对应不改写；async lambda（LambdaTransformer 的 runAsync/supplyAsync）随 ③ 同步降级。
/// </summary>
public sealed class TsAsyncRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext? _context;
    private readonly HashSet<string> _diagnosed = new(StringComparer.Ordinal);
    private readonly Stack<bool> _asyncStack = new();

    private static readonly Regex ReturnCompletedFuturePattern = new(
        @"return\s+CompletableFuture\.completedFuture\((?<inner>.*?)\);",
        RegexOptions.Compiled);

    public TsAsyncRewriter(ConversionContext? context = null)
    {
        _context = context;
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        _asyncStack.Push(node.IsAsync);
        try
        {
            if (!string.IsNullOrEmpty(node.Body))
            {
                // ④ 表达式体 async 先解包 completedFuture（null → 空体改走 raw 分支）
                if (node.IsBodyExpression && node.IsAsync)
                {
                    node.Body = UnwrapExpressionBodyAsync(node.Body, out var keepExpressionBody);
                    node.IsBodyExpression = keepExpressionBody;
                }
                node.Body = RewriteBody(node.Body, node.IsAsync);
            }
            return base.VisitMethodDeclaration(node);
        }
        finally
        {
            _asyncStack.Pop();
        }
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        node.Code = RewriteBody(node.Code, _asyncStack.Count > 0 && _asyncStack.Peek());
        return base.VisitRawStatement(node);
    }

    /// <summary>
    /// 结构化语句中的表达式（变量初始化器/return/if 条件/集合等）：join→await 与
    /// CompletableFuture.* 形态在节点层改写；JavaRawExpression 直通文本走字符串重写。
    /// </summary>
    public override JavaExpression VisitExpression(JavaExpression node)
    {
        var isAsync = _asyncStack.Count > 0 && _asyncStack.Peek();
        if (node is JavaMethodCallExpression call)
        {
            // ② async：X.join() → await (X)（跳过 String/StringHelper 字符串联接；
            // 括号包裹保证后续成员访问/调用优先级正确，如 (await f()).toString()）
            if (isAsync && call.MethodName == "join" && call.Target != null)
            {
                var targetName = (call.Target as JavaIdentifierExpression)?.Name;
                if (targetName is not ("String" or "java.lang.String" or "StringHelper" or "CsString"))
                {
                    var target = VisitExpression(call.Target);
                    return new JavaRawExpression($"await ({target.ToInlineString()})");
                }
            }
            // ③ CompletableFuture.* 静态调用形态（Task 27，spec §1.4.5）
            if (call.Target is JavaIdentifierExpression { Name: "CompletableFuture" })
            {
                switch (call.MethodName)
                {
                    case "completedFuture":
                        var innerExpr = call.Arguments.Count == 1
                            ? VisitExpression(call.Arguments[0]).ToInlineString()
                            : "null";
                        var innerValue = innerExpr.Trim();
                        return new JavaRawExpression(isAsync
                            ? (innerValue == "null" ? "undefined" : innerValue)
                            : (innerValue == "null" ? "Promise.resolve()" : $"Promise.resolve({innerValue})"));
                    case "delayedExecutor":
                        var delay = call.Arguments.Count == 1
                            ? VisitExpression(call.Arguments[0]).ToInlineString()
                            : "0";
                        return new JavaRawExpression($"new Promise<void>(resolve => setTimeout(resolve, {delay.Trim()}))");
                    case "allOf":
                        var allArgs = string.Join(", ", call.Arguments.Select(a => VisitExpression(a).ToInlineString()));
                        return new JavaRawExpression($"Promise.all([{allArgs}])");
                    case "runAsync":
                    case "supplyAsync":
                        WarnTaskRun();
                        var fn = call.Arguments.Count == 1
                            ? VisitExpression(call.Arguments[0]).ToInlineString()
                            : "() => undefined";
                        // 降级为同步调用但保持 Task→Promise 类型契约（非 async 上下文
                        // 如 `return Task.Run(...)` 也不破坏 Promise<X> 返回类型）
                        return new JavaRawExpression($"Promise.resolve(({fn.Trim()})())");
                    case "yield":
                        return new JavaRawExpression("Promise.resolve()");
                }
            }
        }
        return base.VisitExpression(node);
    }

    /// <summary>async 返回 completedFuture(null)（Task 返回）：`return X;` → `return;`。</summary>
    public override JavaReturnStatement VisitReturnStatement(JavaReturnStatement node)
    {
        if (node.Expression != null && _asyncStack.Count > 0 && _asyncStack.Peek()
            && node.Expression is JavaMethodCallExpression { MethodName: "completedFuture" } call
            && call.Target is JavaIdentifierExpression { Name: "CompletableFuture" }
            && (call.Arguments.Count == 0
                || (call.Arguments.Count == 1 && call.Arguments[0].ToInlineString().Trim() == "null")))
        {
            node.Expression = null;
            return node;
        }
        if (node.Expression != null)
        {
            node.Expression = VisitExpression(node.Expression);
        }
        return node;
    }

    /// <summary>直通文本表达式（JavaRawExpression.Code）同样过字符串重写。</summary>
    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        if (!string.IsNullOrEmpty(node.Code))
        {
            node.Code = RewriteBody(node.Code, _asyncStack.Count > 0 && _asyncStack.Peek());
        }
        return base.VisitRawExpression(node);
    }

    /// <summary>字段初始化器（如 static Task&lt;int&gt; T = Task.FromResult(1)）过 ③ 重写。</summary>
    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Initializer))
        {
            node.Initializer = RewriteCompletableFuture(node.Initializer);
        }
        return base.VisitFieldDeclaration(node);
    }

    /// <summary>表达式体 async 解包：`CompletableFuture.completedFuture(X)` → X；
    /// X 为 null → 空体（keepExpressionBody=false，raw 分支发射空块）。</summary>
    private static string? UnwrapExpressionBodyAsync(string? body, out bool keepExpressionBody)
    {
        keepExpressionBody = true;
        const string prefix = "CompletableFuture.completedFuture(";
        if (body != null && body.StartsWith(prefix, StringComparison.Ordinal) && body.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = body[prefix.Length..^1].Trim();
            if (inner == "null")
            {
                keepExpressionBody = false;
                return string.Empty;
            }
            return inner;
        }
        return body;
    }

    private string RewriteBody(string text, bool isAsync)
    {
        // ② async：X.join() → await X（先剥外层 join，再做内层 CompletableFuture 重写）
        if (isAsync)
        {
            text = RewriteJoinToAwait(text);
            text = ReturnCompletedFuturePattern.Replace(text, m =>
            {
                var inner = m.Groups["inner"].Value.Trim();
                return inner == "null" ? "return;" : $"return {inner};";
            });
        }
        // ③ 全局 CompletableFuture 形态（跳过字符串/字符字面量）
        text = RewriteCompletableFuture(text);
        return text;
    }

    /// <summary>
    /// `X.join()` → `await X`；跳过 String.join / java.lang.String.join / StringHelper.join
    /// （字符串联接）。左→右扫描，命中时接收者文本已逐字符复制进 sb：回退前缀后以
    /// `await X` 替换整个 X.join(...)；接收者内可能含嵌套 join（await F(await G())），
    /// 对接收者递归改写，外层 join 由后续扫描命中。
    /// </summary>
    private static string RewriteJoinToAwait(string text)
    {
        const string needle = ".join(";
        var sb = new StringBuilder(text.Length);
        var copyFrom = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"' || text[i] == '\'')
            {
                i = SkipQuotedLiteral(text, i);
                continue;
            }
            if (text.AsSpan(i).StartsWith(needle, StringComparison.Ordinal))
            {
                var exprEnd = i - 1;
                while (exprEnd >= 0 && char.IsWhiteSpace(text[exprEnd]))
                {
                    exprEnd--;
                }
                var exprStart = TsGenericErasureIrRewriter.BacktrackExpressionStart(text, exprEnd);
                if (exprStart >= copyFrom)
                {
                    var receiver = text[exprStart..i].Trim();
                    if (receiver is not ("String" or "java.lang.String" or "StringHelper" or "CsString"))
                    {
                        var close = FindMatchingParen(text, i + needle.Length - 1);
                        if (close > i + needle.Length - 1)
                        {
                            // 接收者已逐字符复制进 sb：回退到 exprStart 再写 `await X`；
                            // 接收者内的嵌套 join 递归改写（从内到外）
                            var rewrittenReceiver = RewriteJoinToAwait(receiver);
                            sb.Append(text, copyFrom, exprStart - copyFrom)
                              .Append("await ").Append(rewrittenReceiver);
                            copyFrom = close + 1;
                            i = close + 1;
                            continue;
                        }
                    }
                }
            }
            i++;
        }
        sb.Append(text, copyFrom, text.Length - copyFrom);
        return sb.ToString();
    }

    /// <summary>CompletableFuture.* 静态形态全局重写（跳过字符串/字符字面量；
    /// 实参内嵌套的 CompletableFuture.* 递归改写）。</summary>
    private string RewriteCompletableFuture(string text)
    {
        const string completedNeedle = "CompletableFuture.completedFuture(";
        const string delayedNeedle = "CompletableFuture.delayedExecutor(";
        const string allOfNeedle = "CompletableFuture.allOf(";
        const string runAsyncNeedle = "CompletableFuture.runAsync(";
        const string supplyAsyncNeedle = "CompletableFuture.supplyAsync(";
        const string yieldNeedle = "CompletableFuture.yield()";

        var sb = new StringBuilder(text.Length);
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
            if (AtIdentifierBoundary(text, i)
                && text.AsSpan(i).StartsWith(completedNeedle, StringComparison.Ordinal))
            {
                if (TryReadCallArgs(text, i, i + completedNeedle.Length - 1, out var inner, out var consumed))
                {
                    var innerRewritten = RewriteCfArgs(inner);
                    sb.Append(innerRewritten.Trim() == "null"
                        ? "Promise.resolve()"
                        : $"Promise.resolve({innerRewritten.Trim()})");
                    i += consumed;
                    continue;
                }
            }
            else if (AtIdentifierBoundary(text, i)
                && text.AsSpan(i).StartsWith(delayedNeedle, StringComparison.Ordinal))
            {
                if (TryReadCallArgs(text, i, i + delayedNeedle.Length - 1, out var delayArgs, out var consumed2))
                {
                    sb.Append($"new Promise<void>(resolve => setTimeout(resolve, {RewriteCfArgs(delayArgs).Trim()}))");
                    i += consumed2;
                    continue;
                }
            }
            else if (AtIdentifierBoundary(text, i)
                && text.AsSpan(i).StartsWith(allOfNeedle, StringComparison.Ordinal))
            {
                if (TryReadCallArgs(text, i, i + allOfNeedle.Length - 1, out var allArgs, out var consumed3))
                {
                    sb.Append($"Promise.all([{RewriteCfArgs(allArgs).Trim()}])");
                    i += consumed3;
                    continue;
                }
            }
            else if (AtIdentifierBoundary(text, i)
                && text.AsSpan(i).StartsWith(runAsyncNeedle, StringComparison.Ordinal))
            {
                if (TryReadCallArgs(text, i, i + runAsyncNeedle.Length - 1, out var fn, out var consumed4))
                {
                    WarnTaskRun();
                    sb.Append($"Promise.resolve(({RewriteCfArgs(fn).Trim()})())");
                    i += consumed4;
                    continue;
                }
            }
            else if (AtIdentifierBoundary(text, i)
                && text.AsSpan(i).StartsWith(supplyAsyncNeedle, StringComparison.Ordinal))
            {
                if (TryReadCallArgs(text, i, i + supplyAsyncNeedle.Length - 1, out var fn2, out var consumed5))
                {
                    WarnTaskRun();
                    sb.Append($"Promise.resolve(({RewriteCfArgs(fn2).Trim()})())");
                    i += consumed5;
                    continue;
                }
            }
            else if (AtIdentifierBoundary(text, i)
                && text.AsSpan(i).StartsWith(yieldNeedle, StringComparison.Ordinal))
            {
                sb.Append("Promise.resolve()");
                i += yieldNeedle.Length;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>CompletableFuture.* 实参串递归改写（实参内可嵌套相同形态，如
    /// `completedFuture(supplyAsync(...))`；仅当含 CompletableFuture. 前缀时递归）。</summary>
    private string RewriteCfArgs(string args)
    {
        return args.Contains("CompletableFuture.", StringComparison.Ordinal)
            ? RewriteCompletableFuture(args)
            : args;
    }

    /// <summary>读取自 start 起的 CompletableFuture.* 调用实参串与消耗长度
    /// （含 needle 前缀与括号）；open 为 '(' 位置。</summary>
    private static bool TryReadCallArgs(string text, int start, int open, out string args, out int consumed)
    {
        args = string.Empty;
        consumed = 0;
        var close = FindMatchingParen(text, open);
        if (close <= open)
        {
            return false;
        }
        args = text[(open + 1)..close];
        consumed = close + 1 - start;
        return true;
    }

    private void WarnTaskRun()
    {
        const string message = "cs2j: Task.Run(...) has no TS counterpart (single-threaded model); degraded to synchronous call (spec §1.4.5)";
        if (_diagnosed.Add(message))
        {
            _context?.Diagnostics.Warning(message);
        }
    }

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
