namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Prevents infinite loop in findClosestPoints() by adding a maximum iteration
/// counter. The shrinkChunks algorithm can fail to converge due to floating-point
/// differences between C# and Java in angle/orientation computations.
/// After 200 iterations the loop is forcibly exited to prevent hanging.
/// </summary>
public sealed class FindClosestPointsMaxIterationRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;
    public int RewriteCount => _rewriteCount;

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        node = (JavaMethodDeclaration)base.VisitMethodDeclaration(node);

        if (node.Name != "findClosestPoints")
            return node;

        if (node.StructuredBody == null && node.Body == null) return node;

        // Only handle the 6-parameter overload (internal) — it has the while loop.
        bool hasWhile = (node.Body != null && node.Body.Contains("while (chunksAreLong"))
                      || (node.StructuredBody != null && node.StructuredBody.ToString("        ").Contains("while (chunksAreLong"));
        if (!hasWhile)
            return node;

        string body;
        if (node.StructuredBody != null)
        {
            body = node.StructuredBody.ToString("        ");
            node.StructuredBody = null;
        }
        else
        {
            body = node.Body!;
        }

        if (body.Contains("_fcpIter"))
            return node;

        body = body.Replace(
            "while (chunksAreLong",
            "int _fcpIter = 0;\n            while (chunksAreLong");

        body = body.Replace(
            "shrinkChunks(p2, p1, q2, q1);",
            "if (++_fcpIter > 200) break;\n            shrinkChunks(p2, p1, q2, q1);");

        node.Body = body;
        node.IsBodyExpression = false;
        _rewriteCount++;
        return node;
    }
}
