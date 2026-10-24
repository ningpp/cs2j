using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// 源内定义类型的大写 System.* 恒等包导入回归：
/// 真实链 dotnet-text-regularexpressions 的 SegmentStringBuilder 定义在
/// <c>namespace System.Text</c>（无 namespaceMappings 条目 → 恒等包
/// <c>package System.Text</c> 发射），跨包引用（dotnet.text.regularexpressions
/// 的 RegexReplacement）需要 <c>import System.Text.SegmentStringBuilder;</c>。
/// 6a48a863 的「大写 System.* 残留单类型导入丢弃」曾把它连同 BCL 残留一起丢掉
/// （mvn 找不到符号 类 SegmentStringBuilder）。判据必须是 source-defined：
/// 源内类型导入保留，BCL 残留（System.Reflection.ParameterModifier）仍丢弃。
/// </summary>
public class SourceDefinedSystemImportTests
{
    // -----------------------------------------------------------------------
    // 发射侧：JavaCompilationUnit.ToString 的丢弃规则
    // -----------------------------------------------------------------------

    [Fact]
    public void EmitImports_SourceDefinedSystemImport_Kept_BclResidueDropped()
    {
        var unit = new JavaCompilationUnit("dotnet.text.regularexpressions");
        unit.TypeDeclarations.Add(new JavaClassDeclaration { Name = "RegexReplacement" });
        unit.Imports.Add(new JavaImport("System.Text.SegmentStringBuilder") { IsSourceDefined = true });
        unit.Imports.Add(new JavaImport("System.Reflection.ParameterModifier"));

        var code = unit.ToString("");

        Assert.Contains("import System.Text.SegmentStringBuilder;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection.ParameterModifier", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitImports_NonSourceSystemImport_StillDropped()
    {
        var unit = new JavaCompilationUnit("dotnet.text.regularexpressions");
        unit.TypeDeclarations.Add(new JavaClassDeclaration { Name = "RegexAssemblyCompiler" });
        unit.Imports.Add(new JavaImport("System.Text.SegmentStringBuilder"));

        var code = unit.ToString("");

        Assert.DoesNotContain("import System.Text.SegmentStringBuilder;", code, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // 登记侧：TypeMappingService 对跨命名空间源内类型导入的 source-defined 登记
    // -----------------------------------------------------------------------

    private const string Source = """
using System;
namespace System.Text
{
    public class SegmentStringBuilder { }
}
namespace A.B
{
    public class Uses
    {
        public System.Text.SegmentStringBuilder Field;
    }
}
""";

    [Fact]
    public void MapType_CrossNamespaceSystemTextType_RegisteredAsSourceDefined()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "sourcedefined-imports",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var model = compilation.GetSemanticModel(tree);
        var fieldDecl = tree.GetRoot().DescendantNodes()
            .OfType<FieldDeclarationSyntax>().Single();
        var fieldSymbol = (IFieldSymbol)model.GetDeclaredSymbol(fieldDecl.Declaration.Variables[0])!;
        var symbol = fieldSymbol.Type;

        var importedTypes = new HashSet<string>();
        var sourceDefinedImports = new HashSet<string>();
        var service = new TypeMappingService(
            new ConversionOptions(),
            new TypeMappingRegistry(new TypeMappingConfig()),
            new DiagnosticCollector(),
            importedTypes,
            () => "A.B",
            () => compilation.GlobalNamespace,
            _ => false,
            sourceDefinedImports: sourceDefinedImports);

        var javaType = service.MapType(symbol);

        Assert.Equal("SegmentStringBuilder", javaType);
        Assert.Contains("System.Text.SegmentStringBuilder", importedTypes);
        Assert.Contains("System.Text.SegmentStringBuilder", sourceDefinedImports);
    }
}
