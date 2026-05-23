using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Adds null-diagonal guard to initActiveDiagonals() method body, matching the
/// pattern already used in sweep(). When firstTangent.Diagonal is null but
/// the tangent is Low, a new Diagonal is created before accessing .RbNode.
/// </summary>
public sealed class InitActiveDiagonalsNullGuardRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;
    public int RewriteCount => _rewriteCount;

    // Match the two vulnerable if-blocks that access firstTangent.Diagonal without null check.
    private static readonly Regex _vulnerablePattern = new(
        @"( *)if\s*\(\s*firstTangent\.getDiagonal\(\)\s*\.getRbNode\(\)\s*==\s*this\.activeDiagonalTree\.treeMinimum\(\)\s*\)\s*\{\s*\n" +
        @"\1\s*addVisibleEdge\(firstTangent\);\s*\n" +
        @"\1\}\s*\n" +
        @"\1if\s*\(\s*firstTangent\.getIsLow\(\)\s*==\s*false\s*\)\s*\{\s*\n" +
        @"\1\s*Diagonal\s+diag\s*=\s*firstTangent\.getDiagonal\(\);\s*\n" +
        @"\1\s*removeDiagonalFromActiveNodes\(diag\);\s*\n" +
        @"\1\}",
        RegexOptions.Compiled);

    private static readonly string _replacement =
        @"$1if (firstTangent.getDiagonal() != null) {
        $1    if (firstTangent.getDiagonal().getRbNode() == this.activeDiagonalTree.treeMinimum()) {
        $1        addVisibleEdge(firstTangent);
        $1    }
        $1    if (firstTangent.getIsLow() == false) {
        $1        Diagonal diag = firstTangent.getDiagonal();
        $1        removeDiagonalFromActiveNodes(diag);
        $1    }
        $1} else if (firstTangent.getIsLow()) {
        $1    this.activeDiagonalComparer.setPointOnTangentAndInsertedDiagonal(firstTangent.getEnd().getPoint());
        $1    this.insertActiveDiagonal(new Diagonal(firstTangent, firstTangent.getComp()));
        $1    if (firstTangent.getDiagonal().getRbNode() == this.activeDiagonalTree.treeMinimum()) {
        $1        addVisibleEdge(firstTangent);
        $1    }
        $1}";

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        node = (JavaMethodDeclaration)base.VisitMethodDeclaration(node);

        if (node.Name != "initActiveDiagonals")
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

        if (body.Contains("firstTangent.getDiagonal() != null"))
            return node;

        var newBody = _vulnerablePattern.Replace(body, _replacement);
        if (newBody != body)
        {
            node.Body = newBody;
            node.IsBodyExpression = false;
            _rewriteCount++;
        }

        return node;
    }
}
