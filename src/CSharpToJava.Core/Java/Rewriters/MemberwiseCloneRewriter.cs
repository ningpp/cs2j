using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that replaces calls to <c>memberwiseClone()</c> (from C#'s <c>MemberwiseClone()</c>)
/// with the compat helper <c>ObjectHelper.__memberwiseClone(target)</c>.
///
/// <para>Java has no intrinsic shallow-copy on arbitrary classes: the class's own
/// <c>clone()</c> may be the caller (recursion) and may not exist on the receiver's
/// static type. The compat helper performs a reflective field-wise shallow copy,
/// matching C# unchecked-exception semantics (no checked CloneNotSupportedException,
/// so the previous try/catch body wrap is no longer needed).</para>
///
/// <para>Also fixes raw statements (<c>var x = this.memberwiseClone();</c>) whose
/// call text never materialized as a <see cref="JavaMethodCallExpression"/> node.</para>
/// </summary>
public sealed class MemberwiseCloneRewriter : JavaSyntaxRewriter
{
    private const string HelperImport = "io.github.ningpp.compat.ObjectHelper";
    private const string HelperTarget = "ObjectHelper";
    private const string HelperMethod = "__memberwiseClone";

    private static readonly Regex RawCallPattern = new(
        @"(?<target>(?:[A-Za-z_$][A-Za-z0-9_$]*\.)+)?memberwiseClone\s*\(\)",
        RegexOptions.Compiled);

    private int _rewriteCount;
    private bool _needsHelperImport;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        _needsHelperImport = false;
        var result = base.VisitCompilationUnit(node);
        if (_needsHelperImport
            && result.Imports.All(i => !string.Equals(i.Name, HelperImport, StringComparison.Ordinal)))
        {
            result.Imports.Add(new JavaImport(HelperImport));
        }
        return result;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        if (node.MethodName is "memberwiseClone" or "MemberwiseClone"
            && node.Arguments.Count == 0
            && (node.Target == null || node.Target is JavaThisExpression))
        {
            node.Target = new JavaRawExpression(HelperTarget);
            node.MethodName = HelperMethod;
            node.Arguments.Add(new JavaThisExpression());
            _needsHelperImport = true;
            _rewriteCount++;
        }

        return node;
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (string.IsNullOrEmpty(node.Code)
            || (!node.Code.Contains("memberwiseClone", StringComparison.Ordinal)
                && !node.Code.Contains("MemberwiseClone", StringComparison.Ordinal)))
        {
            return node;
        }

        var rewritten = RawCallPattern.Replace(node.Code, match =>
        {
            _needsHelperImport = true;
            _rewriteCount++;
            var target = match.Groups["target"].Success
                ? match.Groups["target"].Value[..^1] // strip the trailing dot
                : "this";
            return $"{HelperTarget}.{HelperMethod}({target})";
        });

        if (rewritten != node.Code)
            node.Code = rewritten;
        return node;
    }

    /// <summary>
    /// 强转/复杂表达式语境下的调用以 RawExpression 落地（真实链
    /// <c>(XmlSchemaAttribute)(memberwiseClone())</c>——cast 表达式整体走 raw 文本，
    /// 既不是 <see cref="JavaMethodCallExpression"/> 也不在 RawStatement 里），
    /// 与语句位同规则改写，否则 javac 找不到符号 方法 memberwiseClone()。
    /// </summary>
    public override JavaExpression VisitExpression(JavaExpression node)
    {
        node = base.VisitExpression(node);
        if (node is JavaRawExpression raw
            && !string.IsNullOrEmpty(raw.Code)
            && (raw.Code.Contains("memberwiseClone", StringComparison.Ordinal)
                || raw.Code.Contains("MemberwiseClone", StringComparison.Ordinal)))
        {
            var rewritten = RawCallPattern.Replace(raw.Code, match =>
            {
                _needsHelperImport = true;
                _rewriteCount++;
                var target = match.Groups["target"].Success
                    ? match.Groups["target"].Value[..^1]
                    : "this";
                return $"{HelperTarget}.{HelperMethod}({target})";
            });
            if (rewritten != raw.Code)
                raw.Code = rewritten;
        }
        return node;
    }
}
