using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Fixes potential infinite loop in tangentBetweenBranches() by guarding no-op
/// assignments. When an assignment like p0 = mp doesn't actually change p0
/// (because p0 already equals mp), the loop would spin forever with moveOnP
/// still true. The fix wraps each standalone assignment to set moveOnP/Q = false
/// when the assignment would be a no-op.
///
/// Double-assignments like "p1 = p0 = mp;" (forced convergence) are left unchanged.
/// </summary>
public sealed class TangentBetweenBranchesNoopGuardRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;
    public int RewriteCount => _rewriteCount;

    // Matches standalone single assignments: "        p0 = mp;" etc.
    // Captures: $1=indent, $2=var, $3=value
    private static readonly Regex _singleAssign = new(
        @"^(\s+)(p0|p1|q0|q1)\s*=\s*(mp|mq|p1|p0|q1|q0)\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        node = (JavaMethodDeclaration)base.VisitMethodDeclaration(node);

        if (node.Name != "tangentBetweenBranches")
            return node;

        string? body = null;
        if (node.StructuredBody != null)
        {
            body = node.StructuredBody.ToString("        ");
            node.StructuredBody = null;
        }
        else if (node.Body != null)
        {
            body = node.Body;
        }
        if (body == null) return node;

        // Already fixed
        if (body.Contains("if (p0 != mp)") || body.Contains("if (p1 != mp)"))
            return node;

        var newBody = _singleAssign.Replace(body, m =>
        {
            var indent = m.Groups[1].Value;
            var variable = m.Groups[2].Value;
            var value = m.Groups[3].Value;
            var flag = (variable == "p0" || variable == "p1") ? "moveOnP" : "moveOnQ";
            return $"{indent}if ({variable} != {value}) {{ {variable} = {value}; }} else {{ {flag} = false; }}";
        });

        if (newBody != body)
        {
            node.Body = newBody;
            node.IsBodyExpression = false;
            _rewriteCount++;
        }

        return node;
    }
}
