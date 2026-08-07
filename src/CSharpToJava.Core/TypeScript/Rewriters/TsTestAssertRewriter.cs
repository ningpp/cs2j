using System.Collections.Generic;
using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 测试类 IR 改写器（Task 39，spec §6.1）：把 xUnit/MSTest Assert 调用点改写为
/// vitest expect 链（Assert.Equal(a,b) → expect(b).toEqual(a)、True → toBeTruthy、
/// Throws → toThrow、…），并把测试类成员内的 this.X 接收者剥为裸标识符
/// （vitest describe 作用域字段是 let 变量，无 this）。
/// 与 TsStringCallRewriter 同构：结构化节点 + raw 文本双覆盖（前端把
/// Assert.* 调用经 TransformMemberInvocation 产出为 JavaRawExpression/Body
/// 字符串，结构访问不可达）。仅作用于 TsTestCollectorRewriter 登记的测试类。
/// </summary>
public sealed class TsTestAssertRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext _context;
    private string? _package;
    private int _testClassDepth;

    public TsTestAssertRewriter(ConversionContext context)
    {
        _context = context;
    }

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _package = node.Package;
        return base.VisitCompilationUnit(node);
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        if (IsTestClass(node))
        {
            _testClassDepth++;
            try
            {
                return (JavaClassDeclaration)base.VisitClassDeclaration(node);
            }
            finally
            {
                _testClassDepth--;
            }
        }
        return (JavaClassDeclaration)base.VisitClassDeclaration(node);
    }

    private bool IsTestClass(JavaClassDeclaration node)
    {
        if (_context.TsTestClasses == null)
        {
            return false;
        }
        return _context.TsTestClasses.ContainsKey(Key(_package, node.Name))
            || _context.TsTestClasses.ContainsKey(node.Name);
    }

    // ─── 结构化表达式（Assert.* 通常不可达，保留兜底） ───

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        if (_testClassDepth <= 0)
        {
            return base.VisitExpression(node);
        }

        // 测试类成员内 this.x → x（describe 作用域 let 变量；super.x 保留）。
        // 在递归前剥离：this.x 的 Target 不再下钻（this 无子节点）。
        if (node is JavaMemberAccessExpression member
            && member.Target is JavaThisExpression { IsSuper: false })
        {
            return new JavaIdentifierExpression(member.MemberName);
        }

        var visited = base.VisitExpression(node);
        if (visited is JavaMethodCallExpression call)
        {
            return TryRewriteAssert(call) ?? call;
        }
        return visited;
    }

    // ─── raw 文本（前端 Assert.* 走 TransformMemberInvocation 字符串路径） ───

    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        node = (JavaRawExpression)base.VisitRawExpression(node);
        if (_testClassDepth > 0 && !string.IsNullOrEmpty(node.Code))
        {
            node.Code = RewriteRawTestCode(node.Code);
        }
        return node;
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (_testClassDepth > 0 && !string.IsNullOrEmpty(node.Code))
        {
            node.Code = RewriteRawTestCode(node.Code);
        }
        return node;
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        node = (JavaMethodDeclaration)base.VisitMethodDeclaration(node);
        if (_testClassDepth > 0 && !string.IsNullOrEmpty(node.Body))
        {
            node.Body = RewriteRawTestCode(node.Body);
        }
        return node;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        node = (JavaConstructorDeclaration)base.VisitConstructorDeclaration(node);
        if (_testClassDepth > 0 && !string.IsNullOrEmpty(node.Body))
        {
            node.Body = RewriteRawTestCode(node.Body);
        }
        return node;
    }

    // ─── Assert → expect 映射（结构化） ───

    private JavaExpression? TryRewriteAssert(JavaMethodCallExpression call)
    {
        var assertKind = GetAssertKind(call);
        if (assertKind == null)
        {
            return null;
        }
        var args = call.Arguments;
        switch (assertKind)
        {
            case "equal":
            case "areEqual":
                return Expect(Arg(args, 1), "toEqual", Arg(args, 0));
            case "notEqual":
            case "areNotEqual":
                return ExpectNot(Arg(args, 1), "toEqual", Arg(args, 0));
            case "true_":
            case "trueValue":
            case "isTrue":
                return Expect(Arg(args, 0), "toBeTruthy");
            case "false_":
            case "falseValue":
            case "isFalse":
                return Expect(Arg(args, 0), "toBeFalsy");
            case "null_":
            case "nullValue":
            case "isNull":
                return Expect(Arg(args, 0), "toBeNull");
            case "notNull":
            case "isNotNull":
                return ExpectNot(Arg(args, 0), "toBeNull");
            case "throws_":
            case "throwsValue":
            case "throwsAny":
            case "throwsAsync":
                return Expect(Arg(args, args.Count - 1), "toThrow");
            case "contains":
                return Expect(Arg(args, 1), "toContain", Arg(args, 0));
            case "doesNotContain":
                return ExpectNot(Arg(args, 1), "toContain", Arg(args, 0));
            case "empty":
                return Expect(Arg(args, 0), "toHaveLength", new JavaLiteralExpression("0"));
            case "notEmpty":
                return ExpectNot(Arg(args, 0), "toHaveLength", new JavaLiteralExpression("0"));
            case "single":
                return Expect(Arg(args, 0), "toHaveLength", new JavaLiteralExpression("1"));
            case "same":
            case "areSame":
                return Expect(Arg(args, 1), "toBe", Arg(args, 0));
            case "notSame":
            case "areNotSame":
                return ExpectNot(Arg(args, 1), "toBe", Arg(args, 0));
            case "startsWith":
            case "endsWith":
                // vitest/Chai 无 toStartWith/toEndWith：actual.startsWith(expected) 布尔断言
                var stringCheck = new JavaMethodCallExpression
                {
                    Target = new JavaMemberAccessExpression
                    {
                        Target = Arg(args, 1),
                        MemberName = assertKind,
                    },
                    MethodName = assertKind,
                };
                stringCheck.Arguments.Add(Arg(args, 0));
                return Expect(stringCheck, "toBeTruthy");
            case "fail":
                var unreachable = new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression("expect"),
                    MethodName = "unreachable",
                };
                if (args.Count > 0)
                {
                    unreachable.Arguments.Add(args[0]);
                }
                return unreachable;
            default:
                return null;
        }
    }

    private static string? GetAssertKind(JavaMethodCallExpression call)
    {
        if (call.Target is JavaIdentifierExpression { Name: "Assert" or "CollectionAssert" })
        {
            return call.MethodName;
        }
        if (call.Target is JavaMemberAccessExpression member
            && member.MemberName is "Assert" or "CollectionAssert")
        {
            return call.MethodName;
        }
        return null;
    }

    private static JavaExpression Arg(IReadOnlyList<JavaExpression> args, int index) =>
        index >= 0 && index < args.Count ? args[index] : new JavaLiteralExpression("undefined");

    private static JavaExpression Expect(JavaExpression value, string matcher, JavaExpression? matcherArg = null)
    {
        var expectCall = new JavaMethodCallExpression
        {
            MethodName = "expect",
        };
        expectCall.Arguments.Add(value);
        var result = new JavaMethodCallExpression
        {
            Target = expectCall,
            MethodName = matcher,
        };
        if (matcherArg != null)
        {
            result.Arguments.Add(matcherArg);
        }
        return result;
    }

    private static JavaExpression ExpectNot(JavaExpression value, string matcher, JavaExpression? matcherArg = null)
    {
        var expectCall = new JavaMethodCallExpression
        {
            MethodName = "expect",
        };
        expectCall.Arguments.Add(value);
        var result = new JavaMethodCallExpression
        {
            Target = new JavaMemberAccessExpression
            {
                Target = expectCall,
                MemberName = "not",
            },
            MethodName = matcher,
        };
        if (matcherArg != null)
        {
            result.Arguments.Add(matcherArg);
        }
        return result;
    }

    // ─── raw 文本扫描：Assert.* 改写 + this. 剥离（字符串/注释外） ───

    /// <summary>扫描 raw 文本：识别 Assert./CollectionAssert. 调用并改写；剥离 this. 前缀。</summary>
    private static string RewriteRawTestCode(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            // 字符串/字符字面量原样跳过（内容可能含 "Assert."/"this." 字样）
            if (c == '"' || c == '\'')
            {
                var end = SkipString(code, i);
                sb.Append(code, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                var end = code.IndexOf('\n', i);
                if (end < 0)
                {
                    sb.Append(code, i, code.Length - i);
                    break;
                }
                sb.Append(code, i, end - i);
                i = end;
                continue;
            }
            if (TryRewriteAssertAt(code, i, out var rewritten, out var consumed))
            {
                sb.Append(rewritten);
                i += consumed;
                continue;
            }
            if (IsThisMemberPrefix(code, i))
            {
                i += "this.".Length;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsThisMemberPrefix(string code, int i)
    {
        if (i + "this.".Length > code.Length
            || !code.AsSpan(i).StartsWith("this.", System.StringComparison.Ordinal))
        {
            return false;
        }
        // 前一个字符必须是边界（标识符/数字/下划线前不加剥离，避免 "mythis.x" 误伤）
        if (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_'))
        {
            return false;
        }
        return true;
    }

    /// <summary>纯 this. 前缀剥离（字符串/注释外），用于断言改写结果的参数文本。</summary>
    private static string StripThisPrefixes(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '"' || c == '\'')
            {
                var end = SkipString(code, i);
                sb.Append(code, i, end - i);
                i = end;
                continue;
            }
            if (IsThisMemberPrefix(code, i))
            {
                i += "this.".Length;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryRewriteAssertAt(string code, int start, out string rewritten, out int consumed)
    {
        rewritten = string.Empty;
        consumed = 0;
        foreach (var prefix in new[] { "Assert.", "CollectionAssert." })
        {
            if (start + prefix.Length > code.Length
                || !code.AsSpan(start).StartsWith(prefix, System.StringComparison.Ordinal))
            {
                continue;
            }
            // 前置边界：确保是独立标识符（如 "MyAssert." 不误伤）
            if (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
            {
                continue;
            }
            var nameStart = start + prefix.Length;
            var nameEnd = nameStart;
            while (nameEnd < code.Length && (char.IsLetterOrDigit(code[nameEnd]) || code[nameEnd] == '_'))
            {
                nameEnd++;
            }
            if (nameEnd == nameStart || nameEnd >= code.Length || code[nameEnd] != '(')
            {
                continue;
            }
            var close = FindMatchingParen(code, nameEnd);
            if (close < 0)
            {
                continue;
            }
            var args = SplitTopLevelArgs(code, nameEnd + 1, close);
            var mapped = MapAssertToExpect(code[nameStart..nameEnd], args);
            if (mapped == null)
            {
                continue;
            }
            // 参数文本内可能含 this.x（实例调用点）——改写结果一并剥离
            rewritten = StripThisPrefixes(mapped);
            consumed = close + 1 - start;
            return true;
        }
        return false;
    }

    /// <summary>从 open 括号索引找匹配右括号（跟踪嵌套 ()/[]/{} 与字符串）。</summary>
    private static int FindMatchingParen(string code, int open)
    {
        var depth = 0;
        var i = open;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(code, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
            i++;
        }
        return -1;
    }

    private static int SkipString(string code, int quoteIndex)
    {
        var i = quoteIndex + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (code[i] == code[quoteIndex])
            {
                return i + 1;
            }
            i++;
        }
        return code.Length;
    }

    /// <summary>按顶层逗号切分参数（跟踪嵌套与字符串；lambda 体 {} 也在括号计数内）。</summary>
    private static List<string> SplitTopLevelArgs(string code, int start, int end)
    {
        var args = new List<string>();
        var depth = 0;
        var segmentStart = start;
        var i = start;
        while (i < end)
        {
            var c = code[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(code, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(code[segmentStart..i].Trim());
                segmentStart = i + 1;
            }
            i++;
        }
        args.Add(code[segmentStart..end].Trim());
        return args;
    }

    /// <summary>Assert 方法名 + 参数文本 → vitest expect 文本；不支持返回 null（保持原样）。</summary>
    private static string? MapAssertToExpect(string methodName, IReadOnlyList<string> args)
    {
        switch (methodName)
        {
            case "equal":
            case "areEqual":
                return Args2(args, (expected, actual) => $"expect({actual}).toEqual({expected})");
            case "notEqual":
            case "areNotEqual":
                return Args2(args, (expected, actual) => $"expect({actual}).not.toEqual({expected})");
            case "true_":
            case "trueValue":
            case "isTrue":
                return Arg1(args, value => $"expect({value}).toBeTruthy()");
            case "false_":
            case "falseValue":
            case "isFalse":
                return Arg1(args, value => $"expect({value}).toBeFalsy()");
            case "null_":
            case "nullValue":
            case "isNull":
                return Arg1(args, value => $"expect({value}).toBeNull()");
            case "notNull":
            case "isNotNull":
                return Arg1(args, value => $"expect({value}).not.toBeNull()");
            case "throws_":
            case "throwsValue":
            case "throwsAny":
            case "throwsAsync":
                return args.Count > 0
                    ? $"expect({args[args.Count - 1]}).toThrow()"
                    : null;
            case "contains":
                return Args2(args, (expected, collection) => $"expect({collection}).toContain({expected})");
            case "doesNotContain":
                return Args2(args, (expected, collection) => $"expect({collection}).not.toContain({expected})");
            case "empty":
                return Arg1(args, value => $"expect({value}).toHaveLength(0)");
            case "notEmpty":
                return Arg1(args, value => $"expect({value}).not.toHaveLength(0)");
            case "single":
                return Arg1(args, value => $"expect({value}).toHaveLength(1)");
            case "same":
            case "areSame":
                return Args2(args, (expected, actual) => $"expect({actual}).toBe({expected})");
            case "notSame":
            case "areNotSame":
                return Args2(args, (expected, actual) => $"expect({actual}).not.toBe({expected})");
            case "startsWith":
                // vitest/Chai 无 toStartWith/toEndWith：布尔断言（语义等价且可运行）
                return Args2(args, (expected, actual) => $"expect({actual}.startsWith({expected})).toBeTruthy()");
            case "endsWith":
                return Args2(args, (expected, actual) => $"expect({actual}.endsWith({expected})).toBeTruthy()");
            case "fail":
                return args.Count > 0 ? $"expect.unreachable({args[0]})" : "expect.unreachable()";
            default:
                return null;
        }
    }

    private static string? Arg1(IReadOnlyList<string> args, Func<string, string> build) =>
        args.Count >= 1 ? build(args[0]) : null;

    private static string? Args2(IReadOnlyList<string> args, Func<string, string, string> build) =>
        args.Count >= 2 ? build(args[0], args[1]) : null;

    private static string Key(string? ns, string className) =>
        string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";
}
