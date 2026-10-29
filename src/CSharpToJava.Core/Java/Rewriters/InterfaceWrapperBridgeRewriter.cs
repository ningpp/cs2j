namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Adds wrapper-typed bridge methods for classes implementing a value-type
/// generic interface such as <c>IRectangle&lt;Double&gt;</c> while declaring the
/// corresponding methods with primitive parameters. Example:
/// <c>contains(double,double)</c> gains
/// <c>contains(Double,double) { return contains(p.doubleValue(), radius); }</c>.
/// </summary>
public sealed class InterfaceWrapperBridgeRewriter : JavaSyntaxRewriter
{
    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        node = base.VisitClassDeclaration(node);
        if (!node.ImplementedTypes.Any(type =>
                type.StartsWith("IRectangle<Double>", StringComparison.Ordinal)))
            return node;

        AddContainsBridge(node, parameterCount: 1, wrapper: "Double");
        AddContainsBridge(node, parameterCount: 2, wrapper: "Double");
        AddSingleArgumentBridge(node, methodName: "add", wrapper: "Double");
        return node;
    }

    private static void AddSingleArgumentBridge(
        JavaClassDeclaration node, string methodName, string wrapper)
    {
        var primitive = node.Methods.FirstOrDefault(method =>
            method.Name == methodName
            && method.Parameters.Count == 1
            && method.Parameters[0].Type == "double");
        if (primitive == null)
            return;
        if (node.Methods.Any(method => method.Name == methodName
            && method.Parameters.Count == 1
            && method.Parameters[0].Type == wrapper))
            return;
        var bridge = new JavaMethodDeclaration
        {
            Name = methodName,
            ReturnType = primitive.ReturnType,
            Modifiers = JavaModifiers.Public
        };
        bridge.Parameters.Add(new JavaParameter(wrapper, "p"));
        bridge.Body = primitive.ReturnType == "void"
            ? $"{methodName}(p.doubleValue());"
            : $"return {methodName}(p.doubleValue());";
        node.Methods.Add(bridge);
    }

    private static void AddContainsBridge(JavaClassDeclaration node, int parameterCount, string wrapper)
    {
        var primitive = node.Methods.FirstOrDefault(method =>
            method.Name == "contains"
            && method.Parameters.Count == parameterCount
            && method.Parameters[0].Type == "double");
        if (primitive == null)
            return;
        var already = node.Methods.Any(method =>
            method.Name == "contains"
            && method.Parameters.Count == parameterCount
            && method.Parameters[0].Type == wrapper);
        if (already)
            return;

        var bridge = new JavaMethodDeclaration
        {
            Name = "contains",
            ReturnType = primitive.ReturnType,
            Modifiers = JavaModifiers.Public
        };
        bridge.Parameters.Add(new JavaParameter(wrapper, "p"));
        if (parameterCount == 2)
            bridge.Parameters.Add(new JavaParameter(primitive.Parameters[1].Type, primitive.Parameters[1].Name));
        var arguments = parameterCount == 2
            ? $"p.doubleValue(), {primitive.Parameters[1].Name}"
            : "p.doubleValue()";
        bridge.Body = primitive.ReturnType == "void"
            ? $"contains({arguments});"
            : $"return contains({arguments});";
        node.Methods.Add(bridge);
    }
}
