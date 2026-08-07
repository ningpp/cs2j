using System.Text;
using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// ref/out 参数 TS 化 rewriter（Task 27，M3，spec §4.3.1）。C#→Java 共享管线用
/// io.github.ningpp.compat 的 Holder 包装类表达 ref/out（参数类型、调用点临时声明
/// `IntHolder _aHolder1 = new IntHolder(a);`、读回 `a = _aHolder1.value;`），TS
/// 目标统一为兼容库 Ref&lt;T&gt; + refOf/outRef 工厂：
/// ① 参数/局部类型位置由 TsMemberMappingTable 的 Holder 分支映射（Ref&lt;number&gt; 等）；
/// ② 调用点临时声明（raw 文本）→ `const _aHolder1 = refOf(a);`（out 未播种 →
///    `outRef&lt;number&gt;()`）；
/// ③ out var 读回声明 `int r = _rHolder1.value;` → `let r: number = _rHolder1.value;`
///    （Java 类型注解局部声明非法 TS）。
/// 仅扫描 raw 文本（方法/构造器 Body 字符串、JavaRawStatement、静态初始化块），
/// 跳过字符串/字符字面量区域；结构化语句（已有 let 形态）不受影响。
/// 登记 compat 符号 refOf/outRef 供发射器生成 @csharptotypescript/compat 导入。
/// </summary>
public sealed class TsRefOutRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext? _context;
    private readonly TsMemberMappingTable _mappings = new(null, null);

    private static readonly Regex HolderDeclPattern = new(
        @"^io\.github\.ningpp\.compat\.(?<holder>[A-Za-z]+Holder(?:<[^>]+>)?)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*new\s+io\.github\.ningpp\.compat\.(?<init>[A-Za-z]+Holder)(?:<>)?\s*\((?<args>[^()]*)\);?$",
        RegexOptions.Compiled);

    private static readonly Regex ReadBackDeclPattern = new(
        @"^(?<type>\w[\w.<>\[\], ]*?)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?<init>.+\.value)\s*;?$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> NonDeclarationTypeNames = new(StringComparer.Ordinal)
    {
        "var", "let", "const", "final",
    };

    public TsRefOutRewriter(ConversionContext? context = null)
    {
        _context = context;
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Body))
        {
            node.Body = RewriteBody(node.Body);
        }
        return base.VisitMethodDeclaration(node);
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Body))
        {
            node.Body = RewriteBody(node.Body);
        }
        return base.VisitConstructorDeclaration(node);
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        node.Code = RewriteBody(node.Code);
        return base.VisitRawStatement(node);
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var result = base.VisitClassDeclaration(node);
        // JavaSyntaxRewriter 不遍历静态初始化块（TsTypeWriter 直接发射 Statements），
        // 这里补改其中的 ref/out 临时声明（与 TsEnumCallRewriter 同款模式）
        foreach (var block in node.StaticInitializers)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                block.Statements[i] = RewriteBody(block.Statements[i]);
            }
        }
        return result;
    }

    private string RewriteBody(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code;
        }
        var sb = new StringBuilder(code.Length);
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(RewriteLine(lines[i]));
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private string RewriteLine(string line)
    {
        // 跳过字符串/字符字面量区域（Holder 声明/读回语句不含引号内文本误伤）
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '"' || trimmed[0] == '\'')
        {
            return line;
        }

        // ② 调用点 Holder 临时声明
        var holderMatch = HolderDeclPattern.Match(trimmed);
        if (holderMatch.Success)
        {
            var holder = holderMatch.Groups["holder"].Value;
            var name = holderMatch.Groups["name"].Value;
            var args = holderMatch.Groups["args"].Value.Trim();
            var elemTs = GetHolderElementTs(holder);
            string rewritten;
            if (args.Length > 0)
            {
                rewritten = $"const {name} = refOf({args});";
                UseCompat("refOf");
            }
            else
            {
                rewritten = $"const {name} = outRef<{elemTs}>();";
                UseCompat("outRef");
            }
            return line[..(line.Length - trimmed.Length)] + rewritten;
        }

        // ③ out var 读回声明：int r = _rHolder1.value; → let r: number = ...
        var readBack = ReadBackDeclPattern.Match(trimmed);
        if (readBack.Success)
        {
            var type = readBack.Groups["type"].Value.Trim();
            if (!NonDeclarationTypeNames.Contains(type)
                && type.IndexOfAny(new[] { '(', '[', ';' }) < 0
                && !type.StartsWith("io.github.ningpp.compat.", StringComparison.Ordinal))
            {
                var name = readBack.Groups["name"].Value;
                var init = readBack.Groups["init"].Value.Trim();
                var tsType = _mappings.TryMapType(type, out var mapped, out _) ? mapped : type;
                var rewritten = $"let {name}: {tsType} = {init};";
                return line[..(line.Length - trimmed.Length)] + rewritten;
            }
        }

        return line;
    }

    /// <summary>Holder 类型名 → TS 元素类型（IntHolder → number、ObjectHolder&lt;String&gt;
    /// → string；内层经映射表）。</summary>
    private string GetHolderElementTs(string holderType)
    {
        var simple = holderType.StartsWith("io.github.ningpp.compat.", StringComparison.Ordinal)
            ? holderType["io.github.ningpp.compat.".Length..]
            : holderType;
        switch (simple)
        {
            case "IntHolder": return "number";
            case "LongHolder": return "bigint";
            case "BoolHolder": return "boolean";
            case "DoubleHolder":
            case "FloatHolder":
            case "ShortHolder":
            case "ByteHolder": return "number";
            case "CharHolder": return "string";
        }
        if (simple.StartsWith("ObjectHolder<", StringComparison.Ordinal) && simple.EndsWith(">"))
        {
            var inner = simple["ObjectHolder<".Length..^1];
            return _mappings.TryMapType(inner, out var mapped, out _) ? mapped : inner;
        }
        return "any";
    }

    private void UseCompat(string symbol)
    {
        _context?.TsCompatSymbols.Add(symbol);
    }
}