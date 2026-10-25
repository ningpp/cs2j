using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// 跨命名空间源内类型引用的导入回归（项目管线）：
/// 1) 合并声明成员脱离编译语法树（WithMembers 产物 SyntaxTree 为 dummy），
///    其语义查询一律 ArgumentException → 签名类型退化字符串映射、跨包导入
///    丢失（真实链 dotnet-xml 接口文件 IXmlSerializable/IXsltContextFunction
///    等 ~15 处 javac 找不到符号）。修复：InterfaceTransformer.TransformMerged
///    遍历 OriginalSyntaxNodes（与 ClassTransformer 同策略）。
/// 2) 语义绑定产出 error 符号时（CandidateSymbols 为空的合并声明成员），
///    error 分支此前的源内类型回退藏在 simpleConfigKey != null 门内，未映射
///    工程类型退化为裸简名且被 TypeCache 固化。修复：TryResolveProjectTypeBySimpleName
///    上提到 error 分支顶部（源内类型遮蔽 BCL 同名类型，与 C# 名称解析一致）。
/// 本测试覆盖 2)（TypeMappingService 层）；1) 由真实链最小复现验证。
/// </summary>
public class InterfaceMemberImportTests
{
    private const string Source = """
namespace Other.Ns
{
    public class Widget { }
}
namespace P.Q
{
    public class C
    {
        // no `using Other.Ns;` — Widget binds to an error type (CS0246)
        public Widget Field;
    }
}
""";

    [Fact]
    public void ErrorTypeReference_SourceTypeInOtherNamespace_ResolvesWithImport()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "iface-member-imports",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = compilation.GetSemanticModel(tree);
        var fieldDecl = tree.GetRoot().DescendantNodes()
            .OfType<FieldDeclarationSyntax>().Single();
        var fieldSymbol = (IFieldSymbol)model.GetDeclaredSymbol(fieldDecl.Declaration.Variables[0])!;
        var symbol = fieldSymbol.Type;
        Assert.True(symbol is IErrorTypeSymbol, "test setup: Widget should bind as error type without the using directive");

        var importedTypes = new HashSet<string>();
        var sourceDefinedImports = new HashSet<string>();
        var service = new TypeMappingService(
            new ConversionOptions(),
            new TypeMappingRegistry(new TypeMappingConfig()),
            new DiagnosticCollector(),
            importedTypes,
            () => "P.Q",
            () => compilation.GlobalNamespace,
            _ => false,
            sourceDefinedImports: sourceDefinedImports);

        var javaType = service.MapType(symbol);

        Assert.Equal("Widget", javaType);
        Assert.Contains("Other.Ns.Widget", importedTypes);
        Assert.Contains("Other.Ns.Widget", sourceDefinedImports);
    }
}
