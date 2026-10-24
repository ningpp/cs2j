using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// SpecialType decimal 的 compat 导入登记回归：
/// decimal 的 FullyQualifiedFormat 渲染为 C# 关键字 "decimal"，语义映射的
/// configKeySimple 探测不到 System.Decimal 的类型级映射（带
/// io.github.ningpp.compat.Decimal 导入），方法返回类型 decimal → Decimal
/// 曾不登记 compat 导入（真实链 dotnet-xml XmlAsyncCheckReader/Writer
/// 6 处「找不到符号 类 Decimal」，mvn 拒编）。导入登记必须与命名空间门
/// 一样做 "System."+名字 兜底。
/// </summary>
public class DecimalReturnTypeImportTests
{
    private const string Source = """
namespace demo
{
    public class C
    {
        public decimal M() => 1m;
    }
}
""";

    [Fact]
    public void MapType_DecimalReturnType_RegistersCompatDecimalImport()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "decimal-return-import",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = compilation.GetSemanticModel(tree);
        var methodDecl = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single();
        var symbol = ((IMethodSymbol)model.GetDeclaredSymbol(methodDecl)!).ReturnType;

        var importedTypes = new HashSet<string>();
        var service = new TypeMappingService(
            new ConversionOptions(),
            new TypeMappingRegistry(FindRepoTypeMappingsJson()),
            new DiagnosticCollector(),
            importedTypes,
            () => "demo",
            () => compilation.GlobalNamespace,
            _ => false);

        var javaType = service.MapType(symbol);

        Assert.Equal("Decimal", javaType);
        Assert.Contains("io.github.ningpp.compat.Decimal", importedTypes);
    }

    private static string FindRepoTypeMappingsJson()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
               && !File.Exists(Path.Combine(dir, "config", "TypeMappings.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir!, "config", "TypeMappings.json");
    }
}
