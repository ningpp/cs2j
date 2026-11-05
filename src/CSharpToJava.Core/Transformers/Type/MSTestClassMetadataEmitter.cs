using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// MSTest 运行时特性映射：
/// <list type="bullet">
/// <item><c>[DeploymentItem(source, output)]</c> → compat
/// <c>@MSTestDeploymentItem(source = ..., outputDirectory = ...)</c>，由
/// <c>MSTestExtension</c> 在 beforeAll 时把资源复制到 TestContext.DeploymentDirectory。
/// 生成测试的 <c>getDeploymentDirectory() + "Dgml"</c> 因此能找到真实数据
/// （此前特性被丢弃，DgmlParserTests 7 例均 FileNotFoundException）。</item>
/// <item>需要 MSTestExtension 的类（DeploymentItem / ClassInitialize 注入 /
/// ExpectedException 等）补 <c>@ExtendWith(MSTestExtension.class)</c>。</item>
/// </list>
/// </summary>
internal static class MSTestClassMetadataEmitter
{
    private const string DeploymentItemMetadataName =
        "Microsoft.VisualStudio.TestTools.UnitTesting.DeploymentItemAttribute";
    private const string MstestExtensionFqn =
        "Microsoft.VisualStudio.TestTools.UnitTesting.MSTestExtension";

    public static void Emit(
        JavaClassDeclaration javaClass,
        INamedTypeSymbol? symbol,
        IReadOnlyList<ClassDeclarationSyntax> nodes,
        ConversionContext context)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        if (symbol != null)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != DeploymentItemMetadataName)
                    continue;

                var source = attribute.ConstructorArguments.Length > 0
                    ? attribute.ConstructorArguments[0].Value as string
                    : null;
                var output = attribute.ConstructorArguments.Length > 1
                    ? attribute.ConstructorArguments[1].Value as string
                    : null;
                source ??= attribute.NamedArguments
                    .FirstOrDefault(a => a.Key == "Item").Value.Value as string;
                output ??= attribute.NamedArguments
                    .FirstOrDefault(a => a.Key == "OutputDirectory").Value.Value as string;
                EmitDeploymentItem(javaClass, source, output, emitted, context);
            }
        }

        // 预处理编译不一定引用 MSTest 包：属性符号可能绑定为 error type，
        // 此时退回语法短名扫描（真实链 DgmlParserTests 的类级
        // [DeploymentItem("Resources/Dgml", "Dgml")]）。
        foreach (var node in nodes)
        {
            foreach (var attribute in node.AttributeLists.SelectMany(list => list.Attributes))
            {
                if (!IsDeploymentItemName(attribute.Name.ToString()))
                    continue;

                var arguments = attribute.ArgumentList?.Arguments ?? default;
                var source = arguments.Count > 0 ? StringLiteral(arguments[0].Expression) : null;
                var output = arguments.Count > 1 ? StringLiteral(arguments[1].Expression) : null;
                foreach (var named in arguments)
                {
                    var name = named.NameEquals?.Name.Identifier.Text
                               ?? named.NameColon?.Name.Identifier.Text;
                    if (name == "Item") source ??= StringLiteral(named.Expression);
                    if (name == "OutputDirectory") output ??= StringLiteral(named.Expression);
                }
                EmitDeploymentItem(javaClass, source, output, emitted, context);
            }
        }

        if (!context.CurrentClassNeedsMSTestExtension)
            return;

        // 注解本体与扩展类都必须导入：只导入 MSTestExtension 会生成
        // @ExtendWith(...) 却找不到 ExtendWith 类（javac: 找不到符号）。
        context.AddImport("org.junit.jupiter.api.extension.ExtendWith");
        if (javaClass.Annotations.Any(a => a.Name.StartsWith("ExtendWith(", StringComparison.Ordinal)))
            return;

        context.AddImport(MstestExtensionFqn);
        javaClass.Annotations.Add(new JavaAnnotation("ExtendWith(MSTestExtension.class)"));
    }

    private static void EmitDeploymentItem(
        JavaClassDeclaration javaClass,
        string? source,
        string? output,
        HashSet<string> emitted,
        ConversionContext context)
    {
        if (string.IsNullOrEmpty(source))
            return;

        var annotation =
            $"MSTestDeploymentItem(source = \"{StringEscapeHelper.EscapeJavaString(source)}\", "
            + $"outputDirectory = \"{StringEscapeHelper.EscapeJavaString(output ?? string.Empty)}\")";
        if (!emitted.Add(annotation))
            return;

        context.AddImport("Microsoft.VisualStudio.TestTools.UnitTesting.MSTestDeploymentItem");
        javaClass.Annotations.Add(new JavaAnnotation(annotation));
        context.CurrentClassNeedsMSTestExtension = true;
    }

    private static bool IsDeploymentItemName(string rawName)
    {
        var name = rawName.Trim();
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0) name = name[(lastDot + 1)..];
        if (name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
            name = name[..^9];
        return name.Equals("DeploymentItem", StringComparison.Ordinal);
    }

    private static string? StringLiteral(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax literal
           && literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;
}
