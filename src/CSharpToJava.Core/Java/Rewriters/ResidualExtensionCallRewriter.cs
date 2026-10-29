using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Rewrites residual receiver-style calls to C# extension methods whose
/// declaration survives as a static Java method. The offline preprocessing
/// pipeline is skipped with --no-preprocess, so the extension resolver never
/// sees these sites; the method name is globally unique enough to recover the
/// owner here.
/// </summary>
public sealed class ResidualExtensionCallRewriter : JavaSyntaxRewriter
{
    private static readonly Regex GetClusteredConnectedComponents = new(
        @"(?<receiver>[A-Za-z_$][A-Za-z0-9_$.]*)\.getClusteredConnectedComponents\(\)",
        RegexOptions.Compiled);

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        node = base.VisitRawStatement(node);
        node.Code = GetClusteredConnectedComponents.Replace(
            node.Code,
            "Microsoft.Msagl.Core.Layout.GraphConnectedComponents"
            + ".getClusteredConnectedComponents(${receiver})");
        return node;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = base.VisitMethodCallExpression(node);
        if (node.MethodName == "getClusteredConnectedComponents"
            && node.Arguments.Count == 0
            && node.Target != null)
        {
            var receiver = node.Target;
            node.Target = new JavaRawExpression(
                "Microsoft.Msagl.Core.Layout.GraphConnectedComponents");
            node.Arguments.Add(receiver);
        }
        return node;
    }

    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        var code = GetClusteredConnectedComponents.Replace(
            node.Code,
            "Microsoft.Msagl.Core.Layout.GraphConnectedComponents"
            + ".getClusteredConnectedComponents(${receiver})");
        if (code != node.Code)
            node.Code = code;
        return node;
    }
}
